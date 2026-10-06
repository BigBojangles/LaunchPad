using Avalonia.Media;

namespace LaunchPad.Services;

/// <summary>Native desktop actions consumed by the shared Avalonia views.</summary>
public interface IDesktopIntegration
{
    void OpenProjectFolder(string path);
    IImage? ReadProgramIcon(string? executable);
}

public sealed class WindowsDesktopIntegration : IDesktopIntegration
{
    public void OpenProjectFolder(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows desktop adapter requires Windows.");
        ProjectFolders.OpenInExplorer(path);
    }

    public IImage? ReadProgramIcon(string? executable) => WindowsProductIcon.FromExe(executable);
}
