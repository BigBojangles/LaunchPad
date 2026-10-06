using System.Diagnostics;
using System.Text;
using LaunchPad.Services;

namespace LaunchPad.Services.Fence;

public static class SessionEnd
{
    public static bool HostStillWatching(DateTime writtenUtc, DateTime nowUtc)
    {
        var age = nowUtc - writtenUtc;
        return age < TimeSpan.FromSeconds(3) && age > TimeSpan.FromSeconds(-2);
    }

    public static void AfterConsole(string pidFile)
    {
        var dir = Path.GetDirectoryName(pidFile);
        if (string.IsNullOrEmpty(dir))
            return;

        try
        {
            if (Watching(dir) && File.Exists(Path.Combine(dir, "qemu.pid")))
            {
                File.WriteAllText(Path.Combine(dir, "console.done"), "1", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (WaitUntilMachineStops(dir, TimeSpan.FromMinutes(3)))
                    return;
            }

            DrainAndQuit(dir);
        }
        catch (Exception ex)
        {
            try
            {
                new SetupLog(new AppPaths()).Write("Fenced session end: " + ex.Message);
            }
            catch
            {
                // The job still ends the machine when this process exits.
            }
        }
    }

    private static bool Watching(string dir)
    {
        var path = Path.Combine(dir, "host.alive");
        if (!File.Exists(path))
            return false;

        return HostStillWatching(File.GetLastWriteTimeUtc(path), DateTime.UtcNow);
    }

    private static bool WaitUntilMachineStops(string dir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!MachineAlive(dir))
                return true;

            if (!Watching(dir))
                return false;

            Thread.Sleep(200);
        }

        return !MachineAlive(dir);
    }

    private static bool MachineAlive(string dir)
    {
        var path = Path.Combine(dir, "qemu.pid");
        if (!File.Exists(path))
            return false;

        try
        {
            var text = File.ReadAllText(path).Trim();
            if (!int.TryParse(text, out var pid) || pid <= 0)
                return false;

            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void DrainAndQuit(string dir)
    {
        if (!int.TryParse(ReadText(Path.Combine(dir, "qmp.txt")), out var qmp) || qmp <= 0)
        {
            new SetupLog(new AppPaths()).Write("Fenced session ended without a monitor port. The session disk was left as it was.");
            return;
        }

        var owner = SessionGuardian.TryReadLiveOwner(dir);
        using var terminal = Process.GetCurrentProcess();
        if (owner?.TerminalPid != terminal.Id || owner.TerminalStartTicks != terminal.StartTime.ToUniversalTime().Ticks) owner = null;
        try
        {
            // The Windows project is updated only while LaunchPad is still open to take the backup.
            var recovery = ReturnRecovery.Create(dir);
            SentManifest.Load(Path.Combine(dir, "sent.manifest")).Save(recovery.Manifest);
            var receipt = ProjectPull.Receive(QemuCommand.FencePort(qmp), recovery.Payload, TimeSpan.FromSeconds(20));
            if (owner is not null)
            {
                recovery.SaveTransfer(owner.ProjectPath ?? "", receipt);
                SessionCompletion.RecordReturn(dir, owner, receipt, recovery.DirectoryPath);
                if (owner.ProjectPath is { } project)
                    FenceSession.SaveReturnState(project, recovery, receipt,
                        new ReturnApplyResult(false, "LaunchPad was closed. Received files were preserved for recovery at " + recovery.DirectoryPath));
            }
            recovery.Record(receipt.Complete ? "waiting" : "incomplete", receipt.Error ?? "LaunchPad was closed. The Windows project was left unchanged.");
            new SetupLog(new AppPaths()).Write("Fenced terminal return preserved at " + recovery.DirectoryPath + ". The project folder was left unchanged.");
        }
        catch (Exception ex)
        {
            new SetupLog(new AppPaths()).Write("Fenced session end: " + ex.Message);
        }

        try
        {
            if (int.TryParse(ReadText(Path.Combine(dir, "qemu.pid")), out var machinePid) && machinePid > 0)
            {
                using var machine = Process.GetProcessById(machinePid);
                var exited = MachineShutdown.WaitForGuestExit(machine, qmp);
                if (owner is not null) SessionCompletion.RecordShutdown(dir, owner, exited);
                if (exited)
                    return;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A crashed or already-ended VM is not a verified guest shutdown.
        }
        new SetupLog(new AppPaths()).Write("Guest shutdown could not be verified. Forced terminal cleanup may leave incomplete VM writes; the session disk is preserved for recovery.");
        ConsoleSizeLink.Quit(qmp);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && MachineAlive(dir))
            Thread.Sleep(200);
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }
}
