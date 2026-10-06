namespace LaunchPad.Services.Fence;

public sealed class SentManifest
{
    private readonly Dictionary<string, (long Size, long Ticks, string? Sha256)> _files = new(StringComparer.OrdinalIgnoreCase);

    public static SentManifest Load(string? path)
    {
        var manifest = new SentManifest();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return manifest;

        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
                continue;
            if (!long.TryParse(parts[0], out var size) || !long.TryParse(parts[1], out var ticks))
                continue;
            var relative = parts[2];
            if (!FenceFiles.IsSafe(relative))
                continue;
            var hash = parts.Length > 3 && parts[3].Length == 64 && parts[3].All(Uri.IsHexDigit) ? parts[3] : null;
            manifest._files[relative] = (size, ticks, hash);
        }

        return manifest;
    }

    public bool Any => _files.Count > 0;

    public bool Unchanged(string relative, long size, long ticks) =>
        _files.TryGetValue(relative, out var known) && known.Sha256 is not null && known.Size == size && known.Ticks == ticks;

    public void Note(string relative, long size, long ticks, string? sha256 = null) => _files[relative] = (size, ticks, sha256);

    public string? ContentHash(string relative) => _files.TryGetValue(relative, out var known) ? known.Sha256 : null;

    public byte[] GuestBaseline() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        _files.Where(pair => pair.Value.Sha256 is { Length: 64 } && pair.Value.Sha256.All(Uri.IsHexDigit))
            .Select(pair => new object[] { pair.Key, pair.Value.Size, 420, 0, pair.Value.Sha256!.ToLowerInvariant() }).ToArray());

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var lines = new List<string>(_files.Count);
        foreach (var pair in _files)
            lines.Add(pair.Value.Size + "\t" + pair.Value.Ticks + "\t" + pair.Key + "\t" + pair.Value.Sha256);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                foreach (var line in lines) writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
