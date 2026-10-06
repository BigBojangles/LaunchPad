using System.Security.Cryptography;
using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record MaintenanceArtifact(string File, string Sha256);
public sealed record MaintenanceManifest(int Schema, string Version, MaintenanceArtifact Kernel,
    MaintenanceArtifact Initrd, MaintenanceArtifact Payload, string GuestScriptSha256);
public sealed record MaintenanceKit(string Kernel, string Initrd, string Payload, MaintenanceManifest Manifest)
{
    public static MaintenanceKit Read(string runtimeRoot)
    {
        var images = Path.Combine(runtimeRoot, "images");
        if (!FenceFiles.TryResolveUnlinked(images, "maintenance.json", out var path)) throw new InvalidDataException("The maintenance location contains a link.");
        var manifest = JsonSerializer.Deserialize<MaintenanceManifest>(File.ReadAllText(path), JsonFile.Options);
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
