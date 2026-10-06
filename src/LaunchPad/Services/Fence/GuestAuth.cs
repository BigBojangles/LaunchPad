using System.Text.Json;

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

    public static byte[]? ReadHost()
    {
        try
        {
            if (!File.Exists(HostFile))
                return null;

            var body = File.ReadAllBytes(HostFile);
            if (body.Length == 0 || body.Length > MaxBytes)
                return null;

            return body;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Save(byte[]? body)
    {
        if (body is null)
            return;

        var path = HostFile;
        if (body.Length == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        if (body.Length > MaxBytes || !IsDocument(body))
            return;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, body);
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
                if (prop.Name is "access_token" or "refresh_token" or "id_token" or "token"
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
