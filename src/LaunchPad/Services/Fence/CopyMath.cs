namespace LaunchPad.Services.Fence;

public static class CopyMath
{
    public const long LargeBytes = 512L * 1024 * 1024;

    public static bool IsLarge(long bytes) => bytes > LargeBytes;

    public static int Percent(long sent, long total)
    {
        if (total <= 0)
            return 0;

        var percent = sent * 100 / total;
        if (percent < 0)
            return 0;
        if (percent > 100)
            return 100;
        return (int)percent;
    }

    public static string? Remaining(long sent, long total, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1 || sent <= 0 || total <= sent)
            return null;

        var rate = sent / elapsed.TotalSeconds;
        if (rate <= 0)
            return null;

        var seconds = (long)Math.Round((total - sent) / rate);
        if (seconds < 60)
            return "about " + Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s left";

        var minutes = (seconds + 30) / 60;
        return "about " + minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m left";
    }
}
