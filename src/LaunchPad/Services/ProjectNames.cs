using System.Text.RegularExpressions;

namespace LaunchPad.Services;

public static class ProjectNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool TrySanitize(string? raw, out string name, out string error)
    {
        name = "";
        error = "";

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Please type a name for your project.";
            return false;
        }

        var cleanedChars = raw
            .Trim()
            .Select(ch => @"<>:""/\|?*".Contains(ch) ? ' ' : ch)
            .Where(ch => !char.IsControl(ch))
            .ToArray();

        var cleaned = Regex.Replace(new string(cleanedChars), @"\s+", " ").Trim().TrimEnd('.');

        if (cleaned.Length == 0 || cleaned is "." or ".." || Reserved.Contains(cleaned))
        {
            error = "That name can’t be used for a folder. Try another.";
            return false;
        }

        if (cleaned.Length > 80)
            cleaned = cleaned[..80].TrimEnd('.', ' ');

        if (cleaned.Length == 0)
        {
            error = "That name can’t be used for a folder. Try another.";
            return false;
        }

        name = cleaned;
        return true;
    }
}
