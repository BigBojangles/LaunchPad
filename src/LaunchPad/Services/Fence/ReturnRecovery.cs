using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed class ReturnRecovery
{
    private sealed record TransferRecord(int Schema, string Project, ProjectReturnReceipt Receipt);
    private ReturnRecovery(string directory) { DirectoryPath = directory; }
    public string DirectoryPath { get; }
    public string Payload => Path.Combine(DirectoryPath, "payload");
    public string Manifest => Path.Combine(DirectoryPath, "sent.manifest");

    public void SaveTransfer(string project, ProjectReturnReceipt receipt)
    {
        var path = Path.Combine(DirectoryPath, "transfer.json");
        if (File.Exists(path)) return; // Preserve the original receipt across retries.
        SaveAtomic(path, new TransferRecord(1, string.IsNullOrWhiteSpace(project) ? "" : Path.GetFullPath(project), receipt), overwrite: false);
    }

    public ProjectReturnReceipt LoadTransfer(string project)
    {
        var transfer = JsonSerializer.Deserialize<TransferRecord>(File.ReadAllText(Path.Combine(DirectoryPath, "transfer.json")), JsonFile.Options);
        if (transfer is not { Schema: 1, Receipt: not null } || string.IsNullOrWhiteSpace(transfer.Project)
            || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(transfer.Project)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)), StringComparison.OrdinalIgnoreCase)
            || !transfer.Receipt.HasContentIdentities)
            throw new InvalidDataException("This recovery copy has no verified content receipt for this project. It was preserved for review.");
        return transfer.Receipt;
    }

    public static ReturnRecovery Open(string directory)
    {
        if (!Directory.Exists(directory) || !FenceFiles.TryResolveUnlinked(directory, "payload/check", out _))
            throw new IOException("The recovery copy is unavailable or contains a link.");
        return new ReturnRecovery(directory);
    }

    public static ReturnRecovery Create(string sessionDirectory)
    {
        var relative = "return/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        if (!FenceFiles.TryResolveUnlinked(sessionDirectory, relative, out var directory))
            throw new IOException("The return recovery directory contains a link.");
        Directory.CreateDirectory(Path.Combine(directory, "payload"));
        var recovery = new ReturnRecovery(directory);
        recovery.Record("receiving", "The return has not been verified.");
        return recovery;
    }

    public void Record(string state, string message) => SaveAtomic(Path.Combine(DirectoryPath, "receipt.json"),
        new { state, message, recordedUtc = DateTime.UtcNow });

    internal static void SaveAtomic<T>(string path, T value, bool overwrite = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(file, value, JsonFile.Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
