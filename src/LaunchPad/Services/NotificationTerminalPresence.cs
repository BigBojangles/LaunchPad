using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

// Pager lifetime follows our recorded console owners, including minimized
// terminals. The main GUI and unrelated PowerShell windows do not count.
internal static class NotificationTerminalPresence
{
    internal static bool HasOpenTerminal(AppPaths paths, string? sessionsRoot = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var directory in ProjectDirectories(Path.Combine(paths.AppDataDir, "windows", "native")))
        {
            var record = NativeAgentTerminal.Read(directory);
            if (record is not null && QemuLayout.ProjectKey(record.Project) == Path.GetFileName(directory)
                && (record.AppDataDirectory is null || Path.GetFullPath(record.AppDataDirectory).Equals(
                    Path.GetFullPath(paths.AppDataDir), StringComparison.OrdinalIgnoreCase))
                && NativeAgentTerminal.IsLive(record)) return true;
        }
        if (File.Exists(Path.Combine(paths.ExeDirectory, "native-only.txt"))) return false;
        foreach (var home in ProjectDirectories(sessionsRoot ?? Path.Combine(QemuLayout.Root, "sessions")))
        {
            try
            {
                var directory = ProjectSessionStore.Current(home);
                var owner = SessionGuardian.TryReadLiveOwner(directory);
                if (owner is not null && owner.ProjectPath is { } project
                    && QemuLayout.ProjectKey(project) == Path.GetFileName(home)
                    && HasLiveFencedTerminal(owner, WindowsSessionWindow.MatchesProcess)) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return false;
    }

    internal static bool HasLiveFencedTerminal(SessionOwnerIdentity? owner, Func<int, long, bool> isLive)
        => owner is { TerminalPid: > 0, TerminalStartTicks: > 0 }
            && isLive(owner.TerminalPid.Value, owner.TerminalStartTicks.Value);

    private static IEnumerable<string> ProjectDirectories(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Where(directory =>
        {
            var name = Path.GetFileName(directory);
            return name.Length == 16 && name.All(Uri.IsHexDigit)
                && FenceFiles.TryResolveUnlinked(root, name, out _);
        }).ToArray();
    }
}
