namespace LaunchPad.Services.Fence;

public static class CopyBack
{
    public static bool TryApply(string asideDirectory, string liveProject)
    {
        if (File.Exists(Path.Combine(asideDirectory, "COPY-FAILED")))
            return false;

        Apply(asideDirectory, liveProject);
        return true;
    }

    public static void Apply(string asideDirectory, string liveProject)
    {
        if (!Directory.Exists(asideDirectory))
            throw new DirectoryNotFoundException(asideDirectory);

        Directory.CreateDirectory(liveProject);
        foreach (var file in Directory.EnumerateFiles(asideDirectory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "COPY-FAILED", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(asideDirectory, file);
            var dest = Path.Combine(liveProject, relative);
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            File.Copy(file, dest, overwrite: true);
        }
    }
}
