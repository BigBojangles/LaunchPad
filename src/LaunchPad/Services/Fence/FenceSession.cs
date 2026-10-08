using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;

namespace LaunchPad.Services.Fence;

public delegate bool SessionStarter(SessionLaunch launch);

public sealed class SessionLaunch
{
    public SessionLaunch(string userName, string backingImage, string sessionDisk, IReadOnlyList<string> arguments)
    {
        UserName = userName;
        BackingImage = backingImage;
        SessionDisk = sessionDisk;
        Arguments = arguments;
    }

    public string UserName { get; }
    public string BackingImage { get; }
    public string SessionDisk { get; }
    public IReadOnlyList<string> Arguments { get; }
}

public sealed class FenceSession : IFencedProjectSession
{
    private readonly record struct OpenBox(string Path, int Qmp, string AgentId);

    private static readonly object Gate = new();
    private static readonly Dictionary<int, OpenBox> Boxes = new();
    private static readonly HashSet<int> ReservedBases = new();

    private readonly SetupLog _log;
    private readonly IReturnScan _scan;
    private readonly Func<bool> _launchAccountReady;
    private readonly Func<string>? _keptImage;
    private readonly Func<string>? _sessionsRoot;
    private readonly SessionStarter? _starter;
    private readonly Func<string>? _asideRoot;
    private readonly IReturnFileScanner? _returnScanner;
    private readonly Func<string, Task<bool>>? _returnBackup;
    private readonly Func<SettingsStore> _readSettings;

