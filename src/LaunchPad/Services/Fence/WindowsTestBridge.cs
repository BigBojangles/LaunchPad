using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record WindowsTestExecution(string Outcome, int? ExitCode, string? Error = null,
    bool LogsTruncated = false, string? StandardOutput = null, string? StandardError = null);

public interface IWindowsTestExecutor
{
    Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, CancellationToken token);
}

public interface IControlledWindowsTestExecutor : IWindowsTestExecutor
{
    Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, string metadataSha256, CancellationToken token);
    void RecordResult(WindowsTestResponse response);
}

// Host-owned ledger and frozen response. A recorded start is never replayed;
// loss of a result requires recovery instead of another automatic launch.
public sealed class WindowsTestBridge
{
    private sealed record Ledger(string Generation, string RequestId, string MetadataSha256, string Stage,
        WindowsTestResponse? Response = null);
    private readonly string _root, _generation;
    private readonly IWindowsTestExecutor _executor;

    public WindowsTestBridge(string managedRunsRoot, string generation, IWindowsTestExecutor executor)
    {
        if (!Guid.TryParseExact(generation, "N", out _)) throw new ArgumentException("Invalid Windows test generation.");
        _root = Path.GetFullPath(managedRunsRoot);
        _generation = generation;
        _executor = executor;
    }

    public async Task<WindowsTestResponse> ProcessAsync(Stream input, Stream output, CancellationToken token = default,
        Func<ReadOnlyMemory<byte>, bool>? authenticate = null,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? protectResponse = null)
    {
        var envelope = await WindowsTestProtocol.ReadRequestAsync(input, _generation, token, authenticate).ConfigureAwait(false);
        var request = envelope.Request;
        if (!FenceFiles.TryResolveUnlinked(_root, _generation + "/" + request.RequestId, out var run))
            throw new InvalidDataException("Linked Windows test ledger location.");
        Directory.CreateDirectory(run);
        using var claim = new FileStream(Path.Combine(run, "claim.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var ledgerPath = Path.Combine(run, "ledger.json");
        var workspace = Path.Combine(run, "workspace");
        var results = Path.Combine(run, "results");
        WindowsTestResponse response;
        if (File.Exists(ledgerPath))
        {
            using var saved = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (saved.Length > WindowsTestProtocol.MaximumMetadataBytes) throw new InvalidDataException("Invalid Windows test ledger.");
            var ledger = JsonSerializer.Deserialize<Ledger>(saved, WindowsTestProtocol.Json) ?? throw new InvalidDataException("Invalid Windows test ledger.");
            if (ledger.Generation != _generation || ledger.RequestId != request.RequestId || ledger.MetadataSha256 != envelope.MetadataSha256)
                throw new InvalidDataException("This Windows test request ID already belongs to different content.");
            // Consume and verify the resubmitted snapshot without overwriting the
            // retained working copy. It may contain deliberate test changes.
            await WindowsTestProtocol.ReceiveFilesAsync(input, request.Files, null, token).ConfigureAwait(false);
            response = ledger.Response ?? new(1, _generation, request.RequestId, "interrupted", null,
                "An earlier Windows test did not retain a final result. Its copy is preserved; it will not be started again automatically.", false, []);
            RecordView(response);
            await WindowsTestProtocol.WriteResponseAsync(output, response, results, token, protectResponse).ConfigureAwait(false);
            return response;
        }

        Save("receiving");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(results);
        try
        {
            await WindowsTestProtocol.ReceiveFilesAsync(input, request.Files, workspace, token).ConfigureAwait(false);
            ReturnRecovery.SaveAtomic(Path.Combine(run, "request.json"), request);
            token.ThrowIfCancellationRequested();
            // Flushed host-owned intent precedes the call which can start a
            // process. A crash in that gap is ambiguous and never auto-replayed.
            Save("starting");
            var execution = _executor is IControlledWindowsTestExecutor controlled
                ? await controlled.ExecuteAsync(request, workspace, envelope.MetadataSha256, token).ConfigureAwait(false)
                : await _executor.ExecuteAsync(request, workspace, token).ConfigureAwait(false);
            if (execution.Outcome is not ("finished" or "failed" or "canceled" or "timed-out")
                || execution.Outcome == "finished" && execution.ExitCode is null)
                throw new InvalidDataException("Windows executor returned an invalid outcome.");
            var (files, collectionError) = FreezeResults(workspace, results, request, execution);
            response = new(1, _generation, request.RequestId, collectionError is not null && execution.Outcome == "finished" ? "failed" : execution.Outcome, execution.ExitCode,
                execution.Error is { Length: > 0 } primary
                    ? collectionError is null ? primary : primary + "\nResult collection warning: " + collectionError
                    : collectionError, execution.LogsTruncated, files);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
            or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            response = new(1, _generation, request.RequestId, error is OperationCanceledException ? "canceled" : "failed",
                null, error.Message, false, []);
        }
        Save("finished", response);
        RecordView(response);
        await WindowsTestProtocol.WriteResponseAsync(output, response, results, token, protectResponse).ConfigureAwait(false);
        return response;

        void Save(string stage, WindowsTestResponse? result = null) => ReturnRecovery.SaveAtomic(ledgerPath,
            new Ledger(_generation, request.RequestId, envelope.MetadataSha256, stage, result));

        void RecordView(WindowsTestResponse result)
        {
            if (_executor is not IControlledWindowsTestExecutor observer) return;
            try { observer.RecordResult(result); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // UI projection cannot prevent delivery of a durable result.
                try { ReturnRecovery.SaveAtomic(Path.Combine(run, "control-view-error.json"), new { error = error.Message, recordedUtc = DateTime.UtcNow }); }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static (ReturnedContent[] Files, string? Error) FreezeResults(string workspace, string results, WindowsTestRequest request, WindowsTestExecution execution)
    {
        var entries = new List<ReturnedContent>();
        long total = 0;
        string? collectionError = null;
        if (execution.StandardOutput is not null) Copy(execution.StandardOutput, "logs/stdout.txt", 1024 * 1024);
        if (execution.StandardError is not null) Copy(execution.StandardError, "logs/stderr.txt", 1024 * 1024);
        foreach (var artifact in request.Artifacts) Copy(artifact, "artifacts/" + artifact, WindowsTestProtocol.MaximumResultBytes);
        return (entries.ToArray(), collectionError);

        void Copy(string source, string target, long limit)
        {
            try { CopyFile(source, target, limit); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { collectionError ??= "A requested Windows test log/artifact could not be returned: " + error.Message; }
        }

        void CopyFile(string source, string target, long limit)
        {
            // Executor and requested artifact paths are still untrusted data.
            if (!FenceFiles.TryResolveUnlinked(workspace, source, out var path)
                || !FenceFiles.TryResolveUnlinked(results, target, out var destination))
                throw new InvalidDataException("Linked or unsafe Windows test result.");
            using var input = WindowsTestProtocol.OpenResultSource(path);
            if (input.Length > limit || input.Length > WindowsTestProtocol.MaximumResultBytes - total)
                throw new InvalidDataException("Windows test result exceeds its size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (count > limit - length || count > WindowsTestProtocol.MaximumResultBytes - total - length)
                    throw new InvalidDataException("Windows test result grew beyond its size limit.");
                output.Write(buffer, 0, count);
                digest.AppendData(buffer, 0, count);
                length += count;
            }
            output.Flush(true);
            total += length;
            entries.Add(new(target, length, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()));
        }
    }
}
