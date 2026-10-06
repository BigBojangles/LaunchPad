using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class SessionPathsTests
{
    [Fact]
    public void DecodesUrlEncodedWindowsPath()
    {
        var encoded = "C%3A%5CUsers%5CBig%20Bojangles%5Cprojects%5Clauncher%20build";
        Assert.True(SessionPaths.TryDecodeFolderName(encoded, out var path));
        Assert.Equal(@"C:\Users\Big Bojangles\projects\launcher build", path);
    }

    [Fact]
    public void RejectsNonPathFolderNames()
    {
        Assert.False(SessionPaths.TryDecodeFolderName("session_search", out _));
        Assert.False(SessionPaths.TryDecodeFolderName("", out _));
    }

    [Fact]
    public void ReadsCwdFileWhenPresent()
    {
        var temp = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp, ".cwd"), @"D:\Games\MyTitle" + Environment.NewLine);
            Assert.True(SessionPaths.TryResolveSessionGroupPath(temp, out var path));
            Assert.Equal(@"D:\Games\MyTitle", path);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ExcludesHomeAndGrokHome()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "User");
        var grok = Path.Combine(home, ".grok");
        Directory.CreateDirectory(grok);
        try
        {
            var paths = new AppPaths(userProfile: home, grokHome: grok, grokBin: Path.Combine(grok, "bin"), appDataDir: Path.Combine(root, "app"));
            Assert.True(SessionPaths.IsExcludedProjectPath(home, paths));
            Assert.True(SessionPaths.IsExcludedProjectPath(Path.Combine(grok, "sessions"), paths));
            Assert.False(SessionPaths.IsExcludedProjectPath(Path.Combine(home, "Grok", "Projects", "Demo"), paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
