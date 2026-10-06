namespace LaunchPad.Services;

public static class ProjectFolders
{
    public static bool TryRename(string oldPath, string rawName, out string newPath, out string error)
    {
        newPath = "";
        error = "";

        if (!Directory.Exists(oldPath))
        {
            error = "That project folder is no longer there.";
            return false;
        }

        if (!ProjectNames.TrySanitize(rawName, out var name, out error))
            return false;

        var parent = Path.GetDirectoryName(Path.GetFullPath(oldPath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            error = "Couldn’t rename that folder. Try another name.";
            return false;
        }

        var dest = Path.Combine(parent, name);
        if (string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            newPath = dest;
            return true;
        }

        if (Directory.Exists(dest) || File.Exists(dest))
        {
            error = "A folder with that name already exists. Choose a different name.";
            return false;
        }

        try
        {
            Directory.Move(oldPath, dest);
            newPath = dest;
            return true;
        }
        catch
        {
            error = "Couldn’t rename that folder. Try another name.";
            return false;
        }
    }

    public static void OpenInExplorer(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + path + "\"",
            UseShellExecute = true
        });
    }
}
