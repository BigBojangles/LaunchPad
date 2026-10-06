namespace LaunchPad.Views;

public sealed class OnboardingProjectStatus
{
    public enum Kind
    {
        Ready,
        Exists,
        Failed
    }

    private OnboardingProjectStatus(Kind result, string message, string? path)
    {
        Result = result;
        Message = message;
        Path = path;
    }

    public Kind Result { get; }
    public string Message { get; }
    public string? Path { get; }

    public static OnboardingProjectStatus Ok(string path) => new(Kind.Ready, "", path);

    public static OnboardingProjectStatus AlreadyExists(string path, string message) =>
        new(Kind.Exists, message, path);

    public static OnboardingProjectStatus Fail(string message) => new(Kind.Failed, message, null);
}
