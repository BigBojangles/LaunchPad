using System.Text.RegularExpressions;

namespace LaunchPad.Services.Fence;

public static class GuestWarnings
{
    private static readonly Regex Hex64 = new(
        "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Keep(string log)
    {
        var kept = new List<string>();
        foreach (var raw in log.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (!line.StartsWith("WARN ", StringComparison.Ordinal))
                continue;
            if (Hex64.IsMatch(line))
                continue;
            kept.Add(line);
        }

        return kept;
    }

    public static int Hex64Count(string text) => Hex64.Matches(text).Count;
}
