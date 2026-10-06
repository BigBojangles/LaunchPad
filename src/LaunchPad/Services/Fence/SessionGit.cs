namespace LaunchPad.Services.Fence;

public static class SessionGit
{
    public const string FenceOrigin = "file:///home/builder/fence.git";

    public static void PointCopyAtFence(string sessionCopy)
    {
        var configPath = Path.Combine(sessionCopy, ".git", "config");
        if (!File.Exists(configPath))
            return;

        var lines = File.ReadAllLines(configPath);
        var inOrigin = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trim = lines[i].Trim();
            if (trim.StartsWith('[') && trim.EndsWith(']'))
            {
                inOrigin = string.Equals(trim, "[remote \"origin\"]", StringComparison.Ordinal);
                continue;
            }

            if (inOrigin && trim.StartsWith("url", StringComparison.Ordinal))
                lines[i] = "\turl = " + FenceOrigin;
        }

        File.WriteAllLines(configPath, lines);
    }
}
