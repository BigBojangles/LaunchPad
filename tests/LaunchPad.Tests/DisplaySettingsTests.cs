using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class DisplaySettingsTests
{
    [Fact]
    public void ThemeAndBoardPlacementSurviveRestartWithoutChangingProjectChoicesOrFiles()
    {
        using var fixture = new Fixture();
        fixture.Settings.RememberProject("real-folder", fixture.Project);
        fixture.Settings.SaveAgent(fixture.Project, AgentChoice.Codex, null, null);
        fixture.Settings.SavePreferences(true, AgentChoice.Grok, 4096, 2, theme: Appearance.Dark);
        fixture.Settings.SaveBoardGroup("vm:owned-session", Path.Combine(fixture.Root, "display-target"));
        var reloaded = fixture.Reload();
        Assert.Equal(Appearance.Dark, reloaded.Current.Theme);
        Assert.Equal(Path.Combine(fixture.Root, "display-target"), reloaded.Current.SessionBoardGroups["vm:owned-session"]);
        Assert.Equal(AgentChoice.Codex, reloaded.AgentFor(fixture.Project).Id);
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(fixture.Project, "work.txt")));
    }

    [Fact]
    public void OldSettingsDefaultToWindowsThemeAndInvalidThemeDoesNotReplacePreferences()
    {
        using var fixture = new Fixture();
        Assert.Equal(Appearance.System, fixture.Reload().Current.Theme);
        Assert.Throws<ArgumentException>(() => fixture.Settings.SavePreferences(false, AgentChoice.Claude, 8192, 4, theme: "invalid"));
        Assert.Equal(Appearance.System, fixture.Reload().Current.Theme);
        Assert.True(fixture.Reload().Current.ShowTips);
    }

    [Fact]
    public void ProjectAndSessionAliasesPersistWithoutChangingFolderOrIdentity()
    {
        using var fixture = new Fixture();
        var store = fixture.Settings;
        store.RememberProject("real-folder", fixture.Project);
        var vm = "vm:" + QemuLayout.ProjectKey(fixture.Project);
        var host = "host:" + QemuLayout.ProjectKey(fixture.Project);
        store.SaveDisplayName(fixture.Project, "A display / label with \"quotes\" & punctuation");
        store.SaveDisplayName(fixture.Project, "Coding session", vm);
        var restored = fixture.Reload();
        Assert.Equal("Coding session", restored.DisplayNameFor(fixture.Project, vm));
        Assert.Equal("A display / label with \"quotes\" & punctuation", restored.DisplayNameFor(fixture.Project, host));
        Assert.Equal(fixture.Project, Assert.Single(restored.KnownProjects).Path);
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(fixture.Project, "work.txt")));
        Assert.Equal("A display / label with \"quotes\" & punctuation", Assert.Single(new ProjectCatalog(fixture.Paths, restored).ListProjects()).Name);
        Assert.Equal(vm, "vm:" + QemuLayout.ProjectKey(fixture.Project));
    }

    [Fact]
    public void EmptyAndResetNamesUseTheFolderWhileDistinctSessionNamesRemainSeparate()
    {
        using var fixture = new Fixture();
        fixture.Settings.SaveDisplayName(fixture.Project, "Project alias");
        fixture.Settings.SaveDisplayName(fixture.Project, "Session alias", "vm:test");
        fixture.Settings.SaveDisplayName(fixture.Project, "  ", "vm:test");
        Assert.Equal("real-folder", fixture.Settings.DisplayNameFor(fixture.Project, "vm:test"));
        Assert.Equal("Project alias", fixture.Settings.DisplayNameFor(fixture.Project));
        fixture.Settings.ResetDisplayName(fixture.Project);
        Assert.Equal("real-folder", fixture.Reload().DisplayNameFor(fixture.Project));
    }

    [Fact]
    public void InvalidDisplayInputCannotReplaceThePreviouslySavedName()
    {
        using var fixture = new Fixture();
        fixture.Settings.SaveDisplayName(fixture.Project, "Kept name");
        Assert.Throws<ArgumentException>(() => fixture.Settings.SaveDisplayName(fixture.Project, "new\nname"));
        Assert.Equal("Kept name", fixture.Reload().DisplayNameFor(fixture.Project));
    }

    [Fact]
    public void AppDefaultsPreserveExplicitProjectChoicesAndExistingRecords()
    {
        using var fixture = new Fixture();
        fixture.Settings.RememberProject("real-folder", fixture.Project);
        fixture.Settings.SaveAgent(fixture.Project, AgentChoice.Codex, null, null);
        fixture.Settings.SaveProjectMemory(fixture.Project, 2048);
        fixture.Settings.SaveBackupRemote(fixture.Project, "owned-placeholder");
        fixture.Settings.SavePreferences(false, AgentChoice.Claude, 4096, 3);
        var loaded = fixture.Reload();
        Assert.False(loaded.Current.ShowTips);
        Assert.Equal(AgentChoice.Claude, loaded.AgentFor(Path.Combine(fixture.Root, "new-project")).Id);
        Assert.Equal(AgentChoice.Codex, loaded.AgentFor(fixture.Project).Id);
        Assert.Equal(2048, loaded.ProjectMemoryMbFor(fixture.Project));
        Assert.Equal("owned-placeholder", loaded.BackupRemote(fixture.Project));
        Assert.Equal(4096, loaded.Current.MachineMemoryMb);
        Assert.Equal(3, loaded.Current.MachineCores);
        Assert.Single(loaded.KnownProjects);
    }

    [Fact]
    public void TipsAreOnceOnlyAcrossRestartAndRespectSwitchAndReset()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Settings.TakeTip("project-menu"));
        Assert.False(fixture.Reload().TakeTip("project-menu"));
        fixture.Settings.SavePreferences(false, AgentChoice.Grok, 4096, 2);
        Assert.False(fixture.Settings.TakeTip("another-tip"));
        fixture.Settings.ResetTips();
        Assert.False(fixture.Settings.TakeTip("project-menu"));
        fixture.Settings.SavePreferences(true, AgentChoice.Grok, 4096, 2);
        Assert.True(fixture.Settings.TakeTip("project-menu"));
        Assert.False(fixture.Reload().TakeTip("project-menu"));
        Assert.True(fixture.Reload().TakeTip("another-tip"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPad-display-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "real-folder");
        public AppPaths Paths { get; }
        public SettingsStore Settings { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Project);
            File.WriteAllText(Path.Combine(Project, "work.txt"), "keep original");
            Paths = new AppPaths(userProfile: Root, appDataDir: Path.Combine(Root, "settings"));
            Settings = new SettingsStore(Paths);
        }
        public SettingsStore Reload() => new(Paths);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
