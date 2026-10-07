namespace LaunchPad.Services.Fence;

public static class RocketPicture
{
    // Supplied rocket. Spaces stay empty. Every other cell fills from the bottom.
    private static readonly string[] Shape = ReadSuppliedArt();

    private static string[] ReadSuppliedArt()
    {
        using var input = typeof(RocketPicture).Assembly.GetManifestResourceStream("LaunchPad.LoadingRocket.txt")
            ?? throw new InvalidOperationException("The supplied LaunchPad loading artwork is missing.");
        using var reader = new StreamReader(input);
        var lines = reader.ReadToEnd().Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) throw new InvalidDataException("The loading artwork is empty.");
        var content = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var left = content.Min(line => line.Length - line.TrimStart(' ').Length);
        var right = content.Max(line => line.TrimEnd(' ').Length);
        // The existing renderer halves both axes. Expand each supplied cell so
        // its visible output retains Casey's exact ASCII at native resolution.
        return lines.Select(line => line.PadRight(right).Substring(left, right - left))
            .Select(line => string.Concat(line.Select(character => new string(character, 2))))
            .SelectMany(line => new[] { line, line }).ToArray();
    }
    public static int FillableCount { get; } = Shape.Sum(line => line.Count(item => item != ' '));

    public static IReadOnlyList<string> Lines(int percent)
    {
        _ = Math.Clamp(percent, 0, 100);
        return Shape;
    }

    public static IReadOnlyList<string> Mask(int percent)
    {
        var wanted = FillableCount * Math.Clamp(percent, 0, 100) / 100;
        var seen = 0;
        var rows = new string[Shape.Length];
        for (var row = Shape.Length - 1; row >= 0; row--)
        {
            var source = Shape[row];
            var chars = new char[source.Length];
            for (var column = source.Length - 1; column >= 0; column--)
            {
                if (source[column] == ' ')
                {
                    chars[column] = ' ';
                    continue;
                }

                seen++;
                chars[column] = seen <= wanted ? 'o' : 'e';
            }

            rows[row] = new string(chars);
        }

        return rows;
    }

    public static int FilledCount(int percent) =>
        Mask(percent).Sum(line => line.Count(item => item == 'o'));
}
