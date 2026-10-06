using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Preserves the Windows launch protocol while UI/shared records migrate.</summary>
public sealed class WindowsProjectRuntime(GrokSetup setup, ProjectLauncher launcher, SetupLog log, AppPaths paths, SettingsStore settings) : IProjectRuntime
{
    private readonly object _windowGate = new();
    private readonly Dictionary<string, (int Pid, long Ticks, string Directory, int HelperPid, long HelperTicks)> _hostWatches = new(StringComparer.OrdinalIgnoreCase);
    public bool HasVirtualMachine => OperatingSystem.IsWindows() && FenceReady.Installed();
    public string? HostAgentExecutable => OperatingSystem.IsWindows() ? launcher.AgentExecutable : null;
    public FenceStartAvailability FenceStartAvailability
    {
        get
        {
            RequireWindows();
            var reason = FenceSession.FileShareBlockReason();
            return new FenceStartAvailability(reason, FenceSession.StartBlocked(reason));
        }
    }
    public IFencedProjectSession CreateFencedSession()
    {
        RequireWindows();
        return NewFenceSession();
    }
    public bool IsFencedOpen(string project) => OperatingSystem.IsWindows() && FenceSession.IsProjectOpen(project);
    public bool IsHostOpen(string project) => OperatingSystem.IsWindows() && launcher.WasLaunched(project);
    public Task<bool> EnsureHostAgentAsync() => OperatingSystem.IsWindows()
        ? setup.EnsureHostGrokAsync() : Task.FromResult(false);
    public Task OpenFencedAsync(string project, CancellationToken cancellationToken, IProgress<string>? progress)
    {
        RequireWindows();
        return ProjectRow.OpenAsync(NewFenceSession(), project, cancellationToken, progress);
    }
    private FenceSession NewFenceSession() => new(log, readSettings: () => new SettingsStore(paths));
    public bool TryLaunchHostAgent(string project, LaunchPlacement? placement, out string error)
    {
        RequireWindows();
        return launcher.TryLaunch(project, out error, placement);
    }
    public Task<string?> SendProjectAsync(string project, CancellationToken cancellationToken)
    {
        RequireWindows();
        return LiveSession.TrySendAsync(project, cancellationToken);
    }
    public SessionRecord? DescribeFenced(string project)
    {
        var record = LiveSession.Describe(project) ?? FenceSession.DescribeOpen(project);
        if (record is null) return null;
        var directory = ProjectSessionStore.Current(Path.Combine(QemuLayout.Root, "sessions", QemuLayout.ProjectKey(project)));
        var owner = SessionGuardian.TryReadLiveOwner(directory);
        var window = owner is { TerminalPid: int pid, TerminalStartTicks: long ticks }
            ? WindowsSessionWindow.Read(directory, pid, ticks) : null;
        // A reopened desktop has no status connection to its independently owned VM.
        if (LiveSession.Describe(project) is null && record.State == SessionLifecycle.Running)
            record = record with { State = SessionLifecycle.Unknown, Error = "The terminal is open; agent activity is unavailable in this app instance." };
        return record with { WindowHandle = window is null ? null : (nint)window.WindowHandle, Window = window };
    }
    public void UpdateSessionTitles(string project, string vmTitle, string hostTitle)
    {
        RequireWindows();
        if (IsFencedOpen(project)) TuiWindow.SaveDisplayTitle(ProjectSessionStore.Current(Path.Combine(QemuLayout.Root, "sessions", QemuLayout.ProjectKey(project))), vmTitle);
        if (EnsureHostWatch(project) is { } directory) TuiWindow.SaveDisplayTitle(directory, hostTitle);
    }
    public SessionRecord? DescribeHost(string project)
    {
        if (launcher.Describe(project) is not { } launch) return null;
        var process = launcher.TrackedProcess(project);
        var directory = EnsureHostWatch(project);
        var window = process is { } tracked && directory is not null
            ? WindowsSessionWindow.Read(directory, tracked.Pid, tracked.StartTicks) : null;
        return new SessionRecord("host:" + QemuLayout.ProjectKey(project), Path.GetFullPath(project), "grok",
            SessionKind.HostWindow, process?.Pid, null, window is null ? null : (nint)window.WindowHandle,
            launch.Starting ? SessionLifecycle.Starting : SessionLifecycle.Running, Window: window);
    }

    public string? FocusSession(SessionRecord session)
    {
        RequireWindows();
        var expectedId = (session.Kind == SessionKind.VirtualMachine ? "vm:" : "host:") + QemuLayout.ProjectKey(session.ProjectPath);
        if (session.Id != expectedId || session.Window is null) return "This session's window is not linked yet. Try again when its terminal opens.";
        var current = session.Kind == SessionKind.VirtualMachine ? DescribeFenced(session.ProjectPath) : DescribeHost(session.ProjectPath);
        if (current?.Id != session.Id || current.Window != session.Window)
            return "The session window changed. Refresh the project list and try again.";
        return WindowsSessionWindow.Focus(session.Window);
    }

    private string? EnsureHostWatch(string project)
    {
        if (launcher.TrackedProcess(project) is not { } process) return null;
        var key = QemuLayout.ProjectKey(project);
        lock (_windowGate)
        {
            if (_hostWatches.TryGetValue(key, out var existing) && existing.Pid == process.Pid && existing.Ticks == process.StartTicks
                && WindowsSessionWindow.MatchesProcess(existing.HelperPid, existing.HelperTicks))
                return existing.Directory;
            var relative = "windows/host/" + key + "/" + process.Pid + "-" + process.StartTicks;
            if (!FenceFiles.TryResolveUnlinked(paths.AppDataDir, relative, out var directory)) return null;
            try
            {
                Directory.CreateDirectory(directory);
                TuiWindow.SaveDisplayTitle(directory, settings.DisplayNameFor(project, "host:" + key));
                var helper = WindowsSessionWindow.StartHostWatch(paths.ExePath, directory, process.Pid, process.StartTicks);
                _hostWatches[key] = (process.Pid, process.StartTicks, directory, helper.Pid, helper.Ticks);
                return directory;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { log.Write("Host window association: " + error.Message); return null; }
        }
    }
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This runtime supports Windows. The Mac runtime has not been implemented yet.");
    }
}
