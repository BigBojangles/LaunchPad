namespace LaunchPad.Services;

public static class ProjectOpen
{
    public static bool TryOpen(string path, out string error)
    {
        error = "";
        if (!Directory.Exists(path))
        {
            error = "That project folder is no longer there.";
            return false;
        }

        return true;
    }
}
