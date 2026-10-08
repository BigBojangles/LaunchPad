using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record DirectBootManifest(string KernelRelease, RuntimeImageFile Kernel, RuntimeImageFile Initrd);
public sealed record VerifiedDirectBoot(string Kernel, string Initrd, DirectBootManifest Manifest);

/// <summary>Normal boot assets are an explicit runtime capability, never inferred from maintenance filenames.</summary>
public static class RuntimeBoot
{
    public static void Validate(DirectBootManifest? boot)
    {
        if (boot is null) return;
        if (string.IsNullOrWhiteSpace(boot.KernelRelease) || boot.KernelRelease.Length > 128
            || boot.KernelRelease.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException("The direct-boot kernel release is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { boot.Kernel, boot.Initrd })
            if (file is null || string.IsNullOrWhiteSpace(file.File) || file.File.Length > 128
                || file.File.Contains('/') || file.File.Contains('\\') || !FenceFiles.IsSafe(file.File)
                || file.File.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_')
                || !names.Add(file.File) || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("A direct-boot asset declaration is invalid.");
    }

    public static VerifiedDirectBoot? Read(string root, RuntimeImageManifest? runtime)
    {
        var boot = runtime?.DirectBoot;
        if (boot is null) return null;
        Validate(boot);
        var paths = new List<string>();
        var images = Path.Combine(Path.GetFullPath(root), "images");
        foreach (var file in new[] { boot.Kernel, boot.Initrd })
        {
            if (!FenceFiles.TryResolveUnlinked(images, file.File, out var path) || !File.Exists(path))
                throw new InvalidDataException("A declared direct-boot asset is missing or linked. The VM was not started.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A direct-boot asset failed its content check. The VM was not started.");
            paths.Add(path);
        }
        return new(paths[0], paths[1], boot);
    }

    public static VerifiedDirectBoot? ForSession(VerifiedDirectBoot? boot, string? runtimeVersion,
        string sessionDirectory, bool existingDisk)
    {
        if (boot is null || runtimeVersion is null) return null;
        if (!existingDisk) return boot;
        // Older sessions, including maintenance children without a matching normal-boot
        // receipt, retain GRUB. Never infer the guest kernel from the current template.
        if (ProjectSessionStore.RuntimeVersion(sessionDirectory) != runtimeVersion) return null;
        if (!FenceFiles.TryResolveUnlinked(sessionDirectory, "session-runtime.json", out var path))
            throw new InvalidDataException("The session boot record is linked.");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The session boot record is too large.");
        using var record = JsonDocument.Parse(File.ReadAllText(path));
        if (record.RootElement.GetProperty("version").GetString() != runtimeVersion
            || !record.RootElement.TryGetProperty("directBoot", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        var saved = value.Deserialize<DirectBootManifest>(JsonFile.Options);
        Validate(saved);
        return saved == boot.Manifest ? boot : null;
    }
}