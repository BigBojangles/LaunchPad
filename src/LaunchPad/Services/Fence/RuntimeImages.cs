using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record RuntimeImageFile(string File, string Sha256);
public sealed record RuntimeImageManifest(int Schema, string Version, RuntimeImageFile Image,
    IReadOnlyList<RuntimeImageFile> Dependencies, IReadOnlyList<string> Capabilities);
public sealed record RuntimeImageSelection(string ImagePath, RuntimeImageManifest? Manifest);

public static class RuntimeImages
{
    public const string ManifestName = "runtime.json";

    public static RuntimeImageSelection Read(string root)
    {
        var images = Path.Combine(Path.GetFullPath(root), "images");
        if (!FenceFiles.TryResolveUnlinked(images, ManifestName, out var manifestPath))
            throw new InvalidDataException("The VM image location contains a link.");
        if (!File.Exists(manifestPath)) return new(Path.Combine(images, "debian-12-builder.qcow2"), null);
        try
        {
            if (new FileInfo(manifestPath).Length > 65536) throw new InvalidDataException("The VM runtime manifest is too large.");
            var manifest = JsonSerializer.Deserialize<RuntimeImageManifest>(File.ReadAllText(manifestPath), JsonFile.Options);
            if (manifest is null || manifest.Schema != 1 || string.IsNullOrWhiteSpace(manifest.Version)
                || manifest.Image is null || manifest.Dependencies is null || manifest.Capabilities is null
                || manifest.Dependencies.Count > 16 || manifest.Capabilities.Count > 32)
                throw new InvalidDataException("The VM runtime manifest is invalid.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Dependencies.Prepend(manifest.Image))
            {
                if (item is null || !ValidFile(item.File) || !ValidHash(item.Sha256) || !seen.Add(item.File)
                    || !FenceFiles.TryResolveUnlinked(images, item.File, out var path) || !File.Exists(path))
                    throw new InvalidDataException("A required VM image is missing or its manifest path is unsafe.");
            }
            if (!IsBuilder(manifest.Image.File)) throw new InvalidDataException("The active runtime does not select a builder image.");
            return new(Path.Combine(images, manifest.Image.File), manifest);
        }
        catch (JsonException error) { throw new InvalidDataException("The VM runtime manifest could not be read. Existing disks were preserved.", error); }
    }

    public static void Verify(string root, RuntimeImageSelection selected)
    {
        if (selected.Manifest is null) return; // Legacy discovery is not a provenance pass.
        var images = Path.Combine(Path.GetFullPath(root), "images");
        foreach (var item in selected.Manifest.Dependencies.Prepend(selected.Manifest.Image))
        {
            if (!FenceFiles.TryResolveUnlinked(images, item.File, out var path)) throw new InvalidDataException("A runtime image path changed to a link.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A VM runtime image failed its content check. No session disk was changed.");
        }
    }

    public static bool IsBuilder(string path) => Path.GetFileName(path).StartsWith("debian-12-builder", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase);
    private static bool ValidFile(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('/')
        && FenceFiles.IsSafe(name) && name.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
