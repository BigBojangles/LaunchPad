namespace LaunchPad.Services;

public static class SessionPaths
{
    public static bool TryResolveSessionGroupPath(string sessionGroupDirectory, out string projectPath)
    {
        projectPath = "";
        if (string.IsNullOrWhiteSpace(sessionGroupDirectory))
            return false;

        var cwdFile = Path.Combine(sessionGroupDirectory, ".cwd");
        if (File.Exists(cwdFile))
        {
            try
            {
                var fromFile = File.ReadAllText(cwdFile).Trim();
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    projectPath = fromFile;
                    return true;
                }
            }
            catch
            {
                // Fall through to the folder name.
            }
        }

        var folderName = Path.GetFileName(sessionGroupDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return TryDecodeFolderName(folderName, out projectPath);
    }

    public static bool TryDecodeFolderName(string folderName, out string projectPath)
    {
        projectPath = "";
        if (string.IsNullOrWhiteSpace(folderName))
            return false;

        try
        {
            var decoded = Uri.UnescapeDataString(folderName);
            if (LooksLikePath(decoded))
            {
                projectPath = decoded;
                return true;
            }
        }
        catch (UriFormatException)
        {
            return false;
        }

        return false;
    }

    public static bool IsExcludedProjectPath(string projectPath, AppPaths paths)
    {
        string full;
        try
        {
            full = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return true;
        }

        var home = Path.GetFullPath(paths.UserProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(home, StringComparison.OrdinalIgnoreCase))
            return true;

        var grokHome = Path.GetFullPath(paths.GrokHome).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(grokHome, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(grokHome + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
            (value[2] == '\\' || value[2] == '/'))
            return true;

        return value.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
