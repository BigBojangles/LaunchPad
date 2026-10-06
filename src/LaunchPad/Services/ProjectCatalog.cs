using LaunchPad.Models;

namespace LaunchPad.Services;

public sealed class ProjectCatalog
{
    private readonly SettingsStore _settings;

    public ProjectCatalog(AppPaths paths, SettingsStore settings)
    {
        _ = paths;
        _settings = settings;
    }

    public IReadOnlyList<ProjectEntry> ListProjects()
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        AddKnownProjects(map);

        return map
            .Select(pair => new ProjectEntry
            {
                Name = _settings.DisplayNameFor(pair.Key),
                Path = pair.Key,
                LastActivity = pair.Value
            })
            .OrderByDescending(p => p.LastActivity)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void AddKnownProjects(Dictionary<string, DateTimeOffset> map)
    {
        foreach (var known in _settings.KnownProjects)
        {
            if (string.IsNullOrWhiteSpace(known.Path))
                continue;

            TryAdd(map, known.Path);
        }
    }

    private void TryAdd(Dictionary<string, DateTimeOffset> map, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return;
        }

        if (!Directory.Exists(full))
            return;

        var stamp = _settings.GetLastOpened(full)
            ?? SafeLastWriteUtc(full);

        if (map.TryGetValue(full, out var existing))
        {
            if (stamp > existing)
                map[full] = stamp;
            return;
        }

        map[full] = stamp;
    }

    private static DateTimeOffset SafeLastWriteUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }
}
