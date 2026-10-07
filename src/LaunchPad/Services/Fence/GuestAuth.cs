using System.Text.Json;
using System.Security.Cryptography;

namespace LaunchPad.Services.Fence;

public static class GuestAuth
{
    public const int MaxBytes = 1048576;

    public static string HostFile
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "LaunchPad", "grok-home", "auth.json");
        }
    }

    public static string Header(int size) =>
        "AUTH " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";

    public static byte[]? ReadHost(string? directory = null, INotificationSecretProtector? protector = null)
    {
        try
        {
            directory ??= Path.GetDirectoryName(HostFile)!;
            if (!FenceFiles.TryResolveUnlinked(directory, "auth.dpapi", out var encrypted)
                || !FenceFiles.TryResolveUnlinked(directory, "auth.json", out var legacy)) return null;
            protector ??= new WindowsNotificationSecretProtector("LaunchPad Grok sign-in");
            if (File.Exists(encrypted))
            {
                if (new FileInfo(encrypted).Length > MaxBytes + 16384) return null;
                var body = protector.Unprotect(File.ReadAllBytes(encrypted));
                return IsDocument(body) ? body : null;
            }
            if (!File.Exists(legacy) || new FileInfo(legacy).Length > MaxBytes) return null;
            var previous = File.ReadAllBytes(legacy);
            if (!IsDocument(previous)) return null;
            // Keep the existing legacy file intact; future writes use encrypted storage.
            Save(previous, directory, protector);
            return previous;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public static void Save(byte[]? body, string? directory = null, INotificationSecretProtector? protector = null)
    {
        // Missing/empty guest state is not authority to delete a saved login.
        if (!IsDocument(body))
            return;
        directory ??= Path.GetDirectoryName(HostFile)!;
        if (!FenceFiles.TryResolveUnlinked(directory, "auth.dpapi", out var path))
            throw new IOException("The saved login location contains a link.");
        var encrypted = (protector ?? new WindowsNotificationSecretProtector("LaunchPad Grok sign-in")).Protect(body!);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "auth-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(encrypted); output.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsDocument(byte[]? body)
    {
        if (body is null || body.Length == 0 || body.Length > MaxBytes)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && HasToken(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasToken(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name is "access_token" or "refresh_token" or "id_token" or "token" or "key"
                    && prop.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(prop.Value.GetString()))
                    return true;
                if (HasToken(prop.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasToken(item))
                    return true;
            }
        }

        return false;
    }
}
