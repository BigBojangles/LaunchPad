using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record MaintenanceArtifact(string File, string Sha256);
public sealed record MaintenanceManifest(int Schema, string Version, MaintenanceArtifact Kernel,
    MaintenanceArtifact Initrd, MaintenanceArtifact Payload, string GuestScriptSha256);
public sealed record MaintenanceKit(string Kernel, string Initrd, string Payload, MaintenanceManifest Manifest)
{
    public static MaintenanceKit Read(string runtimeRoot, string manifestName = "maintenance.json")
    {
        var images = Path.Combine(runtimeRoot, "images");
        if (!RuntimeImages.ValidManifestName(manifestName) || !FenceFiles.TryResolveUnlinked(images, manifestName, out var path)) throw new InvalidDataException("The maintenance location contains a link.");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The maintenance manifest is too large.");
        var bytes = File.ReadAllBytes(path);
        if (manifestName.StartsWith(RuntimeActivation.MaintenancePrefix, StringComparison.Ordinal)
            && (manifestName.Length != RuntimeActivation.MaintenancePrefix.Length + 64 + 5
                || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifestName.Substring(RuntimeActivation.MaintenancePrefix.Length, 64), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected maintenance snapshot failed its address check.");
        var manifest = JsonSerializer.Deserialize<MaintenanceManifest>(bytes, JsonFile.Options);
        if (manifest is null || manifest.Schema != 1 || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.GuestScriptSha256 is not { Length: 64 } || !manifest.GuestScriptSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The offline maintenance manifest is invalid.");
        var resolved = new List<string>();
        foreach (var artifact in new[] { manifest.Kernel, manifest.Initrd, manifest.Payload })
        {
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.File) || artifact.File.Contains('/') || !FenceFiles.IsSafe(artifact.File)
                || artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(Uri.IsHexDigit)
                || !FenceFiles.TryResolveUnlinked(images, artifact.File, out var file)) throw new InvalidDataException("An offline maintenance artifact path is invalid.");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The offline maintenance kit failed its content check.");
            resolved.Add(file);
        }
        return new(resolved[0], resolved[1], resolved[2], manifest);
    }
}
