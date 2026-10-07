using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace LaunchPad.Services.Fence;

// Only validated runtime inputs and managed VM directories belong here. Never
// call this on the Windows project folder, an entire profile or a report parent.
public static class RestrictedRuntimeAccess
{
    // Validate the entire saved-disk dependency set before changing any ACL.
    // Old sessions can depend on old templates, not just the selected template.
    public static IReadOnlyList<string> BackingChain(string original, string projectDirectory,
        string imageDirectory, IReadOnlyCollection<string>? trustedBackingFiles = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var images = Path.TrimEndingDirectorySeparator(Path.GetFullPath(imageDirectory));
        var trusted = new HashSet<string>((trustedBackingFiles ?? []).Select(Path.GetFullPath), comparer);
        var seen = new HashSet<string>(comparer);
        var paths = new List<string>();
        var current = Path.GetFullPath(original);
        Span<byte> header = stackalloc byte[104];
        while (true)
        {
            if (paths.Count >= 64 || !seen.Add(current)) throw new InvalidDataException("Saved VM backing chain is cyclic or too deep. All disks were preserved.");
            if ((!current.StartsWith(project + Path.DirectorySeparatorChar, comparison)
                    && !current.StartsWith(images + Path.DirectorySeparatorChar, comparison) && !trusted.Contains(current))
                || !current.EndsWith(".qcow2", comparison)
                || !FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(current)!, Path.GetFileName(current), out _))
                throw new InvalidDataException("Saved VM backing file is outside its trusted project/runtime locations or contains a link. All disks were preserved.");
            using var file = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
            file.ReadExactly(header[..72]);
            var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
            uint headerLength = 72;
            if (BinaryPrimitives.ReadUInt32BigEndian(header[..4]) != 0x514649fb || version is not (2 or 3))
                throw new InvalidDataException("Saved VM backing file is not a supported qcow2 image.");
            if (version == 3)
            {
                file.ReadExactly(header[72..]);
                headerLength = BinaryPrimitives.ReadUInt32BigEndian(header[100..104]);
                if (headerLength < 104 || headerLength % 8 != 0 || headerLength > file.Length)
                    throw new InvalidDataException("Saved VM qcow2 header length is invalid.");
                // External data files would introduce another unvalidated path.
                if ((BinaryPrimitives.ReadUInt64BigEndian(header[72..80]) & 4) != 0)
                    throw new InvalidDataException("External qcow2 data files are not supported for preserved VM upgrades.");
            }
            paths.Add(current);
            var offset = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            var count = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            if (offset == 0) return paths; // Size is undefined when no backing file exists.
            if (count == 0 || count > 1023 || offset < headerLength
                || offset > (ulong)file.Length || count > (ulong)file.Length - offset)
                throw new InvalidDataException("Saved VM backing metadata is malformed. All disks were preserved.");
            file.Position = (long)offset;
            var bytes = new byte[(int)count];
            file.ReadExactly(bytes);
            var backing = new UTF8Encoding(false, true).GetString(bytes);
            if (backing.Contains('\0')) throw new InvalidDataException("Saved VM backing path contains a null character.");
            current = Path.GetFullPath(backing.Replace('/', Path.DirectorySeparatorChar), Path.GetDirectoryName(current)!);
        }
    }

    public static void ReadFile(string path)
    {
        if (OperatingSystem.IsWindows()) GrantWindows(path, FileSystemRights.ReadAndExecute, false);
    }

    public static bool HasRequiredReadGrants(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (!FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(full)!, Path.GetFileName(full), out _)) return false;
            var account = (SecurityIdentifier)new NTAccount(TestUserRunner.UserName).Translate(typeof(SecurityIdentifier));
            var acl = Directory.Exists(full) ? (FileSystemSecurity)new DirectoryInfo(full).GetAccessControl() : new FileInfo(full).GetAccessControl();
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
            return new[] { account, new SecurityIdentifier("S-1-5-12") }.All(sid =>
                rules.Any(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow
                    && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                    && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                && !rules.Any(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Deny
                    && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                    && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) != 0));
        }
        catch { return false; }
    }

    public static void ModifyDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) GrantWindows(path, FileSystemRights.Modify, true);
    }

    public static void ReadRuntimeTree(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.TryPop(out var directory))
        {
            GrantWindows(directory, FileSystemRights.ReadAndExecute, false);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                // The installer may have a fence/share junction into qemu/share.
                // Read its physical target through the ordinary tree, never
                // traverse or grant an arbitrary linked target.
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else GrantWindows(entry.FullName, FileSystemRights.ReadAndExecute, false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantWindows(string path, FileSystemRights rights, bool inherit)
    {
        var full = Path.GetFullPath(path);
        if (!FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(full)!, Path.GetFileName(full), out _))
            throw new InvalidDataException("Restricted runtime access refuses linked paths.");
        var directory = Directory.Exists(full);
        FileSystemInfo target = directory ? new DirectoryInfo(full) : new FileInfo(full);
        if (!target.Exists) throw new FileNotFoundException("Restricted runtime input is missing.", full);
        var acl = directory ? (FileSystemSecurity)((DirectoryInfo)target).GetAccessControl()
            : ((FileInfo)target).GetAccessControl();
        var account = (SecurityIdentifier)new NTAccount(TestUserRunner.UserName).Translate(typeof(SecurityIdentifier));
        var inheritance = inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var changed = false;
        foreach (var sid in new[] { account, new SecurityIdentifier("S-1-5-12") })
        {
            var present = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow
                    && (rule.FileSystemRights & rights) == rights && (rule.InheritanceFlags & inheritance) == inheritance
                    && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0);
            if (present) continue;
            acl.AddAccessRule(new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
            changed = true;
        }
        if (!changed) return;
        if (target is DirectoryInfo folder) folder.SetAccessControl((DirectorySecurity)acl);
        else ((FileInfo)target).SetAccessControl((FileSecurity)acl);
    }
}
