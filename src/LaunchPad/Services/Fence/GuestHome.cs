namespace LaunchPad.Services.Fence;

public static class GuestHome
{
    public const int MaxBytes = 256 * 1024 * 1024;

    public static string Header(int size) =>
        "HOME " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";

    public static string BundlePath(string projectKey, string? root = null) =>
        Path.Combine(root ?? DefaultRoot(), projectKey, "bundle.tar");

    public static byte[]? Read(string projectKey, string? root = null)
    {
        try
        {
            var path = BundlePath(projectKey, root);
            if (!File.Exists(path))
                return null;

            var body = File.ReadAllBytes(path);
            if (body.Length == 0 || body.Length > MaxBytes)
                return null;

            return body;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Save(string projectKey, byte[]? body, string? root = null)
    {
        if (body is null || body.Length == 0 || body.Length > MaxBytes)
            return;

        var path = BundlePath(projectKey, root);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, body);
    }

    private static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LaunchPad", "grok-home", "projects");
    }
}
