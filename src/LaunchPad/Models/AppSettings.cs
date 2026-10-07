namespace LaunchPad.Models;

public sealed class AppSettings
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalFields { get; set; }
    public bool HideWelcome { get; set; }
    public bool OnboardingCompleted { get; set; }
    public bool ShortcutPromptShown { get; set; }
    public bool HypervisorResumePending { get; set; }
    public int MachineMemoryMb { get; set; }
    public int MachineCores { get; set; }
    public bool ShowTips { get; set; } = true;
    public bool NotificationsEnabled { get; set; }
    public string? NotificationDestination { get; set; }
    public string? NotificationEpoch { get; set; }
    public HashSet<string> SeenTips { get; set; } = new(StringComparer.Ordinal);
    public string DefaultAgent { get; set; } = "grok";
    public string? NewProjectsRoot { get; set; }
    public bool RememberGrokSignIn { get; set; } = true;
    public Dictionary<string, int> ProjectMemoryMb { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectAgent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectLaunchMode { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectWindowsTestPermission { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProjectPermissionPolicy> ProjectPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectAgentProgram { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProjectAgentSource { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> LastOpened { get; set; } = new();
    public Dictionary<string, string> BackupRemotes { get; set; } = new();
}
