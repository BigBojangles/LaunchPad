using System.Text.Json;

namespace LaunchPad.Services;

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static (T Value, string? Hash) Load<T>(string path) where T : class, new()
    {
        try
        {
            var bytes = ReadBytes(path);
            using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return (JsonSerializer.Deserialize<T>(reader.ReadToEnd(), Options) ?? throw new JsonException("The saved record is empty."), Hash(bytes));
        }
        catch (FileNotFoundException) { return (new T(), null); }
        catch (DirectoryNotFoundException) { return (new T(), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new IOException("LaunchPad could not read " + Path.GetFileName(path) + ". The saved file was preserved; repair or restore it before retrying.", error);
        }
    }

    public static string Save<T>(string path, T value, string? expectedHash)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var savedHash = Hash(bytes);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // All LaunchPad instances use this lease; stale snapshots cannot replace
        // another instance's completed edit. External editors may not honor it.
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string? actualHash;
        try { actualHash = Hash(ReadBytes(path)); }
        catch (FileNotFoundException) { actualHash = null; }
        if (actualHash != expectedHash)
            throw new IOException("The saved " + Path.GetFileName(path) + " changed in another window. Restart LaunchPad before saving; that file was not overwritten.");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return savedHash;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static byte[] ReadBytes(string path)
    {
        // An atomic replacement can finish while this reader sees a complete
        // previous generation; its content hash then prevents a stale save.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }
}
