using System.Text.Json;

namespace LaunchPad.Services.Fence;

public sealed record SetupRepairCheck(string Name, bool Ready, string Detail);
public sealed record SetupRepairReport(IReadOnlyList<SetupRepairCheck> Checks, bool RestartRequired = false)
{
    public bool Ready => Checks.All(check => check.Ready) && !RestartRequired;
}
public interface IWindowsSetupRepair
{
    Task<SetupRepairReport> CheckAsync();
    Task<SetupRepairReport> RepairAsync();
}

public sealed record RuntimeAccessPlan(string Root, IReadOnlyList<string> ReadPaths, string? SessionsDirectory);

/// <summary>Repairs explicit packaged runtime access without copying images or visiting project disks.</summary>
public static class WindowsRuntimeRepair
{
    public const string Argument = "--repair-runtime-access";
    public static bool IsRequest(string[] args) => args.Length == 1 && args[0] == Argument;

    public static RuntimeAccessPlan Plan(string directory, bool includeVm, string runtimeManifestName = RuntimeImages.ManifestName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string relative)
        {
            if (!FenceFiles.TryResolveUnlinked(root, relative.Replace('\\', '/'), out var path) || !File.Exists(path))
                throw new IOException("A required runtime file is missing or linked: " + relative);
            paths.Add(path);
            var parent = Path.GetDirectoryName(path)!;
            while (parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { paths.Add(parent); parent = Path.GetDirectoryName(parent)!; }
        }
        Add("LaunchPad.exe");
        paths.Add(root);
        void AddAsset(string relative, bool managed)
        {
            var normalized = relative.Replace('\\', '/');
            if (!FenceFiles.IsSafe(normalized)) throw new IOException("Runtime dependency metadata contains an unsafe path.");
            // NuGet records managed assets as lib/net*/name.dll, but build and
            // multi-file publish outputs place those assemblies beside the app.
            var deployed = managed || !File.Exists(Path.Combine(root, normalized)) ? Path.GetFileName(normalized) : normalized;
            Add(deployed);
        }
        var depsPath = Path.Combine(root, "LaunchPad.deps.json");
        if (!File.Exists(depsPath) && (File.Exists(Path.Combine(root, "LaunchPad.dll")) || File.Exists(Path.Combine(root, "LaunchPad.runtimeconfig.json"))))
            throw new IOException("The multi-file application runtime is incomplete; LaunchPad.deps.json is missing.");
        if (File.Exists(depsPath))
        {
            Add("LaunchPad.dll"); Add("LaunchPad.deps.json"); Add("LaunchPad.runtimeconfig.json");
            if (new FileInfo(depsPath).Length > 1024 * 1024) throw new IOException("Runtime dependency metadata is too large.");
            using var deps = JsonDocument.Parse(File.ReadAllText(depsPath), new JsonDocumentOptions { MaxDepth = 32 });
            foreach (var target in deps.RootElement.GetProperty("targets").EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                {
                    foreach (var group in new[] { "runtime", "native" })
                        if (library.Value.TryGetProperty(group, out var assets))
                            foreach (var asset in assets.EnumerateObject()) if (!asset.Name.EndsWith("/_._", StringComparison.Ordinal)) AddAsset(asset.Name, managed: group == "runtime");
                    if (library.Value.TryGetProperty("runtimeTargets", out var ridAssets))
                        foreach (var asset in ridAssets.EnumerateObject())
                        {
                            var rid = asset.Value.GetProperty("rid").GetString() ?? "";
                            if (rid == "win" || rid.StartsWith("win", StringComparison.Ordinal) && rid.EndsWith("-x64", StringComparison.Ordinal))
                                AddAsset(asset.Name, managed: asset.Value.TryGetProperty("assetType", out var kind) && kind.GetString() == "runtime");
                        }
                }
        }
        string? sessions = null;
        if (includeVm)
        {
            var qemu = Path.Combine(root, "qemu");
            Add("qemu/qemu-img.exe");
            Add(File.Exists(Path.Combine(qemu, "fence", "qemu-system-x86_64.exe"))
                ? "qemu/fence/qemu-system-x86_64.exe" : "qemu/qemu-system-x86_64.exe");
            if (!Directory.Exists(Path.Combine(qemu, "share"))) throw new IOException("Packaged QEMU firmware is missing.");
            var pending = new Stack<string>(); pending.Push(qemu);
            var entries = 0;
            while (pending.TryPop(out var parent))
            {
                if (!FenceFiles.TryResolveUnlinked(root, Path.GetRelativePath(root, parent).Replace('\\', '/'), out _))
                    throw new IOException("A runtime directory is linked.");
                paths.Add(parent);
                foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
                {
                    if (++entries > 20000) throw new IOException("The runtime contains too many entries.");
                    // Never follow installer firmware junctions or other links.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                    else Add(Path.GetRelativePath(root, entry.FullName));
                }
            }
            var selected = RuntimeImages.Read(root, runtimeManifestName);
            RuntimeImages.Verify(root, selected);
            var images = Path.Combine(root, "images");
            var chain = RestrictedRuntimeAccess.BackingChain(selected.ImagePath, images, images);
            foreach (var image in chain) Add(Path.GetRelativePath(root, image));
            if (selected.Manifest is not null)
            {
                Add("images/" + runtimeManifestName);
                foreach (var image in selected.Manifest.Dependencies.Prepend(selected.Manifest.Image)) Add("images/" + image.File);
                if (selected.Manifest.MaintenanceManifest is { } maintenanceName)
                {
                    var kit = MaintenanceKit.Read(root, maintenanceName);
                    if (kit.Manifest.Version != selected.Manifest.Version) throw new InvalidDataException("The maintenance kit does not match the selected runtime.");
                    Add("images/" + maintenanceName);
                    foreach (var artifact in new[] { kit.Manifest.Kernel, kit.Manifest.Initrd, kit.Manifest.Payload }) Add("images/" + artifact.File);
                }
            }
            sessions = Path.Combine(root, "sessions");
            if (!FenceFiles.TryResolveUnlinked(root, "sessions", out _)) throw new IOException("The managed session directory is linked.");
        }
        return new(root, paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), sessions);
    }

