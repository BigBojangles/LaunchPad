using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public static class ProjectRow
{
    public static void AddFolder(SettingsStore settings, string name, string path) =>
        settings.RememberProject(name, path);

    public static Task StartAsync(FenceSession session, string path, CancellationToken cancellationToken) =>
        session.StartAsync(path, null, null, cancellationToken);

    public static Task OpenAsync(FenceSession session, string path, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        session.OpenAsync(path, cancellationToken, progress);
}