    public FenceSession(
        SetupLog log,
        IReturnScan? scan = null,
        Func<bool>? launchAccountReady = null,
        Func<string>? keptImage = null,
        Func<string>? sessionsRoot = null,
        SessionStarter? starter = null,
        Func<string>? asideRoot = null,
        IReturnFileScanner? returnScanner = null,
        Func<string, Task<bool>>? returnBackup = null,
        Func<SettingsStore>? readSettings = null)
    {
        _log = log;
        _scan = scan ?? new StubReturnScan();
        _launchAccountReady = launchAccountReady ?? TestUserRunner.LaunchAccountReady;
        _keptImage = keptImage;
        _sessionsRoot = sessionsRoot;
        _starter = starter;
        _asideRoot = asideRoot;
        _returnScanner = returnScanner;
        _returnBackup = returnBackup;
        _readSettings = readSettings ?? (() => new SettingsStore(new AppPaths()));
    }

    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public static bool IsProjectOpen(string path)
    {
        lock (Gate)
        {
            Prune();
            var full = Path.GetFullPath(path);
            foreach (var box in Boxes.Values)
            {
                if (string.Equals(box.Path, full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return ReadProjectOwner(full) is not null;
        }
    }

    public static void NoteOpen(int pid, string path, int qmp, string agentId = AgentChoice.Grok)
    {
        lock (Gate)
        {
            Boxes[pid] = new OpenBox(Path.GetFullPath(path), qmp, agentId);
            ReservedBases.Add(qmp);
        }
    }

    private static SessionOwnerIdentity? ReadProjectOwner(string project)
    {
        try { return SessionGuardian.TryReadLiveOwner(ProjectSessionStore.Current(Path.Combine(QemuLayout.Root, "sessions", QemuLayout.ProjectKey(project)))); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void NoteClosed(int pid)
    {
        lock (Gate)
        {
            if (Boxes.Remove(pid, out var box))
                ReservedBases.Remove(box.Qmp);
        }
    }

    public static LaunchPad.Models.SessionRecord? DescribeOpen(string project)
    {
        lock (Gate)
        {
            Prune();
            var full = Path.GetFullPath(project);
            foreach (var (pid, box) in Boxes)
                if (string.Equals(box.Path, full, StringComparison.OrdinalIgnoreCase))
                    return new LaunchPad.Models.SessionRecord("vm:" + QemuLayout.ProjectKey(full), full, box.AgentId,
                        LaunchPad.Models.SessionKind.VirtualMachine, pid, null, null, LaunchPad.Models.SessionLifecycle.Starting);
            var owner = ReadProjectOwner(full);
            return owner is null ? null : new LaunchPad.Models.SessionRecord("vm:" + QemuLayout.ProjectKey(full), full, owner.AgentId,
                LaunchPad.Models.SessionKind.VirtualMachine, owner.MachinePid, owner.TerminalPid, null,
                owner.TerminalPid is null ? LaunchPad.Models.SessionLifecycle.Starting : LaunchPad.Models.SessionLifecycle.Running);
        }
    }

    public string? LatestAside(string liveProject)
    {
        var statePath = ProjectStatePath(liveProject);
        if (!File.Exists(statePath))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<FenceStateFile>(File.ReadAllText(statePath), JsonFile.Options);
            if (state is null || string.IsNullOrWhiteSpace(state.AsideDirectory) || !Directory.Exists(state.AsideDirectory))
                return null;
            return state.AsideDirectory;
        }
        catch
        {
            return null;
        }
    }

    public ScanReport? LatestScan(string liveProject)
    {
        var statePath = ProjectStatePath(liveProject);
        if (!File.Exists(statePath))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize<FenceStateFile>(File.ReadAllText(statePath), JsonFile.Options);
            if (state is null || string.IsNullOrWhiteSpace(state.ScanStatement))
                return null;
            return new ScanReport(state.ScanIsStub, state.ScanStatement);
        }
        catch
        {
            return null;
        }
    }

    public ProjectRecovery ReadRecovery(string project)
    {
        var report = RecoveryCatalog.Read(project, Path.Combine(SessionsDirectory(), QemuLayout.ProjectKey(project)));
        if (LatestAside(project) is not { } aside || report.Returns.Any(item => item.ReviewPath.Equals(aside, StringComparison.OrdinalIgnoreCase)))
            return report;
        var managedAside = Path.GetFullPath(Path.Combine(_asideRoot?.Invoke() ?? QemuLayout.AsideRoot, QemuLayout.ProjectKey(project)));
        var fullAside = Path.GetFullPath(aside);
        if (!(fullAside.Equals(managedAside, StringComparison.OrdinalIgnoreCase)
                || fullAside.StartsWith(managedAside + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || !FenceFiles.TryResolveUnlinked(fullAside, "review/check", out _)) return report;
        return report with { Returns = report.Returns.Append(new SavedReturn(fullAside, Directory.GetLastWriteTimeUtc(fullAside), -1,
            false, "This older waiting copy has no verified content receipt. Its files remain available for review.", ReviewDirectory: fullAside)).ToArray() };
    }

    public async Task CopyBackToProjectAsync(string liveProject, string? recoveryDirectory = null)
    {
        if (IsProjectOpen(liveProject)) throw new InvalidOperationException("Close the active project session before retrying copy-back. Saved files remain available for review.");
        if (recoveryDirectory is null)
        {
            var state = JsonSerializer.Deserialize<FenceStateFile>(File.ReadAllText(ProjectStatePath(liveProject)), JsonFile.Options);
            if (state is null || string.IsNullOrWhiteSpace(state.RecoveryDirectory))
                throw new InvalidOperationException("This older waiting copy has no verified transfer receipt. It was preserved; the project was left unchanged.");
            recoveryDirectory = state.RecoveryDirectory;
        }
        var recovery = RecoveryCatalog.OpenForProject(liveProject,
            Path.Combine(SessionsDirectory(), QemuLayout.ProjectKey(liveProject)), recoveryDirectory);
        var receipt = recovery.LoadTransfer(liveProject);
        var result = await ApplyReturnAsync(liveProject, recovery, receipt);
        SaveReturnState(liveProject, recovery, receipt, result, ProjectStatePath(liveProject));
        if (!result.Applied) throw new InvalidOperationException(result.Message);
    }

    public static bool StartBlocked(string? reason) =>
        !string.IsNullOrEmpty(reason)
        && !string.Equals(reason, SealText.NoFileShare, StringComparison.Ordinal);

    public static string? FileShareBlockReason()
    {
        var qemu = QemuLayout.FindExe("qemu-system-x86_64.exe");
        if (qemu is null)
            return "QEMU is not in the folder beside this project.";

        if (QemuCommand.FsdevIsDisabled(ReadFsdevHelp(qemu)))
            return SealText.NoFileShare;

        return null;
    }

    public Task OpenAsync(string liveProject, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        StartAsync(liveProject, null, progress, cancellationToken, leaveRunning: true);

    public Task ResumeSavedAsync(string project, LaunchPlacement? placement, IProgress<string>? progress, CancellationToken cancellationToken) =>
        StartCoreAsync(project, placement, progress, cancellationToken, leaveRunning: true, resumeOnly: true);

    public Task StartAsync(string liveProject, LaunchPlacement? placement, IProgress<string>? progress, CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? warningsChanged = null, bool leaveRunning = false) =>
        StartCoreAsync(liveProject, placement, progress, cancellationToken, warningsChanged, leaveRunning);

    private async Task StartCoreAsync(
        string liveProject,
        LaunchPlacement? placement,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? warningsChanged = null,
        bool leaveRunning = false, bool resumeOnly = false)
    {
        _ = placement;
        if (!resumeOnly && !Directory.Exists(liveProject))
            throw new DirectoryNotFoundException("That project folder is no longer there.");

        var liveFull = Path.GetFullPath(liveProject);
        // One readable settings generation selects this launch. Never substitute
        // another agent/resource configuration after a settings read failure.
        var launchSettings = _readSettings();
        var agent = launchSettings.AgentFor(liveFull);
        var permissionPolicy = launchSettings.PermissionPolicyFor(liveFull);
        PermissionPolicies.RequireLaunchSupport(permissionPolicy);
        var machine = MachineSize(launchSettings, liveFull);
        var title = launchSettings.DisplayNameFor(liveFull, "vm:" + QemuLayout.ProjectKey(liveFull));

        if (_starter is null)
            SessionSweep.StopAbandoned(SessionsDirectory());

        if (IsProjectOpen(liveProject))
            throw new InvalidOperationException(SealText.AlreadyRunning);

        if (!_launchAccountReady())
            throw new InvalidOperationException(SealText.TestAccountMissing);

        var user = TestUserRunner.UserName;
        if (string.Equals(user, "builder", StringComparison.Ordinal)
            || string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(SealText.TestAccountMissing);

        var kept = _keptImage?.Invoke() ?? QemuLayout.KeptImagePath;
        if (!RuntimeImages.IsBuilder(kept)
            || kept.Contains("nocloud", StringComparison.OrdinalIgnoreCase)
            || kept.EndsWith("prepared.qcow2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The fenced machine was not pointed at the kept image.");

        var sessionHome = Path.Combine(SessionsDirectory(), SessionFolderName(liveFull));
        using var preparing = ProjectSessionStore.Acquire(sessionHome);
        if (IsProjectOpen(liveFull)) throw new InvalidOperationException(SealText.AlreadyRunning);
        var sessionDir = ProjectSessionStore.Current(sessionHome);
        var overlay = Path.Combine(sessionDir, "session.qcow2");
        var serialLog = Path.Combine(sessionDir, "serial.log");
        var args = QemuCommand.Build("whpx", overlay, 0, PortChoice.First, "fence", share: null, serialLog: serialLog, memoryMb: machine.MemoryMb, cores: machine.Cores);
        RejectLivePath(args, liveFull);
        if (QemuCommand.Mentions(args, "prepared.qcow2") || QemuCommand.Mentions(args, "nocloud"))
            throw new InvalidOperationException("The fenced machine was pointed at the base image.");

        var launch = new SessionLaunch(user, kept, overlay, args);
        if (_starter is not null)
        {
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "session-user.txt"), user, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!_starter(launch))
                throw new InvalidOperationException(SealText.TestAccountMissing);
            Warnings = Array.Empty<string>();
            return;
        }

        int qmp;
        lock (Gate)
        {
            Prune();
            qmp = ReservePort();
            ReservedBases.Add(qmp);
        }

        var noted = false;
        try
        {
            progress?.Report(SealText.WarmingUp);
            var runtime = await Task.Run(() => PublicRuntime.Ensure(_log), cancellationToken).ConfigureAwait(false);
            var qemu = runtime.QemuExe;
            var img = runtime.ImgExe;
            var backing = runtime.KeptImage;
            if (_sessionsRoot is null)
            {
                sessionDir = ProjectSessionStore.Current(Path.Combine(runtime.Sessions, SessionFolderName(liveFull)));
                overlay = Path.Combine(sessionDir, "session.qcow2");
                serialLog = Path.Combine(sessionDir, "serial.log");
            }

            Directory.CreateDirectory(sessionDir);
            if (!resumeOnly && SessionGuardian.NeedsRecovery(sessionDir))
                throw new InvalidOperationException("The previous session has unfinished recovery or an unconfirmed import. Its VM disk is preserved; restarting could overwrite saved work.");
            if (resumeOnly && !File.Exists(overlay)) throw new InvalidOperationException("There is no saved VM disk for this project.");

            // An older disk is upgraded through a new child, never replaced or
            // rebased. Recovery always makes a child even at the same version.
            if (File.Exists(overlay) && runtime.Version is { } version
                && (ProjectSessionStore.RuntimeVersion(sessionDir) != version || (resumeOnly && SessionGuardian.NeedsRecovery(sessionDir, includePreserved: false))))
            {
                var kit = await Task.Run(() => MaintenanceKit.Read(runtime.Root, runtime.MaintenanceManifestName), cancellationToken).ConfigureAwait(false);
                if (kit.Manifest.Version != version) throw new InvalidDataException("The saved-VM maintenance kit does not match the selected runtime.");
                var upgraded = await SessionUpgrade.CreateAsync(runtime, kit, overlay, SessionsDirectory(), cancellationToken, progress).ConfigureAwait(false);
                ProjectSessionStore.Activate(sessionHome, upgraded);
                sessionDir = upgraded.Directory;
                overlay = upgraded.Disk;
                serialLog = Path.Combine(sessionDir, "serial.log");
            }
            RestrictedRuntimeAccess.ModifyDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "session-user.txt"), user, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var directBoot = RuntimeBoot.ForSession(runtime.DirectBoot, runtime.Version, sessionDir, File.Exists(overlay));
            args = QemuCommand.Build("whpx", overlay, 0, qmp, "fence", share: null, serialLog: serialLog, memoryMb: machine.MemoryMb, cores: machine.Cores, firmwareDir: runtime.FirmwareDir, workingDirectory: sessionDir, directBoot: directBoot);
            RejectLivePath(args, liveFull);
            cancellationToken.ThrowIfCancellationRequested();
            var existingSession = File.Exists(overlay);
            if (!existingSession)
            {
                RunTool(img, Path.GetDirectoryName(img)!, "create", "-f", "qcow2", "-b", backing, "-F", "qcow2", overlay);
                ReturnRecovery.SaveAtomic(Path.Combine(sessionDir, "session-runtime.json"), new { version = runtime.Version, directBoot = directBoot?.Manifest });
            }

            // The payload starts as BuildLaunchTest. A failed logon does not start it as the signed-in user.
            if (!agent.IsGrok && !ConfigHeal.ImageHasText(backing, AgentChoice.Marker))
                throw new InvalidOperationException("This machine does not have that agent yet.");
            cancellationToken.ThrowIfCancellationRequested();
            if (leaveRunning) InitialImport.Begin(sessionDir);
            progress?.Report(SealText.VmLaunching);
            if (!TestUserRunner.TryStart(qemu, sessionDir, args, out var process) || process is null)
            {
                if (TestUserRunner.LastStartError == 0)
                    throw new InvalidOperationException(SealText.TestAccountMissing);

                _log.Write("Fenced start win32 " + TestUserRunner.LastStartError);
                throw new InvalidOperationException("The fenced session did not start.");
            }

            NoteOpen(process.Id, liveFull, qmp, agent.Id);
            noted = true;
            var plain = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(Path.Combine(sessionDir, "qemu.pid"), process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), plain);
            File.WriteAllText(Path.Combine(sessionDir, "qmp.txt"), qmp.ToString(System.Globalization.CultureInfo.InvariantCulture), plain);

            if (leaveRunning)
            {
                try
                {
                    using var owner = await SessionGuardian.StartAsync(process, qmp, sessionDir, agent.Id, liveFull, appliedPolicy: permissionPolicy).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(title))
                        title = "LaunchPad";
                    var pidFile = Path.Combine(sessionDir, "tui.pid");
                    var hand = await FenceHost.HandOffAsync(liveFull, qmp, title, pidFile, cancellationToken, agent, existingSession, resumeOnly,
                        ProjectIdentity.ColorHex(launchSettings, liveFull)).ConfigureAwait(false);
                    var oldAgent = !agent.IsGrok
                        && await AgentScriptMissing(serialLog, cancellationToken).ConfigureAwait(false)
                        && ConfigHeal.ImageHasText(backing, AgentChoice.Marker);
                    if ((await ConfigStayedBroken(serialLog, cancellationToken).ConfigureAwait(false)
                                && ConfigHeal.ImageHasConfigFix(backing))
                            || oldAgent)
                    {
                        _log.Write("Fenced session requires recovery; preserving its VM disk and saved state.");
                        hand.Status.Dispose();
                        hand.Tui?.Dispose();
                        // The catch below stops this failed machine. It must never
                        // replace an overlay or silently create a fresh session.
                        ConfigHeal.StopForRecovery(sessionDir);
                    }

                    using var tui = hand.Tui;
                    // wt.exe exits as soon as the terminal accepts the tab. The session
                    // lasts until the --tui process recorded in tui.pid exits.
                    WatchTui(pidFile, process, qmp, hand.Status, QemuLayout.ProjectKey(liveFull), liveFull, agent.Id);
                    _log.Write("Fenced session user " + user);
                    progress?.Report(SealText.BlastOff);
                    return;
                }
                catch
                {
                    TestUserRunner.ReleaseMachine(process.Id);
                    StopMachine(process, qmp, sessionDir);
                    NoteClosed(process.Id);
                    throw;
                }
            }

            try
            {
                while (!process.HasExited)
                {
                    PublishWarnings(serialLog, warningsChanged);
                    try
                    {
                        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // Cancellation stops the wait; cleanup runs away from the UI
                // thread and lets Linux flush its persistent overlay first.
                await Task.Run(() => StopMachine(process, qmp, sessionDir)).ConfigureAwait(false);
                try
                {
                    if (!process.HasExited)
                        process.WaitForExit(2000);
                }
                catch
                {
                    // The process this start created is already gone.
                }

                NoteClosed(process.Id);
                process.Dispose();
            }

            PublishWarnings(serialLog, warningsChanged);
            _log.Write("Fenced session user " + user);
        }
        finally
        {
            if (!noted)
            {
                lock (Gate)
                    ReservedBases.Remove(qmp);
            }
        }
    }

    public void ShowWarnings(string log) => Warnings = GuestWarnings.Keep(log);

    private void PublishWarnings(string serialLog, Action<IReadOnlyList<string>>? warningsChanged)
    {
        var next = GuestWarnings.Keep(ReadSerialLog(serialLog));
        if (SameWarnings(Warnings, next))
            return;

        Warnings = next;
        warningsChanged?.Invoke(next);
    }

    private static bool SameWarnings(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string ReadSerialLog(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return "";

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 7)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return "";
            }
        }

        return "";
    }

    private static async Task<bool> AgentScriptMissing(string serialLog, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (true)
        {
            var log = ReadSerialLog(serialLog);
            if (log.Contains(AgentChoice.Marker, StringComparison.Ordinal))
                return false;
            if (DateTime.UtcNow >= deadline)
                // Import completion precedes child startup. Allow the child the
                // existing readiness interval before declaring an old script.
                return log.Contains("fence-ready", StringComparison.Ordinal);

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ConfigStayedBroken(string serialLog, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (true)
        {
            var log = ReadSerialLog(serialLog);
            if (log.Contains(ConfigHeal.Marker, StringComparison.Ordinal))
                return false;
            if (ConfigHeal.LogShowsBrokenConfig(log))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private string SessionsDirectory() =>
        _sessionsRoot?.Invoke() ?? Path.Combine(QemuLayout.Root, "sessions");

    private static (int MemoryMb, int Cores) MachineSize(SettingsStore settings, string liveProject)
    {
        var installed = GuestMemory.InstalledMegabytes();
        return (
            GuestMemory.ChooseProjectMegabytes(settings.ProjectMemoryMbFor(liveProject), settings.Current.MachineMemoryMb, installed),
            GuestMemory.ChooseCores(settings.Current.MachineCores, Environment.ProcessorCount));
    }

    private void WatchTui(string pidFile, Process qemu, int qmpPort, StatusLink status, string projectKey, string liveProject, string agentId)
    {
        var sessionDir = Path.GetDirectoryName(pidFile) ?? "";
        LiveSession.Begin(liveProject, sessionDir, qmpPort, status, qemu.Id, agentId);
        var stopSize = new CancellationTokenSource();
        var sizing = Task.Run(() => SessionSizeRelay.RunAsync(sessionDir, qmpPort, stopSize.Token));
        _ = Task.Run(async () =>
        {
            using var heartbeatStop = new CancellationTokenSource();
            var heartbeat = KeepHostAliveAsync(sessionDir, heartbeatStop.Token);
            string? returnWarning = null;
            try
            {
            var graceful = false;
            try
            {
                var pid = await WaitForTuiPid(pidFile).ConfigureAwait(false);
                if (pid <= 0)
                {
                    _log.Write("Fenced session window did not start.");
                    TestUserRunner.ReleaseMachine(qemu.Id);
                }
                else
                {
                    // The independent owner watches this terminal and holds the
                    // VM job through guest shutdown, even if the desktop closes.
                    graceful = await WaitForConsole(sessionDir, pid, status).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Write("Fenced session window: " + ex.Message);
            }

            LiveSession.End(liveProject, status);

            if (!graceful)
            {
                stopSize.Cancel();
                try
                {
                    await sizing.ConfigureAwait(false);
                }
                catch
                {
                    // The size relay stops when the window closes.
                }

                var owner = SessionGuardian.TryReadLiveOwner(sessionDir);
                if (owner is null || !qemu.WaitForExit(40000)) StopMachine(qemu, qmpPort, sessionDir);
                NoteClosed(qemu.Id);
                status.Dispose();
                return;
            }

            stopSize.Cancel();
            try
            {
                await sizing.ConfigureAwait(false);
            }
            catch
            {
                // The size relay stops when the window closes.
            }

            ReturnRecovery? recovery = null;
            try
            {
                recovery = ReturnRecovery.Create(sessionDir);
                SentManifest.Load(Path.Combine(sessionDir, "sent.manifest")).Save(recovery.Manifest);
                var receipt = ProjectPull.Receive(QemuCommand.FencePort(qmpPort), recovery.Payload, TimeSpan.FromSeconds(20));
                recovery.SaveTransfer(liveProject, receipt);
                if (SessionGuardian.TryReadLiveOwner(sessionDir) is { } owner)
                    SessionCompletion.RecordReturn(sessionDir, owner, receipt, recovery.DirectoryPath);
                var result = ProjectSessionStore.HasUnconfirmedImport(sessionDir)
                    ? new ReturnApplyResult(false, "The previous send was not confirmed. Returned files and the VM disk are preserved for recovery: " + recovery.DirectoryPath)
                    : await ApplyReturnAsync(liveProject, recovery, receipt).ConfigureAwait(false);
                SaveReturnState(liveProject, recovery, receipt, result);
                _log.Write("Fenced return: " + result.Message);
                if (!result.Applied) returnWarning = result.Message;
            }
            catch (Exception ex)
            {
                _log.Write("Fenced return: " + ex.Message);
                returnWarning = "The return could not be completed. Its received files and VM disk were preserved."
                    + (recovery is null ? "" : " Recovery copy: " + recovery.DirectoryPath);
            }

            // The live status reader owns sign-in caching. A second shutdown
            // saver could overwrite a newer checkpoint with an earlier frame.

            try
            {
                var home = await status.RequestHomeAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (status.HomeTooBig)
                    _log.Write("Fenced session history was left as it was because it is over 256 MiB.");
                else if (home is null || home.Length == 0)
                    _log.Write("Fenced session history was left as it was.");
                else
                {
                    GuestHome.Save(projectKey, home);
                    _log.Write("Fenced session history saved.");
                }
            }
            catch
            {
                _log.Write("Fenced session history was left as it was.");
            }
            finally
            {
                status.Dispose();
            }

            StopMachine(qemu, qmpPort, sessionDir);
            NoteClosed(qemu.Id);
            }
            finally
            {
                heartbeatStop.Cancel();
                await heartbeat.ConfigureAwait(false);
            }
            // Recovery was persisted above. A modal warning must not keep the
            // VM/return owner alive or postpone home capture and disk shutdown.
            if (returnWarning is not null) await TellReturnAsync(returnWarning).ConfigureAwait(false);
        });
    }

    internal static async Task<bool> WaitForConsole(string sessionDir, int tuiPid, StatusLink status)
    {
        var done = Path.Combine(sessionDir, "console.done");
        while (true)
        {
            // Virtio terminal EOF can wait for QEMU shutdown. The parent has
            // reaped the agent and is about to export, so drain that export now.
            // Advisory Stop/SessionEnd hook records cannot enter this path.
            if (status.AgentExited || File.Exists(done))
                return true;

            if (!Alive(tuiPid))
                return false;

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static async Task KeepHostAliveAsync(string sessionDir, CancellationToken cancellationToken)
    {
        var alive = Path.Combine(sessionDir, "host.alive");
        var plain = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { File.WriteAllText(alive, "1", plain); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { /* The terminal falls back to independent recovery on a stale beat. */ }
                // Own return collection through apply and guest shutdown, not
                // just the wait for agent exit, to avoid a second TUI receiver.
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void StopMachine(Process process, int qmpPort, string sessionDirectory)
    {
        var owner = SessionGuardian.TryReadLiveOwner(sessionDirectory);
        var observed = MachineShutdown.WaitForGuestExit(process, qmpPort);
        if (owner is not null) SessionCompletion.RecordShutdown(sessionDirectory, owner, observed);
        if (observed)
            return;
        _log.Write("Guest shutdown could not be verified. Forced cleanup may leave incomplete VM writes; the session disk is preserved for recovery.");
        ConsoleSizeLink.Quit(qmpPort);
        StopProcess(process);
    }

    private static async Task<int> WaitForTuiPid(string pidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(pidFile))
                {
                    var text = File.ReadAllText(pidFile).Trim();
                    if (int.TryParse(text, out var pid) && pid > 0)
                        return pid;
                }
            }
            catch (IOException)
            {
                // The window is still writing the file.
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task WaitForTuiExit(int pid)
    {
        try
        {
            using var child = Process.GetProcessById(pid);
            while (!child.HasExited)
                await Task.Delay(200).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The window already closed.
        }
    }

    private static void StopProcess(Process process)
    {
        var id = 0;
        try
        {
            id = process.Id;
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process this start created is already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (id > 0)
                SessionSweep.StopAsLaunchAccount(id);
        }
        catch
        {
            // The process this start created is already gone.
        }
    }

    private static void RejectLivePath(IReadOnlyList<string> args, string liveProject)
    {
        if (QemuCommand.Mentions(args, liveProject))
            throw new InvalidOperationException("The fenced machine was pointed at the original folder.");
    }

    private static string ReadFsdevHelp(string qemu)
    {
        var start = new ProcessStartInfo
        {
            FileName = qemu,
            WorkingDirectory = Path.GetDirectoryName(qemu) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-fsdev");
        start.ArgumentList.Add("help");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("QEMU did not start.");
        var output = process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(); } catch { }
        }

        return output;
    }

    private static void RunTool(string exe, string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("qemu-img did not start.");
        if (!process.WaitForExit(60_000) || process.ExitCode != 0)
        {
            var error = "";
            try { error = process.StandardError.ReadToEnd(); } catch { }
            throw new InvalidOperationException("qemu-img failed. " + error);
        }
    }

    private static string StatePath(string liveProject)
    {
        return Path.Combine(QemuLayout.AsideRoot, QemuLayout.ProjectKey(liveProject), "state.json");
    }

    private string ProjectStatePath(string project) => _asideRoot is null ? StatePath(project)
        : Path.Combine(_asideRoot(), QemuLayout.ProjectKey(project), "state.json");

    private Task<ReturnApplyResult> ApplyReturnAsync(string liveProject, ReturnRecovery recovery, ProjectReturnReceipt receipt) =>
        ReturnApplier.ApplyAsync(liveProject, recovery, receipt, SentManifest.Load(recovery.Manifest),
            _returnScanner ?? new WindowsReturnFileScanner(),
            () => _returnBackup is null ? EnsureReturnBackupAsync(liveProject) : _returnBackup(liveProject));

    private async Task<bool> EnsureReturnBackupAsync(string liveProject)
    {
        var remote = ProjectReturnHost.ReadSavedRemote?.Invoke(liveProject);
        if (string.IsNullOrWhiteSpace(remote))
        {
            _log.Write("Optional Git backup is not configured; saving to Windows with local recovery.");
            return true;
        }

        if (!await Task.Run(() => GitBackup.TryPush(liveProject, remote, out _)).ConfigureAwait(false))
        {
            _log.Write("backup push failed");
            // A configured remote is extra protection. ReturnApplier still
            // verifies the scanner, host baseline and preserved local originals.
            // Do not hold those writes or VM shutdown on a modal warning.
            _ = TellReturnAsync("The optional Git backup did not complete. Saving to Windows will continue when its safety checks pass; local recovery copies are retained.");
            return true;
        }

        _log.Write("backup push succeeded");
        return true;
    }

    internal static void SaveReturnState(string project, ReturnRecovery recovery, ProjectReturnReceipt receipt, ReturnApplyResult result, string? statePath = null)
    {
        recovery.SaveTransfer(project, receipt);
        var path = statePath ?? StatePath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ReturnRecovery.SaveAtomic(path, new FenceStateFile
        {
            LiveProject = project, AsideDirectory = result.Applied ? "" : recovery.Payload,
            RecoveryDirectory = recovery.DirectoryPath, ReturnedFiles = receipt.Files.ToArray(), ReturnComplete = receipt.Complete,
            ScanIsStub = !result.Applied, ScanStatement = result.Message
        });
    }

    private async Task TellReturnAsync(string message)
    {
        try
        {
            if (ProjectReturnHost.TellAsync is { } tell) await tell(message).ConfigureAwait(false);
            else ProjectReturnHost.Tell?.Invoke(message);
        }
        catch (Exception error) { _log.Write("Return notification: " + error.Message); }
    }

    private static string SessionFolderName(string liveProject) => QemuLayout.ProjectKey(liveProject);

    private static bool OtherBoxesOpen(int pid)
    {
        lock (Gate)
        {
            Prune();
            foreach (var open in Boxes.Keys)
            {
                if (open != pid)
                    return true;
            }

            return false;
        }
    }

    private static void Prune()
    {
        var dead = new List<int>();
        foreach (var pair in Boxes)
        {
            if (!Alive(pair.Key))
                dead.Add(pair.Key);
        }

        foreach (var pid in dead)
        {
            ReservedBases.Remove(Boxes[pid].Qmp);
            Boxes.Remove(pid);
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int ReservePort()
    {
        // Windows hands out 49152-65535 as outbound source ports. Fence, status,
        // console and Windows test bridge are the next four ports, so a port from that range
        // makes QEMU fail the bind. These listeners stay below it.
        for (var port = PortChoice.First; port <= PortChoice.Last; port++)
        {
            if (PortChoice.Overlaps(port, ReservedBases))
                continue;
            if (PortSetIsFree(port))
                return port;
        }

        throw new IOException("The fenced session did not find a free port.");
    }

    private static bool PortSetIsFree(int port)
    {
        var held = new List<TcpListener>(PortChoice.Width);
        try
        {
            for (var offset = 0; offset < PortChoice.Width; offset++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port + offset);
                listener.Start();
                held.Add(listener);
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            foreach (var listener in held)
            {
                try
                {
                    listener.Stop();
                }
                catch (SocketException)
                {
                    // The probe is already closed.
                }
            }
        }
    }
}

public sealed class FenceStateFile
{
    public string LiveProject { get; set; } = "";
    public string AsideDirectory { get; set; } = "";
    public bool ScanIsStub { get; set; }
    public string ScanStatement { get; set; } = "";
    public string Accel { get; set; } = "";
    public string RecoveryDirectory { get; set; } = "";
    public string[] ReturnedFiles { get; set; } = Array.Empty<string>();
    public bool ReturnComplete { get; set; }
}
