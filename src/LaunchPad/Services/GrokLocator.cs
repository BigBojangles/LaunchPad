namespace LaunchPad.Services;

public sealed class GrokLocator
{
    private readonly AppPaths _paths;

    public GrokLocator(AppPaths paths)
    {
        _paths = paths;
    }

    public string? FindGrokExecutable()
    {
        if (File.Exists(_paths.GrokExe))
            return _paths.GrokExe;

        var fromPath = FindOnPath("grok.exe");
        if (fromPath is not null)
            return fromPath;

        return null;
    }

    public bool IsGrokInstalled() => FindGrokExecutable() is not null;

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
