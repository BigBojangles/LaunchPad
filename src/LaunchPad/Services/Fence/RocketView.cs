using System.Globalization;
using System.Text;

namespace LaunchPad.Services.Fence;

public static class RocketView
{
    public static bool TryRead(string text, out long sent, out long total, out long files, out string name) =>
        TryRead(text, out sent, out total, out files, out name, out _);

    public static bool TryRead(string text, out long sent, out long total, out long files, out string name, out bool firstCopy)
    {
        sent = 0;
        total = 0;
        files = 0;
        name = "";
        firstCopy = false;
        var parts = text.Split('\t');
        if (parts.Length < 3)
            return false;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out sent))
            return false;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
            return false;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out files))
            return false;
        if (parts.Length > 3)
            name = parts[3];
        if (parts.Length > 4)
            firstCopy = parts[4] == "1";
        return true;
    }

    public static string OpenScreen() =>
        "\u001b[?1049h\u001b[?7l\u001b[?25l\u001b[48;2;233;228;220m\u001b[38;2;44;61;92m\u001b[2J\u001b[H";

    public static string Frame(long sent, long total, long files, TimeSpan elapsed, int columns, int rows = 0, bool firstCopy = false)
    {
        if (columns < 20)
            columns = 20;

        var percent = CopyMath.Percent(sent, total);
        var art = Half(RocketPicture.Lines(percent)).ToList();
        var mask = Half(RocketPicture.Mask(percent)).ToList();
        var footer = new List<string>
        {
            "",
            files.ToString(CultureInfo.InvariantCulture) + " files  ·  "
                + Megabytes(sent) + " MB of " + Megabytes(total) + " MB  ·  "
                + percent.ToString(CultureInfo.InvariantCulture) + "%"
        };
        var left = CopyMath.Remaining(sent, total, elapsed);
        if (left is not null)
            footer.Add(left);

        var header = new List<string>();
        header.Add(sent < total ? SealText.WarmingUp : SealText.VmLaunching);
        if (firstCopy)
        {
            header.Add("Copying your project into the sandbox.");
            header.Add("First open, so this one takes a minute.");
        }
        else
        {
            header.Add("Copying what changed.");
        }

        header.Add("");
        var chrome = header.Count + footer.Count;
        if (rows > chrome && art.Count + chrome > rows)
        {
            var room = rows - chrome;
            var skip = (art.Count - room) / 2;
            art = art.Skip(skip).Take(room).ToList();
            mask = mask.Skip(skip).Take(room).ToList();
        }

        var lines = new List<string>(header);
        var marks = new List<string>();
        foreach (var line in header)
            marks.Add("");
        lines.AddRange(art);
        marks.AddRange(mask);
        lines.AddRange(footer);
        marks.AddRange(footer.Select(line => new string(' ', line.Length)));

        var top = 1;
        if (rows > lines.Count)
            top = 1 + (rows - lines.Count) / 2;

        var width = columns - 1;
        var picture = new StringBuilder();
        picture.Append("\u001b[48;2;233;228;220m");
        picture.Append("\u001b[38;2;44;61;92m");
        for (var index = 0; index < lines.Count; index++)
        {
            var shown = Window(lines[index], width);
            var shownMask = Window(marks[index], width);
            var pad = Math.Max(0, (width - shown.Length) / 2);
            picture.Append('\u001b').Append('[').Append(top + index).Append(";1H");
            picture.Append("\u001b[38;2;44;61;92m");
            picture.Append(' ', pad);
            var small = firstCopy && index == 2;
            if (small)
                picture.Append("\u001b[2m");
            AppendColored(picture, shown, shownMask);
            if (small)
                picture.Append("\u001b[22m");
            picture.Append("\u001b[48;2;233;228;220m");
            picture.Append("\u001b[K");
        }

        picture.Append("\u001b[").Append(top + lines.Count).Append(";1H");
        picture.Append("\u001b[J");
        return picture.ToString();
    }

    public static IReadOnlyList<string> Half(IReadOnlyList<string> rows)
    {
        var result = new List<string>((rows.Count + 1) / 2);
        for (var row = 0; row < rows.Count; row += 2)
        {
            var line = rows[row];
            var count = (line.Length + 1) / 2;
            var chars = new char[count];
            for (var column = 0; column < count; column++)
                chars[column] = line[column * 2];
            result.Add(new string(chars));
        }

        return result;
    }

    private static string Window(string line, int width)
    {
        if (line.Length <= width)
            return line;

        var start = (line.Length - width) / 2;
        return line.Substring(start, width);
    }

    private static void AppendColored(StringBuilder picture, string line, string mask)
    {
        var mode = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var mark = index < mask.Length ? mask[index] : ' ';
            var next = mark == 'o' ? 'o' : mark == 'e' ? 'e' : 'n';
            if (next != mode)
            {
                mode = next;
                picture.Append(next switch
                {
                    'o' => "\u001b[38;2;240;120;40m",
                    'e' => "\u001b[38;2;168;152;128m",
                    _ => "\u001b[38;2;44;61;92m"
                });
            }

            picture.Append(line[index]);
        }
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
}