    public static void Apply(RuntimeAccessPlan plan, Action<string>? grantRead = null)
    {
        grantRead ??= RestrictedRuntimeAccess.ReadFile;
        // Validate everything before the first ACL change. Recheck on each
        // grant as well; no recursive grants to the application/session root.
        foreach (var path in plan.ReadPaths)
            if (path != plan.Root && (!FenceFiles.TryResolveUnlinked(plan.Root, Path.GetRelativePath(plan.Root, path).Replace('\\', '/'), out _)
                || !File.Exists(path) && !Directory.Exists(path))) throw new IOException("Runtime inputs changed during repair.");
        if (plan.SessionsDirectory is { } sessions && !FenceFiles.TryResolveUnlinked(plan.Root, "sessions", out _))
            throw new IOException("The managed session directory changed during repair.");
        foreach (var path in plan.ReadPaths) grantRead(path);
        if (plan.SessionsDirectory is { } target)
        { Directory.CreateDirectory(target); grantRead(target); }
    }
}

public sealed class WindowsSetupRepair(AppPaths paths, bool nativeOnly) : IWindowsSetupRepair
{
    public Task<SetupRepairReport> CheckAsync() => Task.Run(() => Check());

    private SetupRepairReport Check(bool restart = false, string? repairFailure = null)
    {
        var checks = new List<SetupRepairCheck>();
        if (!OperatingSystem.IsWindows()) return new([new("Windows setup", false, "Unavailable on this platform.")]);
        var account = TestUserRunner.CheckStoredCredential();
        checks.Add(new("Windows test account", account.Ready, account.Message));
        try
        {
            var plan = WindowsRuntimeRepair.Plan(paths.ExeDirectory, includeVm: !nativeOnly);
            var sessionsReady = nativeOnly || Directory.Exists(plan.SessionsDirectory);
            checks.Add(new("Installed runtime", sessionsReady, sessionsReady
                ? "Required executable/runtime inputs are present. This is not a VM boot or interactive-test result."
                : "The managed sessions directory is missing; repair can create it without changing saved disks."));
            var grantsReady = plan.ReadPaths.Concat(plan.SessionsDirectory is null ? [] : new[] { plan.SessionsDirectory })
                .All(RestrictedRuntimeAccess.HasRequiredReadGrants);
            checks.Add(new("Required runtime grants", grantsReady, grantsReady
                ? "Required read/execute grants are present. This does not prove every effective token permission or interactive startup."
                : "Required account/restricted-code grants are missing or could not be checked. Repair applies them only to runtime inputs."));
        }
        catch (Exception error) { checks.Add(new("Installed runtime", false, error.Message + (nativeOnly
            ? " Repair does not download missing runtime files; reinstall the matching package."
            : " Repair does not download or replace missing images; reinstall the matching package."))); }
        if (!nativeOnly)
        {
            try
            {
                var state = HypervisorCheck.Query();
                checks.Add(new("Windows virtualization", state.FeatureEnabled && state.HypervisorPresent,
                    state.FeatureEnabled && state.HypervisorPresent ? "Windows reports the hypervisor available; VM execution is still a separate check."
                    : HypervisorCheck.Decide(state, false).Message));
            }
            catch { checks.Add(new("Windows virtualization", false, "Windows virtualization could not be checked.")); }
        }
        if (repairFailure is not null) checks.Add(new("Repair operation", false, repairFailure));
        return new(checks, restart);
    }

    public Task<SetupRepairReport> RepairAsync() => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return Check();
        var errors = new List<string>();
        if (!LaunchAccountSetup.TryCreate(out var accountError)) errors.Add(accountError);
        try { WindowsRuntimeRepair.Apply(WindowsRuntimeRepair.Plan(paths.ExeDirectory, !nativeOnly)); }
        catch (Exception error) { errors.Add(error.Message); }
        var restart = false;
        if (!nativeOnly)
        {
            try
            {
                if (!HypervisorCheck.Query().FeatureEnabled)
                {
                    var result = HypervisorCheck.EnableFeature();
                    restart = result.RestartRequired;
                    if (!result.Success) errors.Add("Windows did not enable Hypervisor Platform. Administrator approval may have been canceled.");
                }
            }
            catch { errors.Add("Windows virtualization repair did not finish."); }
        }
        return Check(restart, errors.Count == 0 ? null : string.Join("\n", errors));
    });
}
