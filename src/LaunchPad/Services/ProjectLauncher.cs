using System.Diagnostics;

namespace LaunchPad.Services;

public sealed class ProjectLauncher
{
    private readonly GrokLocator _locator;
    private readonly SetupLog _log;
    private readonly LaunchWatch _watch = new();
    private readonly Dictionary<int, Process> _watched = new();
    private readonly object _processGate = new();

    public ProjectLauncher(GrokLocator locator, SetupLog log)
    {
        _locator = locator;
        _log = log;
    }

    public string? AgentExecutable => _locator.FindGrokExecutable();

    public bool TryLaunch(string projectPath, out string error, LaunchPlacement? placement = null)
    {
        error = "";

        if (!Directory.Exists(projectPath))
        {
            error = "That project folder is no longer there.";
            return false;
        }

        var grok = _locator.FindGrokExecutable();
        if (grok is null)
        {
            error = "Grok Build isn’t ready yet. Check your internet and try again.";
            return false;
        }

        if (WasLaunched(projectPath))
        {
            _log.Write($"Grok Build is already open for {projectPath}");
            return true;
        }

        var generation = _watch.TryBegin(projectPath);
        if (generation == 0)
        {
            _log.Write($"Grok Build is already open for {projectPath}");
            return true;
        }

        try
        {
            var before = GrokProcesses.SnapshotIds();
            var directPid = 0;
            if (TryLaunchWithWindowsTerminal(grok, projectPath, placement))
            {
                WatchForGrok(projectPath, generation, before);
                _log.Write($"Opened Grok Build with Windows Terminal in {projectPath}");
                return true;
            }

            if (TryLaunchDirect(grok, projectPath, out directPid))
            {
                if (directPid > 0)
                    Attach(projectPath, generation, directPid);
                else
                    WatchForGrok(projectPath, generation, before);

                _log.Write($"Opened Grok Build in {projectPath}");
                return true;
            }

            _watch.CancelStart(projectPath, generation);
            error = "Couldn’t open Grok Build. Try again.";
            return false;
        }
        catch (Exception ex)
        {
            _watch.CancelStart(projectPath, generation);
            _log.Write("Launch failed: " + ex.Message);
            error = "Couldn’t open Grok Build. Try again.";
            return false;
        }
    }

    public bool WasLaunched(string projectPath)
    {
        _watch.PruneDead(projectPath, GrokProcesses.IsRunning);
        return _watch.IsOpen(projectPath);
    }

    public (bool Starting, int[] ProcessIds)? Describe(string projectPath) => _watch.Describe(projectPath);

    public (int Pid, long StartTicks)? TrackedProcess(string projectPath)
    {
        if (_watch.Describe(projectPath) is not { } launch) return null;
        lock (_processGate)
            foreach (var pid in launch.ProcessIds)
                if (_watched.TryGetValue(pid, out var process))
                {
                    try { if (!process.HasExited) return (pid, process.StartTime.ToUniversalTime().Ticks); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
        return null;
    }

    private void WatchForGrok(string projectPath, int generation, HashSet<int> before)
    {
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var pid in GrokProcesses.FindNewInDirectory(before, projectPath))
                    Attach(projectPath, generation, pid);

                await Task.Delay(250).ConfigureAwait(false);
            }

            _watch.CancelStart(projectPath, generation);
        });
    }

    private void Attach(string projectPath, int generation, int pid)
    {
        if (!_watch.NotePid(projectPath, generation, pid))
            return;

        try
        {
            var process = Process.GetProcessById(pid);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Finish(projectPath, generation, pid, process);
            lock (_processGate)
                _watched[pid] = process;

            if (process.HasExited)
                Finish(projectPath, generation, pid, process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (!GrokProcesses.IsRunning(pid))
                _watch.NoteExited(projectPath, generation, pid);
        }
    }

    private void Finish(string projectPath, int generation, int pid, Process process)
    {
        _watch.NoteExited(projectPath, generation, pid);
        lock (_processGate)
        {
            if (_watched.Remove(pid))
                process.Dispose();
        }
    }

    private bool TryLaunchWithWindowsTerminal(string grokExe, string projectPath, LaunchPlacement? placement)
    {
        var wt = FindWindowsTerminal();
        if (wt is null)
            return false;

        // Do not use --focus: in Windows Terminal that means focus mode,
        // which hides the title bar and window controls.
        // wt.exe exits on its own. The stored pid is the grok process, not wt.
        var nearLauncher = placement is null
            ? ""
            : $"--pos {placement.X},{placement.Y} --size {placement.Columns},{placement.Rows} ";

        var cleanArgs = "-w new " + nearLauncher +
                        "new-tab --useApplicationTitle --title \"Grok Build\" --tabColor #F07828 " +
                        $"-d \"{projectPath}\" -- \"{grokExe}\"";
        var titledArgs = "-w new new-tab --useApplicationTitle --title \"Grok Build\" --tabColor #F07828 " +
                         $"-d \"{projectPath}\" -- \"{grokExe}\"";
        var simpleArgs = $"-d \"{projectPath}\" -- \"{grokExe}\"";

        if (TryStartWindowsTerminal(wt, cleanArgs, projectPath))
            return true;

        if (TryStartWindowsTerminal(wt, titledArgs, projectPath))
            return true;

        if (TryStartWindowsTerminal(wt, simpleArgs, projectPath))
            return true;

        return false;
    }

    private bool TryStartWindowsTerminal(string wt, string arguments, string projectPath)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = wt,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = projectPath,
                WindowStyle = ProcessWindowStyle.Normal
            };

            using var process = Process.Start(start);
            return process is not null || File.Exists(wt);
        }
        catch (Exception ex)
        {
            _log.Write("Windows Terminal launch failed: " + ex.Message);
            return false;
        }
    }

    private bool TryLaunchDirect(string grok, string projectPath, out int pid)
    {
        pid = 0;
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = grok,
            WorkingDirectory = projectPath,
            UseShellExecute = true
        });

        if (process is null)
            return false;

        try
        {
            if (GrokProcesses.IsGrokProcessName(process.ProcessName) && process.Id > 0)
                pid = process.Id;
        }
        catch
        {
            pid = 0;
        }

        return true;
    }

    private static string? FindWindowsTerminal()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(alias))
            return alias;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, "wt.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}

public sealed record LaunchPlacement(int X, int Y, int Columns, int Rows);
