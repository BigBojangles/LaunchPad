using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public interface IWindowsTestDesktopControl
{
    void Activate();
    bool ReturnToLaunchPad();
}

public sealed record WindowsTestControlState(int Version, string Generation, string RequestId, string MetadataSha256,
    string InstanceId, int OwnerPid, long OwnerStartTicks, string Stage, string Tool, string? Program,
    string[] Arguments, bool Interactive, string WorkingDirectory, int TimeoutSeconds, DateTimeOffset? ApprovalDeadline, bool DesktopAvailable,
    string? Outcome, int? ExitCode, string? Error, string? AcknowledgedCommand, DateTimeOffset RecordedUtc);
public sealed record WindowsTestControlCommand(int Version, string Generation, string RequestId, string MetadataSha256,
    string InstanceId, string CommandId, string Action, DateTimeOffset RequestedUtc);
public sealed record WindowsTestControlEntry(string Directory, WindowsTestControlState State);

// Host-only files carry decisions, never process handles or arbitrary paths.
// The guardian creates the protected project root; only fresh workspaces receive
// a test-account write grant. Closing a viewer does not cancel these requests.
public static class WindowsTestControls
{
    public static string ProjectRoot(AppPaths paths, string project) =>
        Path.GetFullPath(Path.Combine(paths.AppDataDir, "windows-tests", QemuLayout.ProjectKey(project)));

    public static WindowsTestControlState? ReadState(string directory)
    {
        var value = Read<WindowsTestControlState>(directory, "control-status.json", 256 * 1024);
        if (value is not null && (value.Version != 1 || !Guid.TryParseExact(value.Generation, "N", out _)
            || !Guid.TryParseExact(value.RequestId, "N", out _) || !Guid.TryParseExact(value.InstanceId, "N", out _)
            || value.MetadataSha256 is not { Length: 64 } || !value.MetadataSha256.All(Uri.IsHexDigit)
            || value.OwnerPid <= 0 || value.OwnerStartTicks <= 0
            || value.Stage is not ("preparing" or "awaiting-approval" or "running" or "canceling" or "collecting-results" or "finished")
            || value.Tool is not ("project" or "dotnet" or "node" or "python" or "powershell")
            || value.Arguments is null || value.Arguments.Length > 128 || value.Arguments.Any(argument => argument is null || argument.Length > 8192)
            || value.Arguments.Sum(argument => (long)argument.Length) > 32768
            || value.WorkingDirectory != "." && !WindowsTestProtocol.SafePath(value.WorkingDirectory)
            || value.TimeoutSeconds is < 1 or > 7200
            || value.Program is not null && !WindowsTestProtocol.SafePath(value.Program)
            || value.Stage == "finished" && (value.Outcome is not ("finished" or "failed" or "canceled" or "timed-out" or "interrupted")
                || value.Outcome == "finished" && value.ExitCode is null)))
            throw new InvalidDataException("Invalid Windows test control identity.");
        return value;
    }

    public static IReadOnlyList<WindowsTestControlEntry> List(AppPaths paths, string project)
    {
        var root = ProjectRoot(paths, project);
        if (!FenceFiles.TryResolveUnlinked(root, "probe", out _)) throw new InvalidDataException("Linked Windows test storage.");
        if (!Directory.Exists(root)) return [];
        var results = new List<WindowsTestControlEntry>();
        // Limit the view, never delete history. Only the two host-owned GUID
        // levels are walked; writable workspaces are not recursively searched.
        foreach (var generation in Directory.EnumerateDirectories(root).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
            .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(10))
        {
            if (!FenceFiles.TryResolveUnlinked(root, Path.GetFileName(generation) + "/probe", out _)) continue;
            foreach (var run in Directory.EnumerateDirectories(generation).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(100))
            {
                var state = ReadState(run);
                if (state is not null && state.Generation == Path.GetFileName(generation) && state.RequestId == Path.GetFileName(run))
                    results.Add(new(run, state));
            }
        }
        return results.OrderByDescending(entry => entry.State.RecordedUtc).Take(100).ToArray();
    }

