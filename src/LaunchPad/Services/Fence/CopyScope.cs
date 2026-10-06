namespace LaunchPad.Services.Fence;

public static class CopyScope
{
    public static readonly AsyncLocal<bool> SendAll = new();
}
