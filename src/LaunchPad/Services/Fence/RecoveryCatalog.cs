using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record SavedReturn(string Directory, DateTime SavedUtc, int Files, bool CanRetry, string Message,
    bool CanReview = true, string? ReviewDirectory = null)
{
    public string ReviewPath => ReviewDirectory ?? Path.Combine(Directory, "payload");
    public override string ToString() => SavedUtc.ToLocalTime().ToString("g") + " — "
        + (Files < 0 ? "unverified copy" : Files + " fully received " + (Files == 1 ? "file" : "files"));
}

public sealed record ProjectRecovery(string SessionDirectory, bool RestartBlocked, string Message, IReadOnlyList<SavedReturn> Returns, bool CanResume = false);

public static class RecoveryCatalog
{
    public static ProjectRecovery Read(string project, string sessionDirectory)
    {
        try
        {
            var history = ProjectSessionStore.History(sessionDirectory);
            var current = ReadSingle(project, history[0]);
            var saved = history.SelectMany(directory => ReadSingle(project, directory).Returns).OrderByDescending(item => item.SavedUtc).ToArray();
            var disk = Path.Combine(history[0], "session.qcow2");
            var canResume = FenceFiles.TryResolveUnlinked(history[0], "session.qcow2", out _) && File.Exists(disk);
            return current with { Returns = saved, CanResume = canResume,
                Message = current.Message + (canResume ? " Open saved VM keeps its stored work and does not send Windows project files." : "") };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        { return new(sessionDirectory, true, "The saved VM selection could not be verified. All disks and recovery copies were preserved.", Array.Empty<SavedReturn>()); }
    }

    private static ProjectRecovery ReadSingle(string project, string sessionDirectory)
    {
        var saved = new List<SavedReturn>();
        var blocked = SessionGuardian.NeedsRecovery(sessionDirectory);
        var message = blocked ? "The VM stopped with unfinished recovery. Its disk and saved files are preserved. Review the copies below."
            : "Saved return copies are retained here. Retrying copy-back checks the receipt, file scanner and Windows conflicts.";
        if (!FenceFiles.TryResolveUnlinked(sessionDirectory, "return/check", out _))
            return new(sessionDirectory, true, "The recovery location contains a link and cannot be used safely.", saved);
        var root = Path.Combine(sessionDirectory, "return");
        if (Directory.Exists(root))
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var count = -1;
                var retry = false;
                var review = false;
                var note = "This copy has no verified receipt. Review its files before taking any action.";
                try
                {
                    var recovery = ReturnRecovery.Open(directory);
                    review = true;
                    var receipt = recovery.LoadTransfer(project);
                    count = receipt.Files.Count;
                    retry = receipt.Complete;
                    if (ProjectSessionStore.HasUnconfirmedImport(sessionDirectory)) retry = false;
                    note = receipt.Complete ? "Complete transfer. Copy-back will recheck every file." : "Incomplete transfer. Files may be truncated; copy-back is unavailable.";
                    if (ProjectSessionStore.HasUnconfirmedImport(sessionDirectory)) note += " An earlier import was not confirmed. These files are available for review; copy-back remains protected.";
                    var statePath = Path.Combine(directory, "receipt.json");
                    if (File.Exists(statePath))
                    {
                        using var status = JsonDocument.Parse(File.ReadAllText(statePath));
                        var state = status.RootElement.GetProperty("state").GetString();
                        if (state == "applied") { retry = false; note = "These returned files were already applied. The recovery copy is retained."; }
                        else if (status.RootElement.TryGetProperty("message", out var detail) && detail.GetString() is { } text)
                            note += " " + text;
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
                { retry = false; note = "This copy could not be verified. Its files were preserved for review."; }
                saved.Add(new(directory, Directory.GetLastWriteTimeUtc(directory), count, retry, note, review));
            }
        return new(sessionDirectory, blocked, message, saved.OrderByDescending(item => item.SavedUtc).ToArray());
    }

    public static ReturnRecovery OpenForProject(string project, string sessionDirectory, string directory)
    {
        var full = Path.GetFullPath(directory);
        var source = ProjectSessionStore.History(sessionDirectory).FirstOrDefault(candidate =>
            string.Equals(Path.GetDirectoryName(full), Path.Combine(candidate, "return"), StringComparison.OrdinalIgnoreCase));
        if (source is null)
            throw new InvalidDataException("The recovery copy does not belong to this project's session.");
        if (ProjectSessionStore.HasUnconfirmedImport(source)) throw new InvalidDataException("The earlier import was not confirmed. Review the saved files before reconciling them with Windows.");
        var recovery = ReturnRecovery.Open(full);
        _ = recovery.LoadTransfer(project);
        return recovery;
    }
}