    public static bool OwnerAlive(WindowsTestControlState state)
    {
        try { using var process = Process.GetProcessById(state.OwnerPid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.OwnerStartTicks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static string Send(WindowsTestControlEntry entry, string action)
    {
        if (action is not ("approve" or "deny" or "cancel" or "show" or "return")) throw new ArgumentException("Unknown Windows test action.");
        var current = ReadState(entry.Directory) ?? throw new IOException("This Windows test is no longer available.");
        if (current.InstanceId != entry.State.InstanceId || current.Generation != entry.State.Generation || current.RequestId != entry.State.RequestId
            || current.MetadataSha256 != entry.State.MetadataSha256 || !OwnerAlive(current))
            throw new IOException("The Windows test owner changed. Refresh before taking an action.");
        var command = new WindowsTestControlCommand(1, current.Generation, current.RequestId, current.MetadataSha256,
            current.InstanceId, Guid.NewGuid().ToString("N"), action, DateTimeOffset.UtcNow);
        var name = action == "cancel" ? "cancel.json" : action is "approve" or "deny" ? "decision.json" : "desktop-command.json";
        ReturnRecovery.SaveAtomic(Safe(entry.Directory, name), command);
        return command.CommandId;
    }

    internal static bool Matches(WindowsTestControlCommand command, WindowsTestControlState state) => command.Version == 1
        && command.Generation == state.Generation && command.RequestId == state.RequestId && command.MetadataSha256 == state.MetadataSha256
        && command.InstanceId == state.InstanceId && Guid.TryParseExact(command.CommandId, "N", out _)
        && command.RequestedUtc <= DateTimeOffset.UtcNow.AddSeconds(5) && command.RequestedUtc >= DateTimeOffset.UtcNow.AddSeconds(-60);

    internal static T? Read<T>(string root, string name, int maximum = 4096)
    {
        var path = Safe(root, name);
        if (!File.Exists(path)) return default;
        using var input = WindowsTestProtocol.OpenResultSource(path);
        if (input.Length > maximum) throw new InvalidDataException("Windows test control exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(input, WindowsTestProtocol.Json) ?? throw new InvalidDataException("Empty Windows test control.");
    }
    internal static string Safe(string root, string name) => FenceFiles.TryResolveUnlinked(root, name, out var path)
        ? path : throw new InvalidDataException("Linked Windows test control storage.");
}

public sealed class ControlledWindowsTestExecutor : IControlledWindowsTestExecutor
{
    public const int ApprovalWaitSeconds = 60; // Fits existing runtime+90 result wait.
    private readonly string _root;
    private readonly Func<string> _permission;
    private readonly Func<WindowsTestRequest, string>? _requestPermission;
    private readonly Func<Action<IWindowsTestDesktopControl?>, Func<bool>, IWindowsTestExecutor> _factory;
    private readonly TimeSpan _approvalWait;
    private readonly object _gate = new();
    private WindowsTestControlState? _state;
    private string _run = "";
    private IWindowsTestDesktopControl? _desktop;

    public ControlledWindowsTestExecutor(string root, Func<string> permission,
        Func<Action<IWindowsTestDesktopControl?>, Func<bool>, IWindowsTestExecutor>? factory = null, TimeSpan? approvalWait = null,
        Func<WindowsTestRequest, string>? requestPermission = null)
    {
        _root = Path.GetFullPath(root); _permission = permission; _requestPermission = requestPermission;
        _factory = factory ?? ((ready, canStart) => new ManagedWindowsTestExecutor(desktop => ready(desktop), canStart));
        _approvalWait = approvalWait ?? TimeSpan.FromSeconds(ApprovalWaitSeconds);
        if (_approvalWait <= TimeSpan.Zero || _approvalWait > TimeSpan.FromSeconds(ApprovalWaitSeconds)) throw new ArgumentOutOfRangeException(nameof(approvalWait));
    }

    public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, CancellationToken token) =>
        ExecuteAsync(request, workingCopy, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, WindowsTestProtocol.Json))).ToLowerInvariant(), token);

