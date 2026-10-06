namespace LaunchPad.Services;

public sealed class AppPaths
{
    public AppPaths(
        string? userProfile = null,
        string? grokHome = null,
        string? grokBin = null,
        string? appDataDir = null,
        string? exePath = null)
    {
        UserProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var grokHomeEnv = Environment.GetEnvironmentVariable("GROK_HOME");
        GrokHome = grokHome
            ?? (!string.IsNullOrWhiteSpace(grokHomeEnv) ? grokHomeEnv : Path.Combine(UserProfile, ".grok"));

        var grokBinEnv = Environment.GetEnvironmentVariable("GROK_BIN_DIR");
        GrokBin = grokBin
            ?? (!string.IsNullOrWhiteSpace(grokBinEnv) ? grokBinEnv : Path.Combine(GrokHome, "bin"));

        GrokExe = Path.Combine(GrokBin, "grok.exe");
        AgentExe = Path.Combine(GrokBin, "agent.exe");
        SessionsDir = Path.Combine(GrokHome, "sessions");
        ProjectsRoot = Path.Combine(UserProfile, "Grok", "Projects");

        AppDataDir = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LaunchPad");
        SettingsFile = Path.Combine(AppDataDir, "settings.json");
        ProjectsFile = Path.Combine(AppDataDir, "projects.json");
        SetupLogFile = Path.Combine(AppDataDir, "logs", "setup.log");

        DesktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        StartMenuPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");

        ExePath = exePath
            ?? Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "LaunchPad.exe");
        ExeDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;
    }

    public string UserProfile { get; }
    public string GrokHome { get; }
    public string GrokBin { get; }
    public string GrokExe { get; }
    public string AgentExe { get; }
    public string SessionsDir { get; }
    public string ProjectsRoot { get; }
    public string AppDataDir { get; }
    public string SettingsFile { get; }
    public string ProjectsFile { get; }
    public string SetupLogFile { get; }
    public string DesktopDirectory { get; }
    public string StartMenuPrograms { get; }
    public string ExePath { get; }
    public string ExeDirectory { get; }

    public string DesktopShortcutPath => Path.Combine(DesktopDirectory, "LaunchPad.lnk");
    public string StartMenuShortcutPath => Path.Combine(StartMenuPrograms, "LaunchPad.lnk");
}

