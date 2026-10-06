namespace LaunchPad.Services.Fence;

public static class ProjectReturnHost
{
    public static Func<string, string?>? ReadSavedRemote { get; set; }
    public static Func<string, string?>? OfferBackup { get; set; }
    public static Action<string>? RemindBackup { get; set; }
    public static Action<string>? Tell { get; set; }
    public static Func<string, Task<string?>>? OfferBackupAsync { get; set; }
    public static Func<string, Task>? RemindBackupAsync { get; set; }
    public static Func<string, Task>? TellAsync { get; set; }
}
