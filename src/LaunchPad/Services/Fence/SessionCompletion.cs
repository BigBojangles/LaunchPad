using System.Text.Json;

namespace LaunchPad.Services.Fence;

/// <summary>Completion belongs to one machine generation, not a console marker.</summary>
public static class SessionCompletion
{
    private sealed record Evidence(string Generation, int MachinePid, long MachineStartTicks, bool Complete, string? RecoveryDirectory);
    private const string ReturnFile = "session-return-completion.json";
    private const string ShutdownFile = "session-shutdown-completion.json";
    public static void Clear(string directory)
    {
        foreach (var file in new[] { ReturnFile, ShutdownFile }) File.Delete(Path.Combine(directory, file));
    }
    public static void RecordReturn(string directory, SessionOwnerIdentity owner, ProjectReturnReceipt receipt, string recoveryDirectory) =>
        Save(directory, ReturnFile, owner, receipt.Complete && receipt.HasContentIdentities, recoveryDirectory);
    public static void RecordShutdown(string directory, SessionOwnerIdentity owner, bool observed) =>
        Save(directory, ShutdownFile, owner, observed, null);
    public static bool Returned(string directory, SessionOwnerIdentity owner) => Read(directory, ReturnFile, owner)?.Complete == true;
    public static bool ShutdownObserved(string directory, SessionOwnerIdentity owner) => Read(directory, ShutdownFile, owner)?.Complete == true;
    public static bool WaitForShutdownEvidence(string directory, SessionOwnerIdentity owner, TimeSpan? timeout = null)
    {
        // QEMU exits before the process that requested ACPI can publish its
        // result. Give that writer a bounded chance; EOF alone proves nothing.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (Read(directory, ShutdownFile, owner) is { } evidence) return evidence.Complete;
            Thread.Sleep(20);
        } while (clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(2)));
        return false;
    }
    public static string? RecoveryDirectory(string directory, SessionOwnerIdentity owner) => Read(directory, ReturnFile, owner)?.RecoveryDirectory;
    private static void Save(string directory, string file, SessionOwnerIdentity owner, bool complete, string? recoveryDirectory)
    {
        if (!FenceFiles.TryResolveUnlinked(directory, file, out var path)) throw new IOException("The session completion location contains a link.");
        ReturnRecovery.SaveAtomic(path, new Evidence(owner.Generation, owner.MachinePid, owner.MachineStartTicks, complete, recoveryDirectory));
    }
    private static Evidence? Read(string directory, string file, SessionOwnerIdentity owner)
    {
        try
        {
            if (!FenceFiles.TryResolveUnlinked(directory, file, out var path) || !File.Exists(path) || new FileInfo(path).Length > 8192) return null;
            var evidence = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(path), JsonFile.Options);
            return evidence is not null && evidence.Generation == owner.Generation && evidence.MachinePid == owner.MachinePid
                && evidence.MachineStartTicks == owner.MachineStartTicks ? evidence : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
