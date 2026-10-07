using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private string? _settingsHash, _projectsHash;

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.AppDataDir);
        var settings = JsonFile.Load<AppSettings>(paths.SettingsFile);
        Current = settings.Value;
        _settingsHash = settings.Hash;
        var loaded = Current.LastOpened ?? new Dictionary<string, DateTimeOffset>();
        Current.LastOpened = new Dictionary<string, DateTimeOffset>(loaded, StringComparer.OrdinalIgnoreCase);
        var remotes = Current.BackupRemotes ?? new Dictionary<string, string>();
        Current.BackupRemotes = new Dictionary<string, string>(remotes, StringComparer.OrdinalIgnoreCase);
        var projectMemory = Current.ProjectMemoryMb ?? new Dictionary<string, int>();
        Current.ProjectMemoryMb = new Dictionary<string, int>(projectMemory, StringComparer.OrdinalIgnoreCase);
        Current.ProjectAgent = CaseMap(Current.ProjectAgent);
        Current.ProjectLaunchMode = CaseMap(Current.ProjectLaunchMode);
        Current.ProjectWindowsTestPermission = CaseMap(Current.ProjectWindowsTestPermission);
        Current.ProjectPermissions = new(Current.ProjectPermissions ?? [], StringComparer.OrdinalIgnoreCase);
        Current.ProjectAgentProgram = CaseMap(Current.ProjectAgentProgram);
        Current.ProjectAgentSource = CaseMap(Current.ProjectAgentSource);
        var projects = JsonFile.Load<List<KnownProject>>(paths.ProjectsFile);
        KnownProjects = projects.Value;
        _projectsHash = projects.Hash;
        foreach (var project in KnownProjects)
        {
            if (project is null) throw new IOException("LaunchPad could not read projects.json: a saved project record is empty. The original file was preserved.");
            project.SessionNames = CaseMap(project.SessionNames);
        }
        Current.SeenTips = new HashSet<string>(Current.SeenTips ?? [], StringComparer.Ordinal);
    }

    public AppSettings Current { get; private set; }
    public AppPaths Paths => _paths;
    public List<KnownProject> KnownProjects { get; private set; }
    public event Action<string>? DisplayNamesChanged;
    public event Action? PreferencesChanged;

    public static string FolderName(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var name = Path.GetFileName(full);
        return string.IsNullOrEmpty(name) ? full : name;
    }

    public string DisplayNameFor(string path, string? sessionId = null)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var project = KnownProjects.FirstOrDefault(item => MatchesProject(item.Path, full));
            if (sessionId is not null && project?.SessionNames.TryGetValue(sessionId, out var sessionName) == true && !string.IsNullOrWhiteSpace(sessionName)) return sessionName;
            return !string.IsNullOrWhiteSpace(project?.DisplayName) ? project.DisplayName : FolderName(full);
        }
    }

    public void SaveDisplayName(string path, string? name, string? sessionId = null)
    {
        name = name?.Trim();
        if (name is { Length: > 512 } || name?.Any(char.IsControl) == true) throw new ArgumentException("Use a display name of up to 512 characters without control characters.");
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var project = KnownProjects.FirstOrDefault(item => MatchesProject(item.Path, full));
            var added = project is null;
            project ??= new KnownProject { Name = FolderName(full), Path = full };
            var previous = project.DisplayName;
            var previousSessions = new Dictionary<string, string>(project.SessionNames, StringComparer.OrdinalIgnoreCase);
            if (sessionId is null) project.DisplayName = string.IsNullOrEmpty(name) ? null : name;
            else if (string.IsNullOrEmpty(name)) project.SessionNames[sessionId] = FolderName(full);
            else project.SessionNames[sessionId] = name;
            if (added) KnownProjects.Add(project);
            try { SaveKnownProjects(); }
            catch { project.DisplayName = previous; project.SessionNames = previousSessions; if (added) KnownProjects.Remove(project); throw; }
        }
        DisplayNamesChanged?.Invoke(full);
    }

    public void ResetDisplayName(string path, string? sessionId = null)
    {
        // An individual reset uses the real folder label even when its project
        // has an alias; project reset removes the alias entirely.
        SaveDisplayName(path, sessionId is null ? null : FolderName(path), sessionId);
    }

    public bool TakeTip(string id)
    {
        lock (_gate)
        {
            if (!Current.ShowTips || Current.SeenTips.Contains(id)) return false;
            Current.SeenTips.Add(id);
            try { SaveSettings(); } catch { Current.SeenTips.Remove(id); throw; }
            return true;
        }
    }

    public void ResetTips()
    {
        lock (_gate) { var old = Current.SeenTips; Current.SeenTips = new(StringComparer.Ordinal); try { SaveSettings(); } catch { Current.SeenTips = old; throw; } }
        PreferencesChanged?.Invoke();
    }

    public void SavePreferences(bool tips, string defaultAgent, int memoryMb, int cores, bool resetTips = false, bool preserveVmDefaults = false,
        NotificationPreference? notifications = null, bool? rememberGrokSignIn = null)
    {
        if (defaultAgent is not (AgentChoice.Grok or AgentChoice.Codex or AgentChoice.Claude) || !preserveVmDefaults && (memoryMb < 2048 || cores < 1))
            throw new ArgumentException("Choose a bundled default agent and valid VM memory/CPU defaults.");
        if (notifications?.Enabled == true && !Guid.TryParseExact(notifications.DestinationReference, "N", out _))
            throw new ArgumentException("Configure a notification channel before enabling alerts.");
        lock (_gate)
        {
            var old = (Current.ShowTips, Current.DefaultAgent, Current.MachineMemoryMb, Current.MachineCores);
            var oldTips = Current.SeenTips;
            var oldNotifications = (Current.NotificationsEnabled, Current.NotificationDestination, Current.NotificationEpoch);
            var oldSignIn = Current.RememberGrokSignIn;
            if (rememberGrokSignIn is { } remember) Current.RememberGrokSignIn = remember;
            (Current.ShowTips, Current.DefaultAgent, Current.MachineMemoryMb, Current.MachineCores) =
                (tips, defaultAgent, preserveVmDefaults ? old.MachineMemoryMb : memoryMb, preserveVmDefaults ? old.MachineCores : cores);
            if (resetTips) Current.SeenTips = new(StringComparer.Ordinal);
            if (notifications is not null)
            {
                if (Current.NotificationsEnabled != notifications.Enabled || Current.NotificationDestination != notifications.DestinationReference)
                    Current.NotificationEpoch = Guid.NewGuid().ToString("N");
                (Current.NotificationsEnabled, Current.NotificationDestination) = (notifications.Enabled, notifications.DestinationReference);
            }
            try { SaveSettings(); } catch { (Current.ShowTips, Current.DefaultAgent, Current.MachineMemoryMb, Current.MachineCores) = old; Current.SeenTips = oldTips;
                (Current.NotificationsEnabled, Current.NotificationDestination, Current.NotificationEpoch) = oldNotifications; Current.RememberGrokSignIn = oldSignIn; throw; }
        }
        PreferencesChanged?.Invoke();
    }

    public NotificationConsent NotificationConsentFor(string path)
    {
        lock (_gate)
        {
            var project = KnownProjects.FirstOrDefault(project => MatchesProject(project.Path, Path.GetFullPath(path)));
            return new(Current.NotificationsEnabled, project?.NotificationsEnabled == true,
                Guid.TryParseExact(Current.NotificationDestination, "N", out _), Current.NotificationDestination,
                Current.NotificationEpoch + ":" + project?.NotificationEpoch);
        }
    }

    public void SaveProjectNotifications(string path, bool enabled)
    {
        lock (_gate)
        {
            var full = Path.GetFullPath(path);
            var project = KnownProjects.FirstOrDefault(item => MatchesProject(item.Path, full));
            var added = project is null;
            project ??= new KnownProject { Name = FolderName(full), Path = full };
            var old = (project.NotificationsEnabled, project.NotificationEpoch);
            if (project.NotificationsEnabled != enabled) project.NotificationEpoch = Guid.NewGuid().ToString("N");
            project.NotificationsEnabled = enabled;
            if (added) KnownProjects.Add(project);
            try { SaveKnownProjects(); }
            catch { (project.NotificationsEnabled, project.NotificationEpoch) = old; if (added) KnownProjects.Remove(project); throw; }
        }
        PreferencesChanged?.Invoke();
    }

    public void SaveSettings()
    {
        lock (_gate)
            _settingsHash = JsonFile.Save(_paths.SettingsFile, Current, _settingsHash);
    }

    public void SaveKnownProjects()
    {
        lock (_gate)
            _projectsHash = JsonFile.Save(_paths.ProjectsFile, KnownProjects, _projectsHash);
    }

    public void RememberProject(string name, string path)
    {
        lock (_gate)
        {
            var full = Path.GetFullPath(path);
            var existing = KnownProjects.FirstOrDefault(p =>
                string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase));
            var oldName = existing?.Name;
            var oldPath = existing?.Path;
            var added = existing is null;
            if (added)
            {
                existing = new KnownProject { Name = name, Path = full };
                KnownProjects.Add(existing);
            }
            else
            {
                existing!.Name = name;
                existing.Path = full;
            }
            try { SaveKnownProjects(); }
            catch { if (added) KnownProjects.Remove(existing!); else { existing!.Name = oldName!; existing.Path = oldPath!; } throw; }
        }
    }

    public void MarkOpened(string path)
    {
        lock (_gate) SaveEntry(Current.LastOpened, Path.GetFullPath(path), DateTimeOffset.Now);
    }

    public int ProjectMemoryMbFor(string path)
    {
        lock (_gate) return Current.ProjectMemoryMb.TryGetValue(Path.GetFullPath(path), out var megabytes) ? megabytes : 0;
    }

    public void SaveProjectMemory(string path, int megabytes)
    {
        lock (_gate) SaveEntry(Current.ProjectMemoryMb, Path.GetFullPath(path), megabytes);
    }

    public AgentLaunch AgentFor(string path)
    {
        lock (_gate)
        {
            var full = Path.GetFullPath(path);
            Current.ProjectAgent.TryGetValue(full, out var id);
            Current.ProjectAgentProgram.TryGetValue(full, out var program);
            Current.ProjectAgentSource.TryGetValue(full, out var source);
            var normalized = AgentChoice.Normalize(id ?? Current.DefaultAgent);
            if (!AgentChoice.IsEnabled(normalized))
                return AgentLaunch.Grok;
            return new AgentLaunch(normalized, program, source);
        }
    }

    public void SaveAgent(string path, string id, string? program, string? sourceFile, string? launchMode = null)
    {
        lock (_gate)
        {
            if (launchMode is not (null or "native" or "fenced")) throw new ArgumentException("Choose Fenced VM or Native.");
            var old = (CaseMap(Current.ProjectAgent), CaseMap(Current.ProjectAgentProgram), CaseMap(Current.ProjectAgentSource), CaseMap(Current.ProjectLaunchMode));
            var full = Path.GetFullPath(path);
            var chosen = AgentChoice.Normalize(id);
            if (launchMode is not null) Current.ProjectLaunchMode[full] = launchMode;
            Current.ProjectAgent[full] = chosen;
            if (chosen == AgentChoice.Custom && (AgentChoice.SafeProgram(program)
                || LaunchModeFor(path) == "native" && !string.IsNullOrWhiteSpace(sourceFile)
                && Path.IsPathFullyQualified(sourceFile) && NativeAgentLocator.Supported(sourceFile)))
            {
                Current.ProjectAgentProgram[full] = program!;
                if (!string.IsNullOrWhiteSpace(sourceFile))
                    Current.ProjectAgentSource[full] = sourceFile;
            }
            else
            {
                Current.ProjectAgentProgram.Remove(full);
                Current.ProjectAgentSource.Remove(full);
            }
            try { SaveSettings(); }
            catch { (Current.ProjectAgent, Current.ProjectAgentProgram, Current.ProjectAgentSource, Current.ProjectLaunchMode) = old; throw; }
        }
    }

    public string? BackupRemote(string path)
    {
        lock (_gate) return Current.BackupRemotes.TryGetValue(Path.GetFullPath(path), out var remote) ? remote : null;
    }

    public void SaveBackupRemote(string path, string remote)
    {
        lock (_gate) SaveEntry(Current.BackupRemotes, Path.GetFullPath(path), remote);
    }

    public DateTimeOffset? GetLastOpened(string path)
    {
        lock (_gate) return Current.LastOpened.TryGetValue(Path.GetFullPath(path), out var when) ? when : null;
    }

    public void UpdateProjectPath(string oldPath, string newName, string newPath)
    {
        var oldFull = Path.GetFullPath(oldPath);
        var newFull = Path.GetFullPath(newPath);

        var existing = KnownProjects.FirstOrDefault(p =>
            string.Equals(p.Path, oldFull, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            KnownProjects.Add(new KnownProject { Name = newName, Path = newFull });
        else
        {
            existing.Name = newName;
            existing.Path = newFull;
        }

        if (Current.LastOpened.TryGetValue(oldFull, out var when))
        {
            Current.LastOpened.Remove(oldFull);
            Current.LastOpened[newFull] = when;
        }

        if (Current.BackupRemotes.TryGetValue(oldFull, out var remote))
        {
            Current.BackupRemotes.Remove(oldFull);
            Current.BackupRemotes[newFull] = remote;
        }

        if (Current.ProjectMemoryMb.TryGetValue(oldFull, out var memoryMb))
        {
            Current.ProjectMemoryMb.Remove(oldFull);
            Current.ProjectMemoryMb[newFull] = memoryMb;
        }

        Move(Current.ProjectAgent, oldFull, newFull);
        Move(Current.ProjectLaunchMode, oldFull, newFull);
        Move(Current.ProjectWindowsTestPermission, oldFull, newFull);
        if (Current.ProjectPermissions.Remove(oldFull, out var policy)) Current.ProjectPermissions[newFull] = policy;
        Move(Current.ProjectAgentProgram, oldFull, newFull);
        Move(Current.ProjectAgentSource, oldFull, newFull);

        SaveSettings();

        SaveKnownProjects();
    }

    private static Dictionary<string, string> CaseMap(Dictionary<string, string>? source) =>
        new(source ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

    private void SaveEntry<T>(Dictionary<string, T> map, string key, T value)
    {
        var existed = map.TryGetValue(key, out var previous);
        map[key] = value;
        try { SaveSettings(); }
        catch { if (existed) map[key] = previous!; else map.Remove(key); throw; }
    }

    public string LaunchModeFor(string path)
    {
        lock (_gate) return Current.ProjectLaunchMode.TryGetValue(Path.GetFullPath(path), out var mode) && mode == "native" ? "native" : "fenced";
    }

    public void SaveLaunchMode(string path, string mode)
    {
        if (mode is not ("native" or "fenced")) throw new ArgumentException("Choose Fenced VM or Native.");
        lock (_gate) SaveEntry(Current.ProjectLaunchMode, Path.GetFullPath(path), mode);
    }

    public string WindowsTestPermissionFor(string path)
    {
        lock (_gate)
        {
            if (!Current.ProjectWindowsTestPermission.TryGetValue(Path.GetFullPath(path), out var value)) return "automatic";
            if (value is not ("automatic" or "confirm")) throw new IOException("This project's Windows test permission is invalid. No test was approved.");
            return value;
        }
    }

    public ProjectPermissionPolicy PermissionPolicyFor(string path)
    {
        lock (_gate)
            return PermissionPolicies.Validate(Current.ProjectPermissions.TryGetValue(Path.GetFullPath(path), out var policy)
                ? policy : ProjectPermissionPolicy.Standard);
    }

    public void SavePermissionPolicy(string path, ProjectPermissionPolicy policy)
    {
        var validated = PermissionPolicies.Validate(policy);
        lock (_gate) SaveEntry(Current.ProjectPermissions, Path.GetFullPath(path), validated);
        PreferencesChanged?.Invoke();
    }

    public void SaveWindowsTestPermission(string path, string permission)
    {
        if (permission is not ("automatic" or "confirm")) throw new ArgumentException("Choose automatic Windows testing or confirmation per test.");
        lock (_gate) SaveEntry(Current.ProjectWindowsTestPermission, Path.GetFullPath(path), permission);
        PreferencesChanged?.Invoke();
    }

    private static bool MatchesProject(string? path, string full)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.GetFullPath(path).Equals(full, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static void Move(Dictionary<string, string> map, string oldFull, string newFull)
    {
        if (!map.TryGetValue(oldFull, out var value))
            return;
        map.Remove(oldFull);
        map[newFull] = value;
    }
}

