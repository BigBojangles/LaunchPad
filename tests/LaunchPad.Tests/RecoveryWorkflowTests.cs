using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class RecoveryWorkflowTests
{
    [Fact]
    public async Task SafeWindowsReturnDoesNotRequireGitSetupAndRetainsLocalOriginal()
    {
        using var fixture = new Fixture(useDefaultBackup: true);
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n");
        await fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath);
        Assert.Equal("new", fixture.ReadHost());
        Assert.Equal("old", File.ReadAllText(Path.Combine(recovery.DirectoryPath, "previous", "a.txt")));
        Assert.Equal("saved VM fixture", File.ReadAllText(fixture.Disk));
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public async Task ACompleteReturnCanBeRetriedWithoutChangingItsReceiptOrSavedVm()
    {
        using var fixture = new Fixture();
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n");
        var originalReceipt = File.ReadAllBytes(Path.Combine(recovery.DirectoryPath, "transfer.json"));
        var originalDisk = File.ReadAllBytes(fixture.Disk);
        var report = fixture.Session.ReadRecovery(fixture.Project);
        Assert.True(Assert.Single(report.Returns).CanRetry);
        await fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath);
        Assert.Equal("new", fixture.ReadHost());
        Assert.Equal("old", File.ReadAllText(Path.Combine(recovery.DirectoryPath, "previous", "a.txt")));
        Assert.Equal(originalReceipt, File.ReadAllBytes(Path.Combine(recovery.DirectoryPath, "transfer.json")));
        Assert.Equal(originalDisk, File.ReadAllBytes(fixture.Disk));
        Assert.False(Assert.Single(fixture.Session.ReadRecovery(fixture.Project).Returns).CanRetry);
        Assert.Equal(1, fixture.BackupCalls);
    }

    [Fact]
    public async Task AnUnavailableScannerCanBeRetriedAfterItBecomesAvailable()
    {
        using var fixture = new Fixture();
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n");
        fixture.Scanner.Verdict = FileScanVerdict.Unavailable;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath));
        Assert.Equal("old", fixture.ReadHost());
        Assert.Equal(0, fixture.BackupCalls);
        Assert.True(Assert.Single(fixture.Session.ReadRecovery(fixture.Project).Returns).CanRetry);
        fixture.Scanner.Verdict = FileScanVerdict.Allowed;
        await fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath);
        Assert.Equal("new", fixture.ReadHost());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("n")]
    public async Task ChangedOrTruncatedRecoveryContentCannotOverwriteWindowsFiles(string changed)
    {
        using var fixture = new Fixture();
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n");
        File.WriteAllText(Path.Combine(recovery.Payload, "a.txt"), changed);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath));
        Assert.Contains("changed or was truncated", error.Message);
        Assert.Equal("old", fixture.ReadHost());
        Assert.Equal(0, fixture.BackupCalls);
        Assert.Equal(0, fixture.Scanner.Calls);
        Assert.Equal(changed, File.ReadAllText(Path.Combine(recovery.Payload, "a.txt")));
    }

    [Fact]
    public async Task AWindowsConflictKeepsBothVersionsAndAllOlderCopies()
    {
        using var fixture = new Fixture();
        var older = fixture.Receive("PROJECT 0\nPROJECT-END\n");
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n");
        File.WriteAllText(Path.Combine(fixture.Project, "a.txt"), "edit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath));
        Assert.Equal("edit", fixture.ReadHost());
        Assert.Equal("new", File.ReadAllText(Path.Combine(recovery.Payload, "a.txt")));
        Assert.True(Directory.Exists(older.DirectoryPath));
        Assert.Equal(2, fixture.Session.ReadRecovery(fixture.Project).Returns.Count);
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public async Task APartialTransferCanBeReviewedButCannotBeRetried()
    {
        using var fixture = new Fixture();
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nn");
        var row = Assert.Single(fixture.Session.ReadRecovery(fixture.Project).Returns);
        Assert.True(row.CanReview);
        Assert.False(row.CanRetry);
        Assert.Contains("Incomplete transfer", row.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath));
        Assert.Equal("old", fixture.ReadHost());
        Assert.Equal("n", File.ReadAllText(Path.Combine(recovery.Payload, "a.txt")));
        Assert.Equal(0, fixture.Scanner.Calls);
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("{\"Files\":[\"a.txt\"],\"Complete\":true}")]
    public async Task ACorruptOrOlderReceiptDoesNotAuthorizeHostWrites(string json)
    {
        using var fixture = new Fixture();
        var recovery = ReturnRecovery.Create(fixture.SessionDirectory);
        File.WriteAllText(Path.Combine(recovery.Payload, "a.txt"), "new");
        File.WriteAllText(Path.Combine(recovery.DirectoryPath, "transfer.json"), json);
        var row = Assert.Single(fixture.Session.ReadRecovery(fixture.Project).Returns);
        Assert.False(row.CanRetry);
        Assert.True(row.CanReview);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Session.CopyBackToProjectAsync(fixture.Project, recovery.DirectoryPath));
        Assert.Equal("old", fixture.ReadHost());
        Assert.Equal(0, fixture.BackupCalls);
        Assert.Equal(0, fixture.Scanner.Calls);
    }

    [Fact]
    public async Task ARecoveryCopyCannotBeAppliedToAnotherProject()
    {
        using var fixture = new Fixture();
        var recovery = fixture.Receive("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n");
        var otherProject = Path.Combine(fixture.Root, "other-project");
        Directory.CreateDirectory(otherProject);
        File.WriteAllText(Path.Combine(otherProject, "a.txt"), "other");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Session.CopyBackToProjectAsync(otherProject, recovery.DirectoryPath));
        Assert.Equal("other", File.ReadAllText(Path.Combine(otherProject, "a.txt")));
        Assert.Equal("old", fixture.ReadHost());
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public void RestartProtectionAndOlderWaitingCopiesRemainVisibleWithoutBootingTheVm()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.SessionDirectory, "resend.failed"), "Unconfirmed import");
        var legacy = Path.Combine(fixture.Aside, QemuLayout.ProjectKey(fixture.Project), "old-copy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "a.txt"), "old recovered content");
        File.WriteAllText(Path.Combine(fixture.Aside, QemuLayout.ProjectKey(fixture.Project), "state.json"),
            JsonSerializer.Serialize(new FenceStateFile { LiveProject = fixture.Project, AsideDirectory = legacy }));
        var report = fixture.Session.ReadRecovery(fixture.Project);
        Assert.True(report.RestartBlocked);
        var row = Assert.Single(report.Returns);
        Assert.Equal(legacy, row.ReviewPath);
        Assert.False(row.CanRetry);
        Assert.True(row.CanReview);
        Assert.Equal("saved VM fixture", File.ReadAllText(fixture.Disk));
        Assert.Equal("old", fixture.ReadHost());
    }

    private sealed class Scanner : IReturnFileScanner
    {
        public FileScanVerdict Verdict = FileScanVerdict.Allowed;
        public int Calls;
        public FileScanVerdict Scan(Stream content, string name) { Calls++; return Verdict; }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPad-recovery-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "project");
        public string Aside => Path.Combine(Root, "aside");
        public string Sessions => Path.Combine(Root, "sessions");
        public string SessionDirectory => Path.Combine(Sessions, QemuLayout.ProjectKey(Project));
        public string Disk => Path.Combine(SessionDirectory, "session.qcow2");
        public Scanner Scanner { get; } = new();
        public FenceSession Session { get; }
        public int BackupCalls;
        public Fixture(bool useDefaultBackup = false)
        {
            Directory.CreateDirectory(Project);
            Directory.CreateDirectory(SessionDirectory);
            File.WriteAllText(Path.Combine(Project, "a.txt"), "old");
            File.WriteAllText(Disk, "saved VM fixture");
            Session = new FenceSession(new SetupLog(new AppPaths(userProfile: Root, appDataDir: Path.Combine(Root, "app"))),
                sessionsRoot: () => Sessions, asideRoot: () => Aside, returnScanner: Scanner,
                returnBackup: useDefaultBackup ? null : _ => { BackupCalls++; return Task.FromResult(true); });
        }
        public ReturnRecovery Receive(string wire)
        {
            var recovery = ReturnRecovery.Create(SessionDirectory);
            var manifest = new SentManifest();
            manifest.Note("a.txt", 3, File.GetLastWriteTimeUtc(Path.Combine(Project, "a.txt")).Ticks,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old"))));
            manifest.Save(recovery.Manifest);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
            var receipt = ProjectPull.Receive(stream, recovery.Payload, TimeSpan.FromSeconds(5));
            recovery.SaveTransfer(Project, receipt);
            return recovery;
        }
        public string ReadHost() => File.ReadAllText(Path.Combine(Project, "a.txt"));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
