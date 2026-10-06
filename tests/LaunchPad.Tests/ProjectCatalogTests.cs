using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class ProjectCatalogTests
{
    [Fact]
    public void ListsOnlyFoldersTheLauncherCreatedOrTheUserPicked()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "User");
        var grokHome = Path.Combine(home, ".grok");
        var projectsRoot = Path.Combine(home, "Grok", "Projects");
        var appData = Path.Combine(root, "AppData");
        var created = Path.Combine(projectsRoot, "Created");
        var picked = Path.Combine(root, "Other", "FromBrowse");
        var plain = Path.Combine(Path.GetTempPath(), "bl-plain-" + Guid.NewGuid().ToString("N"));
        var prompt = Path.Combine(Path.GetTempPath(), "grok-prompt-" + Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(grokHome, "sessions", "not-a-project");
        var child = Path.Combine(created, "nested");
        var unscanned = Path.Combine(projectsRoot, "FromRoot");

        Directory.CreateDirectory(created);
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(unscanned);
        Directory.CreateDirectory(picked);
        Directory.CreateDirectory(plain);
        Directory.CreateDirectory(prompt);
        Directory.CreateDirectory(sessionFolder);
        Directory.CreateDirectory(EncodeSession(grokHome, plain));
        Directory.CreateDirectory(EncodeSession(grokHome, prompt));
        Directory.CreateDirectory(EncodeSession(grokHome, sessionFolder));

        try
        {
            var paths = new AppPaths(
                userProfile: home,
                grokHome: grokHome,
                grokBin: Path.Combine(grokHome, "bin"),
                appDataDir: appData);
            var settings = new SettingsStore(paths);
            settings.RememberProject("Created", created);
            settings.RememberProject("FromBrowse", picked);

            var listed = new ProjectCatalog(paths, settings).ListProjects();

            Assert.Contains(listed, p => Same(p.Path, created));
            Assert.Contains(listed, p => Same(p.Path, picked));
            Assert.DoesNotContain(listed, p => Same(p.Path, plain));
            Assert.DoesNotContain(listed, p => Same(p.Path, prompt));
            Assert.DoesNotContain(listed, p => Same(p.Path, sessionFolder));
            Assert.DoesNotContain(listed, p => Same(p.Path, child));
            Assert.DoesNotContain(listed, p => Same(p.Path, unscanned));
            Assert.DoesNotContain(listed, p => Same(p.Path, home));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(plain, recursive: true);
            Directory.Delete(prompt, recursive: true);
        }
    }

    [Fact]
    public void KeepsAPickedFolderThatIsMissingRightNow()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "User");
        var gone = Path.Combine(root, "Gone");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(gone);

        try
        {
            var paths = new AppPaths(
                userProfile: home,
                grokHome: Path.Combine(home, ".grok"),
                grokBin: Path.Combine(home, ".grok", "bin"),
                appDataDir: Path.Combine(root, "AppData"));
            var settings = new SettingsStore(paths);
            settings.RememberProject("Gone", gone);
            Directory.Delete(gone);

            var listed = new ProjectCatalog(paths, settings).ListProjects();

            Assert.Empty(listed);
            Assert.Contains(settings.KnownProjects, p => Same(p.Path, gone));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string EncodeSession(string grokHome, string projectPath)
    {
        var encoded = projectPath.Replace("\\", "%5C").Replace(":", "%3A").Replace(" ", "%20");
        return Path.Combine(grokHome, "sessions", encoded);
    }

    private static bool Same(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}
