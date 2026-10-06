namespace LaunchPad.Services.Fence;

public static class WindowsWork
{
    public static bool TryResolve(string root, string requested, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(requested))
            return false;

        try
        {
            var baseFull = Path.GetFullPath(root);
            var combined = Path.IsPathRooted(requested) ? requested : Path.Combine(baseFull, requested);
            full = Path.GetFullPath(combined);
            var prefix = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var cursor = full;
            while (true)
            {
                if (File.Exists(cursor) || Directory.Exists(cursor))
                {
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                        return false;
                }

                if (string.Equals(cursor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    return true;

                var parent = Path.GetDirectoryName(cursor);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
                    return false;
                cursor = parent;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsShell(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }
}
