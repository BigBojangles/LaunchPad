namespace LaunchPad.Services.Fence;

public static class QemuLayout
{
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LAUNCHPAD_QEMU");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(exeDir)
                && (File.Exists(Path.Combine(exeDir, "qemu", "fence", "qemu-system-x86_64.exe"))
                    || File.Exists(Path.Combine(exeDir, "qemu", "qemu-system-x86_64.exe"))))
                return exeDir;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "projects",
                "build-launch-qemu");
        }
    }

    public static string? FindExe(string fileName)
    {
        var qemuDir = Path.Combine(Root, "qemu");
        if (!Directory.Exists(qemuDir))
            return null;

        return Directory.EnumerateFiles(qemuDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    public static string AsideRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchPad",
            "fence",
            "aside");

    public static string SessionsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchPad",
            "fence",
            "sessions");

    public static string LaunchSessionsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchPad",
            "sessions");

    public static string KeptImagePath => RuntimeImages.Read(Root).ImagePath;

    public static string ProjectKey(string liveProject)
    {
        var full = Path.GetFullPath(liveProject).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }
}
