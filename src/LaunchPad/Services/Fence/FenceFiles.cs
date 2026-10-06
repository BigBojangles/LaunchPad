namespace LaunchPad.Services.Fence;

public static class FenceFiles
{
    public static string? Relative(string root, string file)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullFile = Path.GetFullPath(file);
        if (!fullFile.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        var relative = fullFile[(fullRoot.Length + 1)..].Replace('\\', '/');
        if (!IsSafe(relative))
            return null;

        return relative;
    }

    public static bool IsSafe(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Any(c => c < 32 || c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            return false;

        if (relative.StartsWith('/') || relative.Contains(':'))
            return false;

        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))
                return false;
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
                return false;
        }

        return true;
    }

    public static bool TryResolveUnlinked(string root, string relative, out string resolved)
    {
        resolved = "";
        if (!IsSafe(relative)) return false;
        try
        {
            var current = Path.GetFullPath(root);
            for (var ancestor = current; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (IsLink(ancestor)) return false;
            foreach (var part in relative.Split('/'))
            {
                current = Path.Combine(current, part);
                if (IsLink(current)) return false;
            }
            resolved = current;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static string Header(long size, string relative) =>
        "FILE " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + relative + "\n";

    public static IEnumerable<string> Enumerate(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Relative(root, file) is not null)
                yield return file;
        }
    }
}
