using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record NativeLaunchRecord(string Project, string Agent, string Program, string Generation,
    int Pid = 0, long StartTicks = 0, string State = "starting", int? ExitCode = null, string? Error = null,
    int AgentPid = 0, long AgentStartTicks = 0, string? AppDataDirectory = null);

/// <summary>A separate console owns the native agent; closing the desktop does not own its lifetime.</summary>
public static class NativeAgentTerminal
{
    public const string Argument = "--native-agent";
    public const string RecordFile = "native-session.json";
    public static bool IsRequest(string[] args) => args.Length == 2 && args[0] == Argument;

    public static NativeLaunchRecord? Read(string directory)
    {
        try
        {
            if (!FenceFiles.TryResolveUnlinked(directory, RecordFile, out var file) || !File.Exists(file)
                || new FileInfo(file).Length > 16384) return null;
            var record = System.Text.Json.JsonSerializer.Deserialize<NativeLaunchRecord>(File.ReadAllText(file), JsonFile.Options);
            return record is not null && Path.IsPathFullyQualified(record.Project ?? "")
                && Path.IsPathFullyQualified(record.Program ?? "") && NativeAgentLocator.Supported(record.Program ?? "")
                && AgentChoice.Known(record.Agent) && Guid.TryParseExact(record.Generation, "N", out _)
                && record.State is "starting" or "running" or "stopped" or "failed" ? record : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return null; }
    }

    public static void Save(string directory, NativeLaunchRecord record)
    {
        if (!FenceFiles.TryResolveUnlinked(directory, RecordFile, out var file)) throw new IOException("Native session path is unavailable.");
        ReturnRecovery.SaveAtomic(file, record);
    }

    public static bool IsLive(NativeLaunchRecord? record) => record is { Pid: > 0, StartTicks: > 0, State: "running" }
        && WindowsSessionWindow.MatchesProcess(record.Pid, record.StartTicks);

    public static ProcessStartInfo AgentStart(NativeLaunchRecord record)
    {
        if (!NativeAgentLocator.Supported(record.Program) || !Path.IsPathFullyQualified(record.Program))
            throw new ArgumentException("Choose a Windows executable or command file.");
        var start = new ProcessStartInfo { WorkingDirectory = record.Project, UseShellExecute = false };
        if (Path.GetExtension(record.Program).Equals(".exe", StringComparison.OrdinalIgnoreCase)) start.FileName = record.Program;
        else
        {
            // The executable path is data, never interpolated into shell source.
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("& $env:LAUNCHPAD_NATIVE_PROGRAM; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE } else { exit 0 }");
            start.Environment["LAUNCHPAD_NATIVE_PROGRAM"] = record.Program;
        }
        return start;
    }

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsRequest(args)) return 1;
        var directory = args[1];
        var record = Read(directory);
        if (record is null || record.State != "starting" || !Guid.TryParseExact(record.Generation, "N", out _)
            || !Directory.Exists(record.Project) || !File.Exists(record.Program)) return 1;
        using var self = Process.GetCurrentProcess();
        using var ownership = new Mutex(false, "Local\\LaunchPad.Native." + QemuLayout.ProjectKey(directory));
        bool held;
        try { held = ownership.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) return 1;
        try
        {
            FreeConsole();
            if (!AllocConsole()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            TuiWindow.BindConsole("LaunchPad native");
            Console.WriteLine("Native mode: the agent works directly in your Windows project with your account permissions.");
            record = record with { Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks };
            Save(directory, record);
            var paths = new AppPaths(appDataDir: record.AppDataDirectory);
            using var activity = NativeGrokActivity.TryCreate(directory, record, paths);
            using var claudeActivity = NativeClaudeActivity.TryCreate(directory, record, paths);
            var agentStart = AgentStart(record);
            activity?.Configure(agentStart);
            claudeActivity?.Configure(agentStart);
            using var child = Process.Start(agentStart) ?? throw new IOException("The native agent did not start.");
            try
            {
                record = record with { State = "running", AgentPid = child.Id, AgentStartTicks = child.StartTime.ToUniversalTime().Ticks };
                Save(directory, record);
                try { activity?.Attach(child); } catch { /* Telemetry cannot stop the agent. */ }
                try { claudeActivity?.Attach(child); } catch { /* Telemetry cannot stop the agent. */ }
                try { NotificationDeliveryOwner.StartIfEnabled(paths); } catch { /* Optional delivery cannot affect the agent. */ }
                var watch = new Thread(() => WindowsSessionWindow.Watch(directory, record.Pid, record.StartTicks)) { IsBackground = true };
                watch.Start();
                while (!child.WaitForExit(250)) { activity?.Poll(true); claudeActivity?.Poll(true); }
                activity?.Poll(false);
                claudeActivity?.Poll(false);
                Save(directory, record with { State = "stopped", ExitCode = child.ExitCode });
                return child.ExitCode;
            }
            catch
            {
                // This helper created this child. A lost owner record cannot leave it unmanaged.
                try { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } } catch { }
                throw;
            }
        }
        catch (Exception error)
        {
            try { Save(directory, record with { State = "failed", Error = error.Message }); } catch { }
            try { Console.Error.WriteLine(error.Message); } catch { }
            return 1;
        }
        finally { ownership.ReleaseMutex(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
}
