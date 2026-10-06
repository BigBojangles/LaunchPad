using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsRuntimeSettingsTests
{
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothDesktopLaunchRoutesReadTheirOwnSettingsBeforePreparingAnyVm(bool directOpen)
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-runtime-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "settings"));
            var settings = new SettingsStore(paths);
            var log = new SetupLog(paths);
            var locator = new GrokLocator(paths);
            var runtime = new WindowsProjectRuntime(new GrokSetup(paths, locator, log),
                new ProjectLauncher(locator, log), log, paths, settings);
            var project = Path.Combine(root, "owned-project");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(paths.SettingsFile, "null");
            var error = await Assert.ThrowsAsync<IOException>(() => directOpen
                ? runtime.OpenFencedAsync(project, CancellationToken.None, null)
                : runtime.CreateFencedSession().StartAsync(project, null, null, CancellationToken.None));
            Assert.Contains("settings.json", error.Message);
            Assert.Equal("null", await File.ReadAllTextAsync(paths.SettingsFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(project));
            Assert.False(File.Exists(paths.ProjectsFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Unverified: requires the Windows runtime adapter.";
        }
    }
}
