namespace LaunchPad.Services.Fence;

public enum FenceBoot
{
    Prep,
    Import,
    Grok,
    Export
}

public sealed record FenceShare(string HostPath, string MountTag);

public static class QemuCommand
{
    public static IReadOnlyList<string> Build(
        string accel,
        string diskPath,
        int sshPort,
        int qmpPort,
        string serial,
        FenceShare? share,
        string? serialLog = null,
        int memoryMb = 0,
        int cores = 0,
        string? firmwareDir = null,
        string? workingDirectory = null)
    {
        _ = accel;
        _ = sshPort;
        _ = share;
        _ = serial;
        var megabytes = memoryMb >= 2048 ? memoryMb : GuestMemory.DefaultMegabytes;
        var smp = cores >= 1 ? cores : GuestMemory.DefaultCores;
        var fencePort = FencePort(qmpPort);
        var statusPort = StatusPort(qmpPort);
        var tuiPort = TuiPort(qmpPort);
        var firmware = string.IsNullOrWhiteSpace(firmwareDir) ? "share" : firmwareDir;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            // Resolve against the unchanged QEMU cwd, keeping the alternate-user
            // command within Windows' limit without changing any runtime files.
            diskPath = ShorterPath(diskPath, workingDirectory);
            firmware = ShorterPath(firmware, workingDirectory);
            if (!Path.IsPathFullyQualified(firmware)
                && Path.Combine(workingDirectory, firmware, "linuxboot_dma.bin").Length >= 260)
                firmware = Path.GetFullPath(firmware, Path.GetFullPath(workingDirectory));
            if (!string.IsNullOrEmpty(serialLog))
                serialLog = ShorterPath(serialLog, workingDirectory);
        }
        var args = new List<string>
        {
            "-machine", "q35",
            "-accel", "whpx",
            // qemu64 hides modern instructions required by bundled native CLIs.
            // max exposes only features supported by the active accelerator.
            "-cpu", "max",
            "-display", "none",
            "-vga", "none",
            "-smp", smp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-m", megabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-nodefaults",
            "-no-reboot",
            "-L", firmware,
            "-serial", string.IsNullOrEmpty(serialLog) ? "none" : "file:" + serialLog,
            // writethrough so a hard stop still leaves the session disk readable.
            "-drive", "file=" + diskPath + ",if=virtio,format=qcow2,media=disk,cache=writethrough",
            "-qmp", "tcp:127.0.0.1:" + qmpPort + ",server=on,wait=off",
            "-device", "virtio-serial-pci,id=vserial0,console-size=on",
            "-chardev", "socket,id=fencech,host=127.0.0.1,port=" + fencePort + ",server=on,wait=off",
            "-device", "virtserialport,bus=vserial0.0,chardev=fencech,name=fence",
            "-chardev", "socket,id=statusch,host=127.0.0.1,port=" + statusPort + ",server=on,wait=off",
            "-device", "virtserialport,bus=vserial0.0,chardev=statusch,name=status",
            "-chardev", "socket,id=ttych,host=127.0.0.1,port=" + tuiPort + ",server=on,wait=off",
            "-device", "virtconsole,bus=vserial0.0,chardev=ttych,name=tui",
            "-netdev", "user,id=net0",
            "-device", "virtio-net-pci,netdev=net0"
        };

        return args;
    }

    private static string ShorterPath(string path, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(path))
            return path;
        var relative = Path.GetRelativePath(workingDirectory, path);
        return relative.Length < path.Length ? relative : path;
    }

    public static IReadOnlyList<string> BuildMaintenance(string disk, string kernel, string initrd, int qmpPort,
        int consolePort, string firmwareDirectory, string workingDirectory, int payloadPort) =>
    [
        "-machine", "q35", "-accel", "whpx", "-cpu", "max", "-display", "none", "-vga", "none",
        "-smp", "2", "-m", "2048", "-nodefaults", "-no-reboot", "-net", "none",
        "-L", Path.IsPathFullyQualified(firmwareDirectory) ? firmwareDirectory : Path.GetFullPath(firmwareDirectory, Path.GetFullPath(workingDirectory)),
        "-drive", "file=" + MaintenancePath(disk, workingDirectory) + ",if=virtio,format=qcow2,media=disk,cache=writethrough",
        "-kernel", MaintenancePath(kernel, workingDirectory), "-initrd", MaintenancePath(initrd, workingDirectory),
        "-append", "root=/dev/vda1 rw init=/bin/sh console=ttyS0,115200 panic=-1",
        "-serial", "tcp:127.0.0.1:" + consolePort + ",server=on,wait=off",
        "-qmp", "tcp:127.0.0.1:" + qmpPort + ",server=on,wait=off",
        "-device", "virtio-serial-pci,id=m",
        "-chardev", "socket,id=p,host=127.0.0.1,port=" + payloadPort + ",server=on,wait=off",
        "-device", "virtserialport,bus=m.0,chardev=p,name=launchpad-maintenance"
    ];

    private static string MaintenancePath(string path, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        var relative = Path.GetRelativePath(workingDirectory, path);
        // This custom Windows QEMU also checks the unnormalized cwd+path.
        // A shorter argument can still produce an overlong expanded path.
        return relative.Length < path.Length && Path.Combine(workingDirectory, relative).Length < 260 ? relative : path;
    }

    public static int FencePort(int qmpPort) => qmpPort + 1;

    public static int StatusPort(int qmpPort) => qmpPort + 2;

    public static int TuiPort(int qmpPort) => qmpPort + 3;

    public static bool FsdevIsDisabled(string helpText)
    {
        if (string.IsNullOrWhiteSpace(helpText))
            return true;

        return helpText.Contains("fsdev support is disabled", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Mentions(IReadOnlyList<string> args, string text)
    {
        foreach (var arg in args)
        {
            if (arg.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
