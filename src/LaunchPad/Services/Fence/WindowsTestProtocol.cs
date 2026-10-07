using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LaunchPad.Services.Fence;

public sealed record WindowsTestRequest(int Version, string Generation, string RequestId, string Tool,
    string? Program, string WorkingDirectory, string[] Arguments, bool Interactive, int TimeoutSeconds,
    string[] Artifacts, ReturnedContent[] Files);
public sealed record WindowsTestResponse(int Version, string Generation, string RequestId, string Outcome,
    int? ExitCode, string? Error, bool LogsTruncated, ReturnedContent[] Files);
public sealed record WindowsTestEnvelope(WindowsTestRequest Request, string MetadataSha256);

// Dedicated bulk channel, never the AUTH/HOME/status stream. All host paths are
// derived by the receiver; the guest supplies only relative snapshot paths.
public static class WindowsTestProtocol
{
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    public const long MaximumSnapshotBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumSnapshotFiles = 20000;
    public const long MaximumResultBytes = 32L * 1024 * 1024;
    public const int MaximumResultFiles = 130;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<WindowsTestEnvelope> ReadRequestAsync(Stream input, string generation, CancellationToken token,
        Func<ReadOnlyMemory<byte>, bool>? authenticate = null)
    {
        var bytes = await ReadMetadataAsync(input, "LP-WINDOWS-TEST 1 ", token).ConfigureAwait(false);
        if (authenticate is not null && !authenticate(bytes)) throw new InvalidDataException("Windows test request authentication failed.");
        var request = JsonSerializer.Deserialize<WindowsTestRequest>(bytes, Json) ?? throw new InvalidDataException("Empty Windows test request.");
        Validate(request, generation);
        return new(request, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public static void Validate(WindowsTestRequest value, string generation)
    {
        if (value.Version != 1 || value.Generation != generation || !Guid.TryParseExact(generation, "N", out _)
            || !Guid.TryParseExact(value.RequestId, "N", out _)) throw new InvalidDataException("Wrong Windows test session or request identity.");
        if (value.Tool is not ("project" or "dotnet" or "node" or "python" or "powershell"))
            throw new InvalidDataException("Unsupported Windows test tool.");
        if (value.Tool == "project" ? !SafePath(value.Program) || !value.Program!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            : value.Program is not null) throw new InvalidDataException("Use a snapshot .exe or a named installed Windows tool.");
        if (value.WorkingDirectory != "." && !SafePath(value.WorkingDirectory)) throw new InvalidDataException("Unsafe Windows test working directory.");
        if (value.TimeoutSeconds is < 1 or > 7200 || value.Arguments is null || value.Arguments.Length > 128
            || value.Arguments.Any(argument => argument is null || argument.Length > 8192 || argument.Contains('\0'))
            || value.Arguments.Sum(argument => (long)argument.Length) > 32768) throw new InvalidDataException("Invalid Windows test duration or arguments.");
        if (value.Artifacts is null || value.Artifacts.Length > 128 || value.Artifacts.Any(path => !SafePath(path) || path.Length > 220)
            || value.Artifacts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Artifacts.Length)
            throw new InvalidDataException("Unsafe or duplicate Windows test artifact.");
        ValidateFiles(value.Files, MaximumSnapshotFiles, MaximumSnapshotBytes);
        if (value.Tool == "project" && !value.Files.Any(file => file.Path.Equals(value.Program, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The requested executable is absent from the snapshot.");
    }

    internal static bool SafePath(string? path) => path is { Length: > 0 and <= 240 } && FenceFiles.IsSafe(path)
        && !path.Split('/')[0].Equals(".launchpad-test", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateFiles(ReturnedContent[]? files, int maximumFiles, long maximumBytes)
    {
        if (files is null || files.Length > maximumFiles) throw new InvalidDataException("Too many Windows test files.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null || !SafePath(file.Path) || file.Size < 0 || file.Size > maximumBytes - total
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit) || !seen.Add(file.Path))
                throw new InvalidDataException("Unsafe, duplicate or oversized Windows test file.");
            total += file.Size;
        }
        // A directory/file collision must fail before any payload is staged.
        foreach (var path in seen)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (seen.Contains(path[..slash])) throw new InvalidDataException("Windows test file/directory collision.");
    }

    public static async Task ReceiveFilesAsync(Stream input, ReturnedContent[] files, string? destination, CancellationToken token)
    {
        var buffer = new byte[65536];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream? output = null;
            try
            {
                if (destination is not null)
                {
                    if (!FenceFiles.TryResolveUnlinked(destination, file.Path, out var path)) throw new InvalidDataException("Linked Windows test destination.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (!FenceFiles.TryResolveUnlinked(destination, file.Path, out path)) throw new InvalidDataException("Linked Windows test destination.");
                    output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                for (var left = file.Size; left > 0;)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("Truncated Windows test file.");
                    digest.AppendData(buffer, 0, count);
                    if (output is not null) await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    left -= count;
                }
                output?.Flush(true);
                if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Windows test file hash mismatch.");
            }
            finally { output?.Dispose(); }
        }
        if (await ReadLineAsync(input, token).ConfigureAwait(false) != "LP-WINDOWS-FILES-END")
            throw new InvalidDataException("Windows test snapshot completion marker is missing.");
    }

    public static async Task WriteRequestAsync(Stream output, WindowsTestRequest request, string snapshot, CancellationToken token)
    {
        Validate(request, request.Generation);
        await WriteMetadataAsync(output, "LP-WINDOWS-TEST 1 ", request, token).ConfigureAwait(false);
        await SendFilesAsync(output, request.Files, snapshot, token).ConfigureAwait(false);
    }

    public static async Task WriteResponseAsync(Stream output, WindowsTestResponse response, string resultDirectory, CancellationToken token,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? protectMetadata = null)
    {
        ValidateFiles(response.Files, MaximumResultFiles, MaximumResultBytes);
        await WriteMetadataAsync(output, "LP-WINDOWS-RESULT 1 ", response, token, protectMetadata).ConfigureAwait(false);
        await SendFilesAsync(output, response.Files, resultDirectory, token).ConfigureAwait(false);
    }

    public static async Task<WindowsTestResponse> ReadResponseAsync(Stream input, string generation, string requestId,
        string destination, CancellationToken token)
    {
        var bytes = await ReadMetadataAsync(input, "LP-WINDOWS-RESULT 1 ", token).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<WindowsTestResponse>(bytes, Json) ?? throw new InvalidDataException("Empty Windows test result.");
        if (response.Version != 1 || response.Generation != generation || response.RequestId != requestId
            || response.Outcome is not ("finished" or "failed" or "canceled" or "timed-out" or "interrupted")
            || response.Outcome == "finished" && response.ExitCode is null)
            throw new InvalidDataException("Wrong Windows test result identity or outcome.");
        ValidateFiles(response.Files, MaximumResultFiles, MaximumResultBytes);
        await ReceiveFilesAsync(input, response.Files, destination, token).ConfigureAwait(false);
        return response;
    }

    private static async Task SendFilesAsync(Stream output, ReturnedContent[] files, string root, CancellationToken token)
    {
        var buffer = new byte[65536];
        foreach (var file in files)
        {
            if (!FenceFiles.TryResolveUnlinked(root, file.Path, out var path)) throw new InvalidDataException("Linked Windows test source.");
            using var input = OpenResultSource(path);
            if (input.Length != file.Size) throw new InvalidDataException("Windows test file changed before send.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var left = file.Size; left > 0;)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Windows test file changed during send.");
                digest.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                left -= count;
            }
            if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows test file changed during send.");
        }
        await output.WriteAsync(Utf8.GetBytes("LP-WINDOWS-FILES-END\n"), token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static FileStream OpenResultSource(string path)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Check the opened handle, not just the pre-open path. A
                // test-created junction/hardlink must not read other host data.
                var final = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(input.SafeFileHandle, final, (uint)final.Capacity, 0);
                if (length == 0 || length >= final.Capacity || !GetFileInformationByHandle(input.SafeFileHandle, out var info)
                    || info.NumberOfLinks != 1 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked Windows test result.");
                var actual = final.ToString();
                if (actual.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) actual = "\\\\" + actual[8..];
                else if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
                if (!actual.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Windows test result resolved outside its owned path.");
            }
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    private static async Task<byte[]> ReadMetadataAsync(Stream input, string prefix, CancellationToken token)
    {
        var line = await ReadLineAsync(input, token).ConfigureAwait(false);
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(line.AsSpan(prefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out var size) || size is < 2 or > MaximumMetadataBytes)
            throw new InvalidDataException("Invalid Windows test metadata frame.");
        var bytes = new byte[size];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteMetadataAsync<T>(Stream output, string prefix, T value, CancellationToken token,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? protectMetadata = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Windows test metadata is too large.");
        if (protectMetadata is not null) await protectMetadata(bytes, token).ConfigureAwait(false);
        await output.WriteAsync(Utf8.GetBytes(prefix + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n"), token).ConfigureAwait(false);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(Stream input, CancellationToken token)
    {
        var buffer = new byte[128];
        var count = 0;
        while (count < buffer.Length)
        {
            await input.ReadExactlyAsync(buffer.AsMemory(count, 1), token).ConfigureAwait(false);
            if (buffer[count] == '\n') return Utf8.GetString(buffer, 0, count);
            count++;
        }
        throw new InvalidDataException("Windows test frame header is too long.");
    }
}
