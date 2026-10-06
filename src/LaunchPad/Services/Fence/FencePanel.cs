namespace LaunchPad.Services.Fence;

public static class FencePanel
{
    public const string Busy = "busy";
    public const string NeedsAnswer = "needs an answer";
    public const string Stopped = "stopped";

    public static string Failure(string message) =>
        string.IsNullOrWhiteSpace(message) ? Stopped : message;

    public static string Label(string? statusLine, bool vmStopped)
    {
        if (vmStopped)
            return Stopped;

        var line = statusLine?.Trim() ?? "";
        if (string.Equals(line, "needs-an-answer", StringComparison.Ordinal))
            return NeedsAnswer;
        if (string.Equals(line, "busy", StringComparison.Ordinal))
            return Busy;

        return "";
    }
}
