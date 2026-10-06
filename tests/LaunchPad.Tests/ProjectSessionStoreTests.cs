using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class ProjectSessionStoreTests
{
    [Fact]
    public void AVerifiedChildBecomesCurrentWithoutReplacingOriginalOrItsJournals()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Home, "resend.failed"), "keep this journal");
        var upgraded = fixture.Child();
        using var lease = ProjectSessionStore.Acquire(fixture.Home);
        ProjectSessionStore.Activate(fixture.Home, upgraded);
        Assert.Equal(upgraded.Directory, ProjectSessionStore.Current(fixture.Home));
        Assert.Equal(new[] { upgraded.Directory, fixture.Home }, ProjectSessionStore.History(fixture.Home));
        Assert.Equal("saved original", File.ReadAllText(fixture.Original));
        Assert.Equal("keep this journal", File.ReadAllText(Path.Combine(fixture.Home, "resend.failed")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AnUnverifiedOrChangedOriginalCannotSwitchTheProject(bool verified, bool changeOriginal)
    {
        using var fixture = new Fixture();
        var upgraded = fixture.Child(verified);
        if (changeOriginal) File.WriteAllText(fixture.Original, "changed original");
        Assert.Throws<InvalidDataException>(() => ProjectSessionStore.Activate(fixture.Home, upgraded));
        Assert.Equal(fixture.Home, ProjectSessionStore.Current(fixture.Home));
        Assert.True(File.Exists(upgraded.Disk));
    }

    [Fact]
    public void AChildOfAnOlderGenerationCannotReplaceTheCurrentGeneration()
    {
        using var fixture = new Fixture();
        var first = fixture.Child();
        var other = fixture.Child();
        ProjectSessionStore.Activate(fixture.Home, first);
        Assert.Throws<InvalidDataException>(() => ProjectSessionStore.Activate(fixture.Home, other));
        Assert.Equal(first.Directory, ProjectSessionStore.Current(fixture.Home));
        Assert.True(File.Exists(other.Disk));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("unreviewed")]
    public void InvalidSelectionsNeverFallBackToAnOlderDisk(string path)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Home, "active-session.json"), JsonSerializer.Serialize(new ActiveSession(1, path, "runtime-test")));
        Assert.Throws<InvalidDataException>(() => ProjectSessionStore.Current(fixture.Home));
        var report = RecoveryCatalog.Read(fixture.Project, fixture.Home);
        Assert.True(report.RestartBlocked);
        Assert.False(report.CanResume);
        Assert.Equal("saved original", File.ReadAllText(fixture.Original));
    }

    [Fact]
    public void APreparationLeaseBlocksASecondLaunchAndIsObservableByCleanup()
    {
        using var fixture = new Fixture();
        using (ProjectSessionStore.Acquire(fixture.Home))
        {
            Assert.True(ProjectSessionStore.IsPreparing(fixture.Home));
            Assert.Throws<IOException>(() => ProjectSessionStore.Acquire(fixture.Home));
        }
        Assert.False(ProjectSessionStore.IsPreparing(fixture.Home));
        using var next = ProjectSessionStore.Acquire(fixture.Home);
    }

    [Fact]
    public void HistoricalReturnsRemainReviewableAndProtectedImportsRemainProtected()
    {
        using var fixture = new Fixture();
        var old = fixture.Return(fixture.Home);
        var upgraded = fixture.Child();
        ProjectSessionStore.Activate(fixture.Home, upgraded);
        File.WriteAllText(Path.Combine(upgraded.Directory, ProjectSessionStore.UnconfirmedImport), "an earlier import was unconfirmed");
        var newer = fixture.Return(upgraded.Directory);
        var report = RecoveryCatalog.Read(fixture.Project, fixture.Home);
        Assert.True(report.RestartBlocked);
        Assert.True(report.CanResume);
        Assert.Equal(2, report.Returns.Count);
        Assert.True(report.Returns.Single(row => row.Directory == old.DirectoryPath).CanRetry);
        Assert.False(report.Returns.Single(row => row.Directory == newer.DirectoryPath).CanRetry);
        Assert.Equal(old.DirectoryPath, RecoveryCatalog.OpenForProject(fixture.Project, fixture.Home, old.DirectoryPath).DirectoryPath);
        Assert.Throws<InvalidDataException>(() => RecoveryCatalog.OpenForProject(fixture.Project, fixture.Home, newer.DirectoryPath));
        Assert.False(SessionGuardian.NeedsRecovery(upgraded.Directory, includePreserved: false));
    }

    [Fact]
    public async Task ResumeSelectionSendsNoProjectOrHostHomeEvenWithLocalChanges()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Project, "a.txt"), "new host value");
        var manifestPath = Path.Combine(fixture.Home, "sent.manifest");
        var manifest = new SentManifest();
        manifest.Note("a.txt", 3, 0, Hash("old"));
        manifest.Save(manifestPath);
        var originalManifest = File.ReadAllBytes(manifestPath);
        using var channel = new MemoryStream();
        var returned = await FenceHost.SendProjectAsync(channel, fixture.Project, null, manifestPath, CancellationToken.None,
            AgentLaunch.Grok, restoreGuestState: true, restoreHostHome: false, sendProjectFiles: false);
        Assert.Equal("AGENT grok\n", Encoding.UTF8.GetString(channel.ToArray()));
        Assert.Equal(Hash("old"), returned.ContentHash("a.txt"));
        Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal("new host value", File.ReadAllText(Path.Combine(fixture.Project, "a.txt")));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPad-selection-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(Root, "sessions", "owned-project");
        public string Project => Path.Combine(Root, "project");
        public string Original => Path.Combine(Home, "session.qcow2");
        public Fixture() { Directory.CreateDirectory(Home); Directory.CreateDirectory(Project); File.WriteAllText(Original, "saved original"); }
        public UpgradedSession Child(bool verified = true)
        {
            var directory = Path.Combine(Home, "upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var disk = Path.Combine(directory, "session.qcow2");
            File.WriteAllText(disk, "upgraded child fixture");
            File.WriteAllText(Path.Combine(directory, "sent.manifest"), "");
            var hash = Hash("saved original");
            File.WriteAllText(Path.Combine(directory, "upgrade-result.json"), JsonSerializer.Serialize(new { verified, original = Original, originalSha256 = hash, version = "runtime-test" }));
            return new(directory, disk, Original, hash, "runtime-test");
        }
        public ReturnRecovery Return(string directory)
        {
            var recovery = ReturnRecovery.Create(directory);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n"));
            var receipt = ProjectPull.Receive(input, recovery.Payload, TimeSpan.FromSeconds(5));
            Assert.True(receipt.Complete, receipt.Error);
            recovery.SaveTransfer(Project, receipt);
            return recovery;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
