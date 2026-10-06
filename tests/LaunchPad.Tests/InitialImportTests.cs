using System.Security.Cryptography;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class InitialImportTests
{
    [Fact]
    public async Task ReopenSelectsTheAgentWithoutHostHomeOrRepositoryReplay()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "edit.txt"), "new");
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        File.WriteAllText(Path.Combine(fixture.Root, ".git", "config"), "host metadata");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "nested", ".GIT"));
        File.WriteAllText(Path.Combine(fixture.Root, "nested", ".GIT", "config"), "host metadata");
        using var stream = new MemoryStream();
        await FenceHost.SendProjectAsync(stream, fixture.Root, null, null, CancellationToken.None,
            new AgentLaunch(AgentChoice.Custom, "owned-program", null), restoreGuestState: true,
            restoreHostHome: false, preserveRepositoryMetadata: true);
        Assert.Equal("AGENT custom\nAGENT-CMD owned-program\nFILE 3 edit.txt\nnew", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void StartupJournalPreservesThePreviousBaselineAndHistoricalLogUntilCommit()
    {
        using var fixture = new Fixture();
        var baseline = new SentManifest();
        baseline.Note("a.txt", 3, 1, Hash("old"));
        baseline.Save(Path.Combine(fixture.Root, "sent.manifest"));
        File.WriteAllText(Path.Combine(fixture.Root, "serial.log"), "DOOR-READY safe-import safe-merge\n");
        InitialImport.Begin(fixture.Root);
        Assert.True(SessionGuardian.NeedsRecovery(fixture.Root));
        Assert.Equal(Hash("old"), SentManifest.Load(Path.Combine(fixture.Root, "sent.manifest")).ContentHash("a.txt"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "serial.log")));
        Assert.Contains("DOOR-READY", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Root, "serial-*.log"))));
        var updated = new SentManifest();
        updated.Note("a.txt", 3, 2, Hash("new"));
        InitialImport.Commit(fixture.Root, updated);
        Assert.False(SessionGuardian.NeedsRecovery(fixture.Root));
        Assert.Equal(Hash("new"), SentManifest.Load(Path.Combine(fixture.Root, "sent.manifest")).ContentHash("a.txt"));
    }

    [Fact]
    public async Task PartialReadinessDoesNotCommitAndGuestFailureKeepsTheJournal()
    {
        using var fixture = new Fixture();
        InitialImport.Begin(fixture.Root);
        var serial = Path.Combine(fixture.Root, "serial.log");
        File.WriteAllText(serial, "IMPORT-READY safe-merge");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = InitialImport.WaitForMarkerAsync(serial, "IMPORT-READY safe-merge", timeout.Token);
        await Task.Delay(150, timeout.Token);
        Assert.False(waiting.IsCompleted);
        File.AppendAllText(serial, "\n");
        await waiting;
        File.AppendAllText(serial, "IMPORT-FAILED\n");
        await Assert.ThrowsAsync<IOException>(() => InitialImport.WaitForMarkerAsync(serial, "DOOR-READY safe-import safe-merge", timeout.Token));
        Assert.True(SessionGuardian.NeedsRecovery(fixture.Root));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    [Theory]
    [InlineData("AGENT-TERMINAL-FAILED", "terminal")]
    [InlineData("AGENT-POLICY-FAILED", "policy")]
    public async Task AgentSafetyFailureCannotCommitEvenIfReadinessFollows(string marker, string message)
    {
        using var fixture = new Fixture();
        var previous = new SentManifest();
        previous.Note("saved.txt", 3, 1, Hash("old"));
        previous.Save(Path.Combine(fixture.Root, "sent.manifest"));
        InitialImport.Begin(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "serial.log"), marker + "\nDOOR-READY safe-import safe-merge\n");
        var error = await Assert.ThrowsAsync<IOException>(() => InitialImport.WaitForMarkerAsync(
            Path.Combine(fixture.Root, "serial.log"), "DOOR-READY safe-import safe-merge", CancellationToken.None));
        Assert.Contains(message, error.Message);
        Assert.True(SessionGuardian.NeedsRecovery(fixture.Root));
        Assert.Equal(Hash("old"), SentManifest.Load(Path.Combine(fixture.Root, "sent.manifest")).ContentHash("saved.txt"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
