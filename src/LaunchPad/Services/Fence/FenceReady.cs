namespace LaunchPad.Services.Fence;

public static class FenceReady
{
    public static bool Installed()
    {
        var root = QemuLayout.Root;
        var qemuDir = Path.Combine(root, "qemu");
        if (!File.Exists(Path.Combine(qemuDir, "qemu-img.exe")))
            return false;

        var system = Path.Combine(qemuDir, "fence", "qemu-system-x86_64.exe");
        if (!File.Exists(system))
            system = Path.Combine(qemuDir, "qemu-system-x86_64.exe");
        if (!File.Exists(system))
            return false;

        var images = Path.Combine(root, "images");
        return File.Exists(Path.Combine(images, "debian-12-builder.qcow2"))
            && File.Exists(Path.Combine(images, PublicRuntime.BaseName));
    }
}
