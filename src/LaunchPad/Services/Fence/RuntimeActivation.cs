using System.Security.Cryptography;

namespace LaunchPad.Services.Fence;

public sealed record RuntimeActivationResult(string Version, bool Changed, string? PreviousManifest);

/// <summary>Validates installed immutable inputs before publishing the single runtime selection pointer.</summary>
public static class RuntimeActivation
{
    public const string Argument = "--activate-runtime";
    public const string RuntimePrefix = "runtime-package-";
    public const string MaintenancePrefix = "maintenance-package-";
    public static bool IsRequest(string[] args) => args.Length > 0 && args[0] == Argument;

    public static RuntimeActivationResult Activate(string directory, string candidateName,
        Action<RuntimeAccessPlan>? prepareAccess = null)
    {
        var root = Path.GetFullPath(directory);
        Action<RuntimeAccessPlan> prepare = prepareAccess ?? (value => WindowsRuntimeRepair.Apply(value));
        var images = Path.Combine(root, "images");
        string Safe(string name)
        {
            if (!FenceFiles.TryResolveUnlinked(images, name, out var path)) throw new InvalidDataException("Runtime activation path is linked or unsafe.");
            return path;
        }
        using var owner = new FileStream(Safe("runtime-activation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var leases = new List<FileStream>();
        string? temporary = null;
        try
        {
            FileStream Hold(string name)
            {
                var input = new FileStream(Safe(name), FileMode.Open, FileAccess.Read, FileShare.Read);
                leases.Add(input);
                return input;
            }
            FileStream Manifest(string name, string prefix)
            {
                if (!RuntimeImages.ValidManifestName(name) || !name.StartsWith(prefix, StringComparison.Ordinal)
                    || name.Length != prefix.Length + 64 + 5) throw new InvalidDataException("The package manifest name is not content addressed.");
                var input = Hold(name);
                if (input.Length > 65536 || !Convert.ToHexString(SHA256.HashData(input)).Equals(name.Substring(prefix.Length, 64), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("An installed package manifest failed its content check.");
                input.Position = 0;
                return input;
            }

            var candidate = Manifest(candidateName, RuntimePrefix);
            var selected = RuntimeImages.Read(root, candidateName);
            var manifest = selected.Manifest ?? throw new InvalidDataException("A package must select an explicit runtime.");
            var maintenanceName = manifest.MaintenanceManifest ?? throw new InvalidDataException("The package does not identify its maintenance kit.");
            Manifest(maintenanceName, MaintenancePrefix);
            foreach (var image in manifest.Dependencies.Prepend(manifest.Image)) Hold(image.File);
            if (manifest.DirectBoot is { } boot)
                foreach (var artifact in new[] { boot.Kernel, boot.Initrd }) Hold(artifact.File);
            RuntimeImages.Verify(root, selected);
            var declared = manifest.Dependencies.Prepend(manifest.Image).Select(image => Safe(image.File)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actual = RestrictedRuntimeAccess.BackingChain(selected.ImagePath, images, images);
            if (!declared.SetEquals(actual)) throw new InvalidDataException("The installed backing chain differs from the declared runtime dependencies.");
            if (manifest.Dependencies.Count > 0 && !manifest.Dependencies.Any(image => image.File.Equals(PublicRuntime.BaseName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The packaged runtime lacks its required Debian base.");
            var kit = MaintenanceKit.Read(root, maintenanceName);
            if (kit.Manifest.Version != manifest.Version) throw new InvalidDataException("The package maintenance version differs from its runtime.");
            foreach (var artifact in new[] { kit.Manifest.Kernel, kit.Manifest.Initrd, kit.Manifest.Payload }) Hold(artifact.File);
            // Reverify after holding every artifact, so replacement is excluded
            // throughout access preparation and selection publication.
            MaintenanceKit.Read(root, maintenanceName);
            var plan = WindowsRuntimeRepair.Plan(root, includeVm: true, runtimeManifestName: candidateName);

            var active = Safe(RuntimeImages.ManifestName);
            using var previous = File.Exists(active)
                ? new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
            if (previous is { Length: > 65536 }) throw new InvalidDataException("The prior runtime selection is too large; it was preserved.");
            var bytes = new byte[checked((int)candidate.Length)]; candidate.ReadExactly(bytes);
            if (previous is not null)
            {
                var oldBytes = new byte[checked((int)previous.Length)]; previous.ReadExactly(oldBytes);
                if (oldBytes.AsSpan().SequenceEqual(bytes))
                {
                    prepare(plan);
                    return new(manifest.Version, false, null);
                }
            }
            temporary = Safe("runtime-selection-" + Guid.NewGuid().ToString("N") + ".pending");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            // Stage/read permissions before switching. A denied account/runtime
            // or ACL operation cannot replace the previous selection.
            var prepared = plan with { ReadPaths = plan.ReadPaths.Append(temporary).ToArray() };
            prepare(prepared);
            string? backup = null;
            if (previous is null) File.Move(temporary, active);
            else
            {
                backup = Safe("runtime-before-" + Guid.NewGuid().ToString("N") + ".json");
                File.Replace(temporary, active, backup);
            }
            temporary = null;
            return new(manifest.Version, true, backup);
        }
        finally
        {
            foreach (var input in leases) input.Dispose();
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
