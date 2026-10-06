using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class SettingsFailureTests
{
    [Theory]
    [InlineData("settings.json", "{")]
    [InlineData("settings.json", "null")]
    [InlineData("projects.json", "[null]")]
    [InlineData("projects.json", "[")]
    public void InvalidSavedRecordsArePreservedInsteadOfBecomingEmptyDefaults(string file, string body)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Paths.AppDataDir, file);
        File.WriteAllText(path, body);
        var error = Assert.Throws<IOException>(() => new SettingsStore(fixture.Paths));
        Assert.Contains(file, error.Message);
        Assert.Equal(body, File.ReadAllText(path));
        Assert.Equal("original project", File.ReadAllText(fixture.WorkFile));
        Assert.Empty(Directory.GetFiles(fixture.Paths.AppDataDir, "*.tmp"));
    }

    [Fact]
    public void AnUnreadableSavedFileIsNotTreatedAsANewInstallation()
    {
        using var fixture = new Fixture();
        var settings = new SettingsStore(fixture.Paths);
        settings.SavePreferences(false, AgentChoice.Claude, 4096, 2);
        var bytes = File.ReadAllBytes(fixture.Paths.SettingsFile);
        using (var held = new FileStream(fixture.Paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => new SettingsStore(fixture.Paths));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Paths.SettingsFile));
        Assert.False(new SettingsStore(fixture.Paths).Current.ShowTips);
    }

    [Fact]
    public async Task UnreadableSettingsStopVMStartBeforeAnySessionDiskOrDefaultAgentIsPrepared()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.SettingsFile, "{bad saved settings");
        var sessions = Path.Combine(fixture.Paths.UserProfile, "sessions");
        var started = false;
        var session = new FenceSession(new SetupLog(fixture.Paths), launchAccountReady: () => true,
            keptImage: () => Path.Combine(fixture.Paths.UserProfile, "debian-12-builder.qcow2"), sessionsRoot: () => sessions,
            starter: _ => { started = true; return true; }, readSettings: () => new SettingsStore(fixture.Paths));
        await Assert.ThrowsAsync<IOException>(() => session.StartAsync(fixture.Project, null, null, CancellationToken.None));
        Assert.False(started);
        Assert.False(Directory.Exists(sessions));
        Assert.Equal("{bad saved settings", File.ReadAllText(fixture.Paths.SettingsFile));
        Assert.Equal("original project", File.ReadAllText(fixture.WorkFile));
    }

    [Fact]
    public void AStaleInstanceCannotDropAnotherInstancesProjectsPreferencesOrTips()
    {
        using var fixture = new Fixture();
        var first = new SettingsStore(fixture.Paths);
        var stale = new SettingsStore(fixture.Paths);
        first.SavePreferences(false, AgentChoice.Claude, 6144, 3);
        first.SaveDisplayName(fixture.Project, "Saved first");
        var preferences = File.ReadAllBytes(fixture.Paths.SettingsFile);
        var projects = File.ReadAllBytes(fixture.Paths.ProjectsFile);
        Assert.Throws<IOException>(() => stale.SavePreferences(false, AgentChoice.Codex, 4096, 2));
        Assert.Equal(AgentChoice.Grok, stale.Current.DefaultAgent);
        Assert.True(stale.Current.ShowTips);
        Assert.Throws<IOException>(() => stale.TakeTip("project-menu"));
        Assert.Empty(stale.Current.SeenTips);
        Assert.Throws<IOException>(() => stale.SaveAgent(fixture.Project, AgentChoice.Custom, "cat", "fixture.sh"));
        Assert.Empty(stale.Current.ProjectAgent);
        Assert.Empty(stale.Current.ProjectAgentProgram);
        Assert.Empty(stale.Current.ProjectAgentSource);
        Assert.Throws<IOException>(() => stale.SaveBackupRemote(fixture.Project, "owned-placeholder"));
        Assert.Empty(stale.Current.BackupRemotes);
        Assert.Throws<IOException>(() => stale.SaveProjectMemory(fixture.Project, 2048));
        Assert.Empty(stale.Current.ProjectMemoryMb);
        Assert.Throws<IOException>(() => stale.MarkOpened(fixture.Project));
        Assert.Empty(stale.Current.LastOpened);
        Assert.Throws<IOException>(() => stale.RememberProject("Unsaved", fixture.Project));
        Assert.Throws<IOException>(() => stale.SaveDisplayName(fixture.Project, "Unsaved"));
        Assert.Empty(stale.KnownProjects);
        Assert.Equal(preferences, File.ReadAllBytes(fixture.Paths.SettingsFile));
        Assert.Equal(projects, File.ReadAllBytes(fixture.Paths.ProjectsFile));
        Assert.Equal("Saved first", new SettingsStore(fixture.Paths).DisplayNameFor(fixture.Project));
    }

    [EnvironmentFact("OS", "Windows_NT")]
    [Trait("Platform", "Windows")]
    public void WindowsFailedAtomicReplacementKeepsDiskAndMemoryIncludingTheTipsReset()
    {
        using var fixture = new Fixture();
        var store = new SettingsStore(fixture.Paths);
        store.SavePreferences(true, AgentChoice.Grok, 4096, 2);
        Assert.True(store.TakeTip("project-menu"));
        var bytes = File.ReadAllBytes(fixture.Paths.SettingsFile);
        using (var held = new FileStream(fixture.Paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => store.SavePreferences(false, AgentChoice.Claude, 6144, 3, resetTips: true));
            Assert.True(failure is IOException or UnauthorizedAccessException, "A denied atomic replacement must fail explicitly.");
            Assert.Equal(bytes, File.ReadAllBytes(fixture.Paths.SettingsFile));
            Assert.True(store.Current.ShowTips);
            Assert.Contains("project-menu", store.Current.SeenTips);
            Assert.Equal(AgentChoice.Grok, store.Current.DefaultAgent);
            Assert.Equal(4096, store.Current.MachineMemoryMb);
            Assert.Equal(2, store.Current.MachineCores);
        }
        Assert.Empty(Directory.GetFiles(fixture.Paths.AppDataDir, "*.tmp"));
        store.SavePreferences(false, AgentChoice.Claude, 6144, 3, resetTips: true);
        var loaded = new SettingsStore(fixture.Paths);
        Assert.Equal(AgentChoice.Claude, loaded.Current.DefaultAgent);
        Assert.Empty(loaded.Current.SeenTips);
    }

    [Fact]
    public async Task ConcurrentInstancesHaveOneCompleteSaveAndOneExplicitConflict()
    {
        using var fixture = new Fixture();
        var a = new SettingsStore(fixture.Paths);
        var b = new SettingsStore(fixture.Paths);
        static bool Save(SettingsStore store, string agent)
        { try { store.SavePreferences(false, agent, 4096, 2); return true; } catch (IOException) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Save(a, AgentChoice.Codex)), Task.Run(() => Save(b, AgentChoice.Claude)));
        Assert.Single(results, result => result);
        var loaded = new SettingsStore(fixture.Paths);
        Assert.Equal(results[0] ? AgentChoice.Codex : AgentChoice.Claude, loaded.Current.DefaultAgent);
        Assert.False(loaded.Current.ShowTips);
        Assert.Equal(4096, loaded.Current.MachineMemoryMb);
        Assert.Empty(Directory.GetFiles(fixture.Paths.AppDataDir, "*.tmp"));
    }

    [Fact]
    public void OlderAndNewerRecordFieldsSurviveAnOrdinarySettingsOrNameSave()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Paths.SettingsFile, "{\"hideWelcome\":true,\"futureOption\":{\"enabled\":true,\"count\":7}}", System.Text.Encoding.Unicode);
        File.WriteAllText(fixture.Paths.ProjectsFile, JsonSerializer.Serialize(new[] { new { name = "Old record", path = fixture.Project,
            futureProjectOption = new[] { "one", "two" } } }));
        var settings = new SettingsStore(fixture.Paths);
        settings.SavePreferences(false, AgentChoice.Codex, 4096, 2);
        settings.SaveDisplayName(fixture.Project, "New display label");
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.Paths.SettingsFile));
        using var projects = JsonDocument.Parse(File.ReadAllText(fixture.Paths.ProjectsFile));
        Assert.True(saved.RootElement.GetProperty("hideWelcome").GetBoolean());
        Assert.Equal(7, saved.RootElement.GetProperty("futureOption").GetProperty("count").GetInt32());
        Assert.Equal(new[] { "one", "two" }, projects.RootElement[0].GetProperty("futureProjectOption").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Old record", projects.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("New display label", new SettingsStore(fixture.Paths).DisplayNameFor(fixture.Project));
        Assert.Equal("original project", File.ReadAllText(fixture.WorkFile));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LaunchPad-settings-fail-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(_root, "real-folder");
        public string WorkFile => Path.Combine(Project, "work.txt");
        public AppPaths Paths { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Project);
            File.WriteAllText(WorkFile, "original project");
            Paths = new AppPaths(userProfile: _root, appDataDir: Path.Combine(_root, "settings"));
            Directory.CreateDirectory(Paths.AppDataDir);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
