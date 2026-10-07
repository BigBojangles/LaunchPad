using System.Diagnostics;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed class ProjectLauncher
{
    private readonly AppPaths _paths;
    private readonly NativeAgentLocator _locator;
    private readonly SetupLog _log;
    public ProjectLauncher(GrokLocator locator, SetupLog log)
    {
        _paths = locator.Paths;
        _locator = new NativeAgentLocator(locator, _paths);
        _log = log;
    }
    public string? AgentExecutable => _locator.Find(AgentLaunch.Grok);
    public string? FindAgent(AgentLaunch agent) => _locator.Find(agent);
    public string? SessionDirectory(string project)
        => FenceFiles.TryResolveUnlinked(_paths.AppDataDir, "windows/native/" + QemuLayout.ProjectKey(project), out var directory) ? directory : null;
    public NativeLaunchRecord? Record(string project)
    {
        var record = SessionDirectory(project) is { } directory ? NativeAgentTerminal.Read(directory) : null;
        return record is not null && string.Equals(Path.GetFullPath(project), record.Project, StringComparison.OrdinalIgnoreCase) ? record : null;
    }
    public bool WasLaunched(string project) => LiveRecord(project) is not null;
    public (bool Starting, int[] ProcessIds)? Describe(string project)
        => LiveRecord(project) is { } record ? (record.State == "starting", new[] { record.Pid }) : null;
    public (int Pid, long StartTicks)? TrackedProcess(string project)
        => LiveRecord(project) is { Pid: > 0 } record ? (record.Pid, record.StartTicks) : null;
    private NativeLaunchRecord? LiveRecord(string project)
    {
        var record = Record(project);
        if (record is { Pid: > 0, StartTicks: > 0, State: "starting" or "running" }
            && WindowsSessionWindow.MatchesProcess(record.Pid, record.StartTicks)) return record;
        // Reserve startup before the helper publishes its PID; stale requests expire.
        if (record is { State: "starting", Pid: 0 } && SessionDirectory(project) is { } directory
            && FenceFiles.TryResolveUnlinked(directory, NativeAgentTerminal.RecordFile, out var file))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
            if (age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(15)) return record;
        }
        return null;
    }
    public bool TryLaunch(string project, out string error, LaunchPlacement? placement = null)
        => TryLaunch(project, AgentLaunch.Grok, out error, placement);
    public bool TryLaunch(string project, AgentLaunch agent, out string error, LaunchPlacement? placement = null)
    {
        error = "";
        if (!Directory.Exists(project)) { error = "That project folder is no longer there."; return false; }
        if (LiveRecord(project) is not null) return true;
        if (_locator.Find(agent) is not { } program)
        {
            error = $"{AgentChoice.Find(agent.Id)?.Label ?? agent.Id} is not installed for native Windows use. Install its Windows CLI and sign in, or choose a Windows program in the project Agent menu.";
            return false;
        }
        if (SessionDirectory(project) is not { } directory) { error = "Native session storage is unavailable."; return false; }
        string? requestedGeneration = null;
        try
        {
            Directory.CreateDirectory(directory);
            using var startGate = new Mutex(false, "Local\\LaunchPad.Native.Start." + QemuLayout.ProjectKey(directory));
            bool held;
            try { held = startGate.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) { error = "A native launch is already being prepared for this project."; return false; }
            try
            {
                if (LiveRecord(project) is not null) return true;
                var request = new NativeLaunchRecord(Path.GetFullPath(project), agent.Id, program, Guid.NewGuid().ToString("N"),
                    AppDataDirectory: _paths.AppDataDir);
                requestedGeneration = request.Generation;
                NativeAgentTerminal.Save(directory, request);
                TuiWindow.SaveDisplayTitle(directory, new SettingsStore(_paths).DisplayNameFor(project, "host:" + QemuLayout.ProjectKey(project)));
                var start = new ProcessStartInfo(_paths.ExePath) { UseShellExecute = false, WorkingDirectory = project };
                start.ArgumentList.Add(NativeAgentTerminal.Argument); start.ArgumentList.Add(directory);
                using var process = Process.Start(start) ?? throw new IOException("The native terminal did not start.");
                // The helper publishes running only after the actual agent starts.
                // Do not overwrite its state from this desktop process.
                _log.Write("Opened native " + agent.Id + " in " + project);
                return true;
            }
            finally { startGate.ReleaseMutex(); }
        }
        catch (Exception failure)
        {
            // Retire only our unstarted request; do not replace a newer/live helper's record.
            try
            {
                if (NativeAgentTerminal.Read(directory) is { State: "starting", Pid: 0 } pending && pending.Generation == requestedGeneration)
                    NativeAgentTerminal.Save(directory, pending with { State = "failed", Error = failure.Message });
            }
            catch { }
            _log.Write("Native launch failed: " + failure.Message); error = failure.Message; return false;
        }
    }
}

public sealed record LaunchPlacement(int X, int Y, int Columns, int Rows);