    public async Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, string metadataSha256, CancellationToken token)
    {
        WindowsTestProtocol.Validate(request, request.Generation);
        if (metadataSha256.Length != 64 || !metadataSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid Windows test content identity.");
        _run = WindowsTestControls.Safe(_root, request.Generation + "/" + request.RequestId);
        if (Path.GetFullPath(workingCopy) != Path.Combine(_run, "workspace")) throw new InvalidDataException("Windows test working copy does not match its request.");
        Directory.CreateDirectory(_run);
        using var owner = Process.GetCurrentProcess();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _state = new(1, request.Generation, request.RequestId, metadataSha256, Guid.NewGuid().ToString("N"), owner.Id,
            owner.StartTime.ToUniversalTime().Ticks, "preparing", request.Tool, request.Program, request.Arguments, request.Interactive,
            request.WorkingDirectory, request.TimeoutSeconds, null, false, null, null, null, null, DateTimeOffset.UtcNow);
        Save();
        Task? monitor = null;
        try
        {
            var approved = false;
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                CheckCancel(stop);
                stop.Token.ThrowIfCancellationRequested();
                var permission = Permission(request); // Read latest saved policy, not UI memory.
                if (permission is not ("automatic" or "confirm")) throw new InvalidDataException("Windows test permission is invalid; nothing was approved.");
                if (permission == "automatic" || approved) break;
                var limit = _state.ApprovalDeadline ?? DateTimeOffset.UtcNow.Add(_approvalWait);
                _state = _state with { Stage = "awaiting-approval", ApprovalDeadline = limit };
                Save();
                if (DateTimeOffset.UtcNow >= limit) return new("failed", null, "Windows test approval expired. Nothing was started; the snapshot is retained.");
                var decision = WindowsTestControls.Read<WindowsTestControlCommand>(_run, "decision.json");
                if (decision is not null && WindowsTestControls.Matches(decision, _state) && decision.Action is "approve" or "deny")
                {
                    _state = _state with { AcknowledgedCommand = decision.CommandId };
                    if (decision.Action == "deny") return new("failed", null, "Windows test denied by the user. Nothing was started.");
                    approved = true;
                }
                await Task.Delay(200, stop.Token).ConfigureAwait(false);
            }
            // A saved policy change during pending/preparation is observed here.
            var finalPermission = Permission(request);
            if (finalPermission is not ("automatic" or "confirm") || finalPermission == "confirm" && !approved)
                return new("failed", null, "Windows test permission changed before launch. Nothing was started; submit a new request for confirmation.");
            _state = _state with { Stage = "running", ApprovalDeadline = null };
            Save();
            monitor = MonitorAsync(stop, request, approved);
            return await _factory(DesktopReady, () =>
            {
                CheckCancel(stop);
                var permission = Permission(request);
                return !stop.IsCancellationRequested && (permission == "automatic" || permission == "confirm" && approved);
            }).ExecuteAsync(request, workingCopy, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new("canceled", null, _state?.Error ?? "Windows test canceled; its snapshot and results are retained."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or System.ComponentModel.Win32Exception or ArgumentException)
        { return new("failed", null, "Windows test control failed: " + error.Message); }
        finally
        {
            stop.Cancel();
            if (monitor is not null) try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
            lock (_gate) { _desktop = null; _state = _state with { Stage = "collecting-results", DesktopAvailable = false }; Save(); }
        }
    }

    public void RecordResult(WindowsTestResponse response)
    {
        lock (_gate)
        {
            var run = WindowsTestControls.Safe(_root, response.Generation + "/" + response.RequestId);
            var state = WindowsTestControls.ReadState(run);
            if (state is null || state.Generation != response.Generation || state.RequestId != response.RequestId) return;
            ReturnRecovery.SaveAtomic(WindowsTestControls.Safe(run, "response.json"), response);
            state = state with { Stage = "finished", DesktopAvailable = false, Outcome = response.Outcome, ExitCode = response.ExitCode,
                Error = response.Error, RecordedUtc = DateTimeOffset.UtcNow };
            ReturnRecovery.SaveAtomic(WindowsTestControls.Safe(run, "control-status.json"), state);
            if (_state?.Generation == response.Generation && _state.RequestId == response.RequestId) _state = state;
        }
    }

    private void DesktopReady(IWindowsTestDesktopControl? desktop)
    { lock (_gate) { _desktop = desktop; _state = _state! with { DesktopAvailable = desktop is not null }; Save(); } }

    private void CheckCancel(CancellationTokenSource stop)
    {
        var cancel = WindowsTestControls.Read<WindowsTestControlCommand>(_run, "cancel.json");
        if (cancel is not null && cancel.Action == "cancel" && WindowsTestControls.Matches(cancel, _state!))
        { lock (_gate) { _state = _state! with { Stage = "canceling", AcknowledgedCommand = cancel.CommandId }; Save(); } stop.Cancel(); }
    }

    private string Permission(WindowsTestRequest request) => _requestPermission?.Invoke(request) ?? _permission();

    private async Task MonitorAsync(CancellationTokenSource stop, WindowsTestRequest request, bool approved)
    {
        string? consumed = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                CheckCancel(stop);
                var permission = Permission(request);
                if (permission is not ("automatic" or "confirm") || permission == "confirm" && !approved)
                {
                    lock (_gate) { _state = _state! with { Error = "Windows test permission expired or changed; stopping this owned test." }; Save(); }
                    stop.Cancel();
                }
                if (stop.IsCancellationRequested) break;
                var command = WindowsTestControls.Read<WindowsTestControlCommand>(_run, "desktop-command.json");
                if (command is not null && command.CommandId != consumed && command.Action is "show" or "return"
                    && WindowsTestControls.Matches(command, _state!))
                {
                    lock (_gate)
                    {
                        consumed = command.CommandId;
                        string? error = null;
                        try
                        {
                            if (_desktop is null) throw new InvalidOperationException("This test has no interactive desktop available.");
                            if (command.Action == "show") _desktop.Activate();
                            else if (!_desktop.ReturnToLaunchPad()) throw new IOException("Return to the normal desktop could not be verified.");
                        }
                        catch (Exception failure) when (failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        { error = failure.Message; }
                        _state = _state! with { AcknowledgedCommand = command.CommandId, Error = error }; Save();
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                stop.Cancel(); // A failed control file cannot leave execution unmonitored.
                lock (_gate) { _state = _state! with { Error = "Windows test controls unavailable: " + error.Message }; Save(); }
            }
            await Task.Delay(200, stop.Token).ConfigureAwait(false);
        }
    }

    private void Save()
    {
        _state = _state! with { RecordedUtc = DateTimeOffset.UtcNow };
        ReturnRecovery.SaveAtomic(WindowsTestControls.Safe(_run, "control-status.json"), _state);
    }
}
