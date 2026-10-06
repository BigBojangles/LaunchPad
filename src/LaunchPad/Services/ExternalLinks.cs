using System.Diagnostics;

namespace LaunchPad.Services;

public static class ExternalLinks
{
    public const string XProfile = "https://x.com/BigBojangles_";
    public const string Guide = "https://github.com/BigBojangles/launch-pad/blob/main/README.md";
    public const string Repo = "https://github.com/BigBojangles/launch-pad";
    public const string GrokChat = "https://grok.com";

    public static void Open(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
