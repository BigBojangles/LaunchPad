using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record SessionOwnerIdentity(int OwnerPid, long OwnerStartTicks, int MachinePid, long MachineStartTicks,
    int QmpPort, string Generation, string AgentId, string? ProjectPath, int? TerminalPid = null, long? TerminalStartTicks = null,
    int? DesktopPid = null, long? DesktopStartTicks = null);

/// <summary>Owns the Windows job while watching the terminal, independently of the desktop UI.</summary>
public static class SessionGuardian
{
    public const string Argument = "--session-owner";
    private const string IdentityFile = "session-owner.json";
    private const string ArmedFile = "session-owner.armed";
    public const string ResultFile = "session-owner-result.json";

    public static bool IsRequest(string[] args) => args.Length is 8 or 10 && args[0] == Argument;

    public static async Task<Process> StartAsync(Process machine, int qmpPort, string sessionDirectory,
        string agentId = AgentChoice.Grok, string? project = null, string? executable = null, LaunchPad.Models.ProjectPermissionPolicy? appliedPolicy = null)
    {
        project = string.IsNullOrWhiteSpace(project) ? null : Path.GetFullPath(project);
        if (appliedPolicy is not null) PermissionPolicies.RequireLaunchSupport(appliedPolicy);
        if (TryReadLiveOwner(sessionDirectory) is not null)
            throw new InvalidOperationException("This session still has a live terminal owner.");
        foreach (var file in new[] { IdentityFile, ArmedFile, ResultFile, "tui.pid", "console.done", "console.finished", "console.ready", "host.alive" })
            File.Delete(Path.Combine(sessionDirectory, file));
        SessionCompletion.Clear(sessionDirectory);
        var generation = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable ?? Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true };
        using var desktop = Process.GetCurrentProcess();
        foreach (var value in new[] { Argument, Path.GetFullPath(sessionDirectory), machine.Id.ToString(CultureInfo.InvariantCulture),
            machine.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), qmpPort.ToString(CultureInfo.InvariantCulture),
            generation, AgentChoice.Normalize(agentId), project ?? "", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            desktop.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
        var owner = Process.Start(start) ?? throw new IOException("The terminal owner did not start.");
        try
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (owner.HasExited) throw new IOException("The terminal owner exited before handoff.");
                var identity = ReadIdentity(sessionDirectory);
                if (identity is not null && identity.OwnerPid == owner.Id && identity.Generation == generation)
                {
                    var expected = new SessionOwnerIdentity(owner.Id, owner.StartTime.ToUniversalTime().Ticks, machine.Id,
                        machine.StartTime.ToUniversalTime().Ticks, qmpPort, generation, AgentChoice.Normalize(agentId), project,
                        DesktopPid: desktop.Id, DesktopStartTicks: desktop.StartTime.ToUniversalTime().Ticks);
                    RequireMatchingHandoff(identity, expected);
                    if (!TestUserRunner.HandMachineTo(machine.Id, owner.Id))
                        throw new IOException("The VM job could not be handed to its terminal owner.");
                    PermissionPolicies.Begin(new AppPaths(), expected, appliedPolicy ?? LaunchPad.Models.ProjectPermissionPolicy.Standard);
                    WriteJson(Path.Combine(sessionDirectory, ArmedFile), generation);
                    return owner;
                }
                await Task.Delay(40).ConfigureAwait(false);
            }
            throw new IOException("The terminal owner did not become ready.");
        }
        catch
        {
            // Let the owner observe the failed handoff and perform bounded cleanup;
            // killing a successfully handed-off owner would kill QEMU immediately.
            owner.Dispose();
            throw;
        }
    }

    public static void RequireMatchingHandoff(SessionOwnerIdentity actual, SessionOwnerIdentity expected)
    {
        // The writable VM directory is an observation channel, never the
        // authority for a host-owned permission grant or process identity.
        if (actual != expected) throw new InvalidDataException("The terminal-owner handoff identity does not match this launch. Existing VM state was preserved.");
    }

    public static void Run(string[] args)
    {
        if (!IsRequest(args)) return;
        var directory = Path.GetFullPath(args[1]);
        if (!int.TryParse(args[2], out var machinePid) || !long.TryParse(args[3], out var machineTicks)
            || !int.TryParse(args[4], out var qmp) || qmp < PortChoice.First || qmp > PortChoice.Last
            || !Guid.TryParseExact(args[5], "N", out _)) return;
        using var owner = Process.GetCurrentProcess();
        Process? machine = null;
        using var windowsTestStop = new CancellationTokenSource();
        Task? windowsTests = null;
        SessionOwnerIdentity? permissionOwner = null;
        try
        {
            machine = OpenMatchingProcess(machinePid, machineTicks);
            if (machine is null || !machine.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)) return;
            var identity = new SessionOwnerIdentity(owner.Id, owner.StartTime.ToUniversalTime().Ticks, machinePid, machineTicks,
                qmp, args[5], AgentChoice.Normalize(args[6]), string.IsNullOrWhiteSpace(args[7]) ? null : args[7],
                DesktopPid: args.Length == 10 && int.TryParse(args[8], out var desktopPid) ? desktopPid : null,
                DesktopStartTicks: args.Length == 10 && long.TryParse(args[9], out var desktopTicks) ? desktopTicks : null);
            WriteJson(Path.Combine(directory, IdentityFile), identity);
            permissionOwner = identity;
            var wait = Stopwatch.StartNew();
            while (!machine.HasExited && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (ReadGeneration(directory) == identity.Generation) break;
                Thread.Sleep(40);
            }
            var armed = ReadGeneration(directory) == identity.Generation;
            if (armed) windowsTests = Task.Run(() => WindowsTestSessionHost.RunAsync(directory, identity, machine, windowsTestStop.Token));
            using var terminal = armed ? WaitForTerminal(directory, machine, owner.StartTime.ToUniversalTime()) : null;
            if (terminal is not null)
            {
                identity = identity with { TerminalPid = terminal.Id, TerminalStartTicks = terminal.StartTime.ToUniversalTime().Ticks };
                WriteJson(Path.Combine(directory, IdentityFile), identity);
                try { NotificationDeliveryOwner.StartIfEnabled(new AppPaths()); }
                catch { /* Optional paging cannot affect the terminal or VM. */ }
                using var controlStop = new CancellationTokenSource();
                Task? control = null;
                try
                {
                    while (!machine.HasExited && !terminal.HasExited)
                    {
                        if (File.Exists(Path.Combine(directory, "console.finished"))) { controlStop.Cancel(); windowsTestStop.Cancel(); }
                        else if (control is null && !DesktopAlive(identity, directory))
                            control = KeepControlsAsync(directory, identity, controlStop.Token);
                        Thread.Sleep(200);
                    }
                }
                finally
                {
                    controlStop.Cancel();
                    windowsTestStop.Cancel();
                    if (control is not null) try { control.GetAwaiter().GetResult(); } catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
                }
            }
            if (machine.HasExited)
            {
                var clean = SessionCompletion.WaitForShutdownEvidence(directory, identity);
                WriteResult(clean ? "guest-shutdown" : "machine-ended", clean, SessionCompletion.RecoveryDirectory(directory, identity),
                    !clean || !SessionCompletion.Returned(directory, identity) || ProjectSessionStore.HasUnconfirmedImport(directory));
                return;
            }

            // A dead terminal must not close the last job handle before Linux
            // flushes its disk. End the agent through its existing console input.
            ReturnRecovery? recovery = null;
            ProjectReturnReceipt? receipt = null;
            if (terminal is not null)
            {
                try
                {
                    using var console = new TcpClient();
                    if (console.ConnectAsync("127.0.0.1", QemuCommand.TuiPort(qmp)).Wait(TimeSpan.FromSeconds(2)))
                        console.GetStream().WriteByte(3);
                    recovery = ReturnRecovery.Create(directory);
                    SentManifest.Load(Path.Combine(directory, "sent.manifest")).Save(recovery.Manifest);
                    receipt = ProjectPull.Receive(QemuCommand.FencePort(qmp), recovery.Payload, TimeSpan.FromSeconds(5));
                    recovery.Record(receipt.Complete ? "waiting" : "incomplete",
                        receipt.Error ?? "The terminal closed. The Windows project was left unchanged.");
                    recovery.SaveTransfer(identity.ProjectPath ?? "", receipt);
                    SessionCompletion.RecordReturn(directory, identity, receipt, recovery.DirectoryPath);
                    if (identity.ProjectPath is { } project)
                        FenceSession.SaveReturnState(project, recovery, receipt,
                            new ReturnApplyResult(false, "The terminal closed. Received files were preserved for recovery at " + recovery.DirectoryPath));
                }
                catch (Exception error) when (error is IOException or SocketException or AggregateException or UnauthorizedAccessException)
                {
                    // The VM disk remains the recovery copy if the return is incomplete.
                }
            }
            var guestExited = MachineShutdown.WaitForGuestExit(machine, qmp);
            SessionCompletion.RecordShutdown(directory, identity, guestExited);
            WriteResult(guestExited ? "guest-shutdown" : "shutdown-unverified", guestExited, recovery?.DirectoryPath,
                !guestExited || receipt?.Complete != true || ProjectSessionStore.HasUnconfirmedImport(directory));
            if (!guestExited)
            {
                ConsoleSizeLink.Quit(qmp);
                if (!machine.WaitForExit(5000)) SessionSweep.StopAsLaunchAccount(machine.Id);
            }

            void WriteResult(string outcome, bool guestExited, string? recoveryDirectory, bool needsRecovery) =>
                WriteJson(Path.Combine(directory, ResultFile), new
                {
                    outcome, guestShutdownObserved = guestExited, recoveryDirectory, needsRecovery,
                    recordedUtc = DateTime.UtcNow, identity
                });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Attempt a guest shutdown even when owner metadata cannot be written.
            if (machine is not null) _ = MachineShutdown.WaitForGuestExit(machine, qmp);
        }
        finally
        {
            windowsTestStop.Cancel();
            if (windowsTests is not null)
                try { windowsTests.Wait(TimeSpan.FromSeconds(8)); }
                catch (AggregateException) { /* Bridge faults do not replace guest shutdown/recovery. */ }
            if (permissionOwner is not null)
                try { PermissionPolicies.End(new AppPaths(), permissionOwner); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                { /* Generation/owner/expiry checks still refuse grants on a stopped session. */ }
            machine?.Dispose();
        }
    }

    private static bool DesktopAlive(SessionOwnerIdentity identity, string directory)
    {
        if (identity.DesktopPid is not int pid || identity.DesktopStartTicks is not long ticks)
        {
            var path = Path.Combine(directory, "host.alive");
            return !File.Exists(Path.Combine(directory, "console.ready")) || (File.Exists(path) && SessionEnd.HostStillWatching(File.GetLastWriteTimeUtc(path), DateTime.UtcNow));
        }
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (ArgumentException) { return false; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
    private static async Task KeepControlsAsync(string directory, SessionOwnerIdentity identity, CancellationToken token)
    {
        var resize = SessionSizeRelay.RunAsync(directory, identity.QmpPort, token);
        var status = DrainStatusAsync();
        await Task.WhenAll(resize, status).ConfigureAwait(false);
        async Task DrainStatusAsync()
        {
            using var client = await FenceHost.ConnectAsync(QemuCommand.StatusPort(identity.QmpPort), token).ConfigureAwait(false);
            using var link = new StatusLink(client, new SessionActivityContext(directory, identity.Generation, identity.ProjectPath, identity.AgentId));
            while (!token.IsCancellationRequested && link.Snapshot.Connected) await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    public static SessionOwnerIdentity? TryReadLiveOwner(string directory)
    {
        var identity = ReadIdentity(directory);
        if (identity is null) return null;
        using var owner = OpenMatchingProcess(identity.OwnerPid, identity.OwnerStartTicks);
        using var machine = OpenMatchingProcess(identity.MachinePid, identity.MachineStartTicks);
        return owner is not null && machine is not null && owner.ProcessName.Equals("LaunchPad", StringComparison.OrdinalIgnoreCase)
            && machine.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase) ? identity : null;
    }

    public static bool NeedsRecovery(string directory, bool includePreserved = true)
    {
        if (ProjectSessionStore.HasUnconfirmedImport(directory, includePreserved)) return true;
        try
        {
            var path = Path.Combine(directory, ResultFile);
            if (!File.Exists(path)) return File.Exists(Path.Combine(directory, IdentityFile));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("needsRecovery").GetBoolean()
                || document.RootElement.GetProperty("outcome").GetString() is "shutdown-unverified" or "machine-ended";
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return true; }
    }

    private static Process? WaitForTerminal(string directory, Process machine, DateTime ownerStarted)
    {
        var clock = Stopwatch.StartNew();
        while (!machine.HasExited && clock.Elapsed < TimeSpan.FromSeconds(45))
        {
            try
            {
                if (int.TryParse(File.ReadAllText(Path.Combine(directory, "tui.pid")), out var pid) && pid > 0)
                {
                    var terminal = Process.GetProcessById(pid);
                    if (!terminal.HasExited && terminal.StartTime.ToUniversalTime() >= ownerStarted.AddSeconds(-1)
                        && terminal.ProcessName.Equals("LaunchPad", StringComparison.OrdinalIgnoreCase)) return terminal;
                    terminal.Dispose();
                }
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Thread.Sleep(100);
        }
        return null;
    }

    private static Process? OpenMatchingProcess(int pid, long started)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (!process.WaitForExit(0) && process.StartTime.ToUniversalTime().Ticks == started) return process;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }
    private static SessionOwnerIdentity? ReadIdentity(string directory)
    {
        try { return JsonSerializer.Deserialize<SessionOwnerIdentity>(File.ReadAllText(Path.Combine(directory, IdentityFile))); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private static string? ReadGeneration(string directory)
    {
        try { return JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(directory, ArmedFile))); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }
    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
