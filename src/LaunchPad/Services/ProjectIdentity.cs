using LaunchPad.Views;

namespace LaunchPad.Services;

public static class ProjectIdentity
{
    public static string ColorHex(SettingsStore settings, string project)
    {
        var index = IndexFor(project, settings.KnownProjects.Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
        var color = IdentityPalette.At(index);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static int IndexFor(string project, IReadOnlyList<string> known,
        IReadOnlyList<LaunchPad.Models.ProjectEntry>? projects = null)
    {
        static bool Same(string a, string b)
        {
            try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        for (var i = 0; i < known.Count; i++) if (Same(known[i], project)) return i;
        if (projects is not null)
            for (var i = 0; i < projects.Count; i++) if (Same(projects[i].Path, project)) return known.Count + i;
        return 0;
    }
    public static bool IsColorHex(string? value) => value is { Length: 7 } && value[0] == '#'
        && value.AsSpan(1).ToArray().All(Uri.IsHexDigit);
}
