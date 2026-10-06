using System.Text;

namespace LaunchPad.Services.Fence;

public static class ConfigHeal
{
    public const string Marker = "GROK-HOME builder";
    public const int SearchBytes = 1024 * 1024;

    public static bool LogShowsBrokenConfig(string? log)
    {
        if (string.IsNullOrEmpty(log))
            return false;
        if (log.Contains(Marker, StringComparison.Ordinal))
            return false;

        return log.Contains("AUTH-IN ", StringComparison.Ordinal)
            && log.Contains("fence-ready", StringComparison.Ordinal);
    }

    public static bool ImageHasConfigFix(string imagePath) => ImageHasText(imagePath, Marker);

    public static bool ImageHasText(string imagePath, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return false;

            if (string.IsNullOrEmpty(text))
                return false;
            var needle = Encoding.ASCII.GetBytes(text);
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[SearchBytes];
            var filled = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return false;

                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == needle[filled])
                    {
                        filled++;
                        if (filled == needle.Length)
                            return true;
                        continue;
                    }

                    filled = value == needle[0] ? 1 : 0;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void StopForRecovery(string sessionDir)
    {
        // A session overlay can contain login, history, tools and unreturned work.
        // A newer template is never permission to delete that durable state.
        throw new InvalidOperationException(
            "This session could not read its saved guest configuration or start the selected agent. "
            + "Its VM disk and saved state have been preserved for recovery at: "
            + Path.GetFullPath(sessionDir));
    }
}
