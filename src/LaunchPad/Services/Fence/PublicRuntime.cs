namespace LaunchPad.Services.Fence;

public sealed class PublicRuntime
{
    public const string BaseName = "debian-12-nocloud-amd64-20260601-2496.qcow2";

    private PublicRuntime(string qemuDirectory, string qemuExe, string imgExe, string keptImage, string sessions, string firmwareDir, string? version)
    {
        QemuDirectory = qemuDirectory;
        QemuExe = qemuExe;
        ImgExe = imgExe;
        KeptImage = keptImage;
        Sessions = sessions;
        FirmwareDir = firmwareDir;
        Version = version;
    }

    public string QemuDirectory { get; }
    public string QemuExe { get; }
    public string ImgExe { get; }
    public string KeptImage { get; }
    public string Sessions { get; }
    public string FirmwareDir { get; }
    public string? Version { get; }

    public static PublicRuntime Ensure(SetupLog log)
    {
        _ = log;
        var root = QemuLayout.Root;
        var qemuDir = Path.Combine(root, "qemu");
        var imgExe = Path.Combine(qemuDir, "qemu-img.exe");
        if (!File.Exists(imgExe))
            throw new FileNotFoundException("qemu-img.exe is not next to QEMU.");

        // The console-size QEMU keeps its own DLLs. The Stefan Weil tools stay
        // in the parent folder so qemu-img still loads the DLLs it was built with.
        var launchDir = Path.Combine(qemuDir, "fence");
        var qemuExe = Path.Combine(launchDir, "qemu-system-x86_64.exe");
        if (!File.Exists(qemuExe))
        {
            launchDir = qemuDir;
            qemuExe = Path.Combine(qemuDir, "qemu-system-x86_64.exe");
        }

        if (!File.Exists(qemuExe))
            throw new FileNotFoundException("QEMU is not in the folder beside this project.");

        if (!Directory.Exists(launchDir))
            throw new DirectoryNotFoundException("The QEMU launch folder is missing.");

        var firmwareDir = Path.Combine(launchDir, "share");
        if (!Directory.Exists(firmwareDir))
        {
            var parentShare = Path.Combine(qemuDir, "share");
            if (Directory.Exists(parentShare))
                firmwareDir = parentShare;
            else
                throw new DirectoryNotFoundException("QEMU firmware share folder is missing.");
        }

        var selection = RuntimeImages.Read(root);
        RuntimeImages.Verify(root, selection);
        var kept = selection.ImagePath;
        var baseImage = Path.Combine(root, "images", BaseName);
        if (!File.Exists(kept) || !File.Exists(baseImage))
            throw new FileNotFoundException("The kept Debian image is not in the image folder.");

        RestrictedRuntimeAccess.ReadRuntimeTree(qemuDir);
        foreach (var image in selection.Manifest is null
            ? new[] { kept, baseImage }
            : selection.Manifest.Dependencies.Prepend(selection.Manifest.Image)
                .Select(item => Path.Combine(root, "images", item.File)))
            RestrictedRuntimeAccess.ReadFile(image);

        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        return new PublicRuntime(launchDir, qemuExe, imgExe, kept, sessions, Path.GetFullPath(firmwareDir), selection.Manifest?.Version);
    }
}
