using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record ActiveSession(int Schema, string Directory, string RuntimeVersion);

/// <summary>Selects a verified writable child without replacing any saved disk.</summary>
public static class ProjectSessionStore
{
    private const string SelectionFile = "active-session.json";
    private const string LeaseFile = "launch.lock";
    public const string UnconfirmedImport = "preserved-import.failed";

    public static string Current(string home)
    {
        home = Path.GetFullPath(home);
        if (!FenceFiles.TryResolveUnlinked(home, SelectionFile, out var path))
            throw new InvalidDataException("The saved VM selection contains a link.");
        if (!File.Exists(path)) return home;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The saved VM selection is invalid.");
        var selection = JsonSerializer.Deserialize<ActiveSession>(File.ReadAllText(path), JsonFile.Options);
        if (selection is null || selection.Schema != 1 || string.IsNullOrWhiteSpace(selection.RuntimeVersion)
            || !SafeChild(selection.Directory) || !FenceFiles.TryResolveUnlinked(home, selection.Directory + "/session.qcow2", out var disk)
            || !File.Exists(disk)) throw new InvalidDataException("The selected saved VM could not be verified. All disks were preserved.");
        return Path.GetDirectoryName(disk)!;
    }

    public static FileStream Acquire(string home)
    {
        if (!FenceFiles.TryResolveUnlinked(home, LeaseFile, out var lease)) throw new InvalidDataException("The session location contains a link.");
        Directory.CreateDirectory(home);
        try { return new(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("This project's VM is already being prepared. Wait for that launch to finish.", error); }
    }

    public static bool IsPreparing(string home)
    {
        if (!FenceFiles.TryResolveUnlinked(home, LeaseFile, out var path)) return true;
        if (!File.Exists(path)) return false;
        try { using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    // The caller holds Acquire(home) across maintenance, selection and handoff.
    public static void Activate(string home, UpgradedSession upgraded)
    {
        home = Path.GetFullPath(home);
        var current = Current(home);
        var relative = Path.GetRelativePath(home, upgraded.Directory).Replace('\\', '/');
        if (!SafeChild(relative) || !FenceFiles.TryResolveUnlinked(home, relative + "/session.qcow2", out var disk)
            || !disk.Equals(Path.GetFullPath(upgraded.Disk), StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(upgraded.Directory)!.Equals(current, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(upgraded.Original).Equals(Path.Combine(current, "session.qcow2"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The upgrade does not belong to the current saved VM.");
        using var original = new FileStream(upgraded.Original, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(original)).Equals(upgraded.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The original VM changed after verification. The child was retained without activation.");
        if (!FenceFiles.TryResolveUnlinked(upgraded.Directory, "upgrade-result.json", out var resultPath))
            throw new InvalidDataException("The upgrade receipt contains a link.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(resultPath));
        var record = receipt.RootElement;
        if (!record.GetProperty("verified").GetBoolean()
            || !string.Equals(record.GetProperty("original").GetString(), upgraded.Original, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.GetProperty("originalSha256").GetString(), upgraded.OriginalSha256, StringComparison.OrdinalIgnoreCase)
            || record.GetProperty("version").GetString() != upgraded.RuntimeVersion
            || !File.Exists(disk) || !File.Exists(Path.Combine(upgraded.Directory, "sent.manifest")))
            throw new InvalidDataException("The upgrade did not complete its verification. Its files were preserved.");
        ReturnRecovery.SaveAtomic(Path.Combine(home, SelectionFile), new ActiveSession(1, relative, upgraded.RuntimeVersion));
    }

    public static IReadOnlyList<string> History(string home)
    {
        home = Path.GetFullPath(home);
        var history = new List<string>();
        for (var current = Current(home); ; current = Path.GetDirectoryName(current)!)
        {
            history.Add(current);
            if (current.Equals(home, StringComparison.OrdinalIgnoreCase)) return history;
        }
    }

    public static bool HasUnconfirmedImport(string directory, bool includePreserved = true) =>
        File.Exists(Path.Combine(directory, "resend.failed"))
        || File.Exists(Path.Combine(directory, InitialImport.JournalName))
        || (includePreserved && File.Exists(Path.Combine(directory, UnconfirmedImport)));

    public static string? RuntimeVersion(string directory)
    {
        var upgrade = Path.Combine(directory, "upgrade-result.json");
        var path = File.Exists(upgrade) ? upgrade : Path.Combine(directory, "session-runtime.json");
        if (!File.Exists(path)) return null;
        if (!FenceFiles.TryResolveUnlinked(directory, Path.GetFileName(path), out _))
            throw new InvalidDataException("The saved VM runtime record contains a link.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (path == upgrade && !document.RootElement.GetProperty("verified").GetBoolean())
            throw new InvalidDataException("The selected VM upgrade did not complete.");
        return document.RootElement.GetProperty("version").GetString();
    }

    private static bool SafeChild(string? relative) => relative is not null && relative.Length < 640
        && relative.Split('/').Length <= 16 && relative.Split('/').All(part =>
            part.StartsWith("upgrade-", StringComparison.Ordinal) && Guid.TryParseExact(part[8..], "N", out _));
}
