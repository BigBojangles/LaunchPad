using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class NativeReturnScanTests
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static string Reports => Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-native-return-scan-20261006", "native-" + RunId);
    private static byte[] Signature() => Encoding.ASCII.GetBytes(string.Concat("X5O!P%@AP[4", @"\PZX54(P^)7CC)7}", "$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!", "$H+H*"));

    [EnvironmentFact("LAUNCHPAD_NATIVE_RETURN_SCAN", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualProviderRejectsTheHarmlessMemoryControlDespiteShortReads()
    {
        var results = new List<NativeFileScanResult>();
        foreach (var shortReads in new[] { false, true })
        {
            using MemoryStream input = shortReads ? new ShortReads(Signature()) : new MemoryStream(Signature());
            results.Add(WindowsAmsiScanner.Scan(input, "owned-harmless-control.txt"));
        }
        await Save("native-memory-controls-private.json", new { results, limitations = "Harmless standard test bytes only in memory; never executed or saved. Native stream/provider test, not exhaustive antivirus or file-type coverage." });
        foreach (var result in results) Assert.Equal(FileScanVerdict.Rejected, result.Verdict);
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_RETURN_SCAN", "1")]
    [Trait("Category", "Integration")]
    public async Task NativeReadFailureIsUnavailableAndASubsequentBenignScanCanRetry()
    {
        using var broken = new BrokenRead();
        var failed = WindowsAmsiScanner.Scan(broken, "owned-unreadable.txt");
        using var clean = new MemoryStream(Encoding.UTF8.GetBytes("LaunchPad owned benign retry control."));
        var retry = WindowsAmsiScanner.Scan(clean, "owned-retry.txt");
        await Save("native-read-failure-retry-private.json", new { failed, retry, limitations = "Owned failing input callback, not disabling/removing the OS provider. Protection unchanged; no file apply." });
        Assert.Equal(FileScanVerdict.Unavailable, failed.Verdict);
        Assert.True(failed.ReadFailed);
        Assert.Equal(FileScanVerdict.Allowed, retry.Verdict);
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_RETURN_SCAN", "1")]
    [Trait("Category", "Integration")]
    public async Task NativeScannedOwnedReturnAppliesAndRetainsOriginalAndRecoveryContent()
    {
        Directory.CreateDirectory(Reports);
        var root = Path.Combine(Reports, "fixture-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "project"); Directory.CreateDirectory(live);
        var session = Path.Combine(root, "session"); Directory.CreateDirectory(session);
        var disk = Path.Combine(session, "session.qcow2"); await File.WriteAllTextAsync(disk, "Owned saved-state witness; not a VM image.");
        var diskBefore = GuestBaselineTests.HashFile(disk);
        var recovery = ReturnRecovery.Create(session);
        var baseline = new SentManifest();
        var before = Encoding.UTF8.GetBytes("owned-old-content");
        await File.WriteAllBytesAsync(Path.Combine(live, "a.txt"), before);
        baseline.Note("a.txt", before.Length, File.GetLastWriteTimeUtc(Path.Combine(live, "a.txt")).Ticks, Convert.ToHexString(SHA256.HashData(before)));
        baseline.Save(recovery.Manifest);
        var returned = new Dictionary<string, byte[]> {
            ["a.txt"] = Encoding.UTF8.GetBytes("owned-new-content"), ["empty.txt"] = Array.Empty<byte>(),
            ["large.txt"] = Encoding.ASCII.GetBytes(new string('L', 2 * 1024 * 1024 + 123)) };
        var identities = new List<ReturnedContent>();
        foreach (var item in returned)
        {
            await File.WriteAllBytesAsync(Path.Combine(recovery.Payload, item.Key), item.Value);
            identities.Add(new(item.Key, item.Value.Length, Convert.ToHexString(SHA256.HashData(item.Value))));
        }
        var receipt = new ProjectReturnReceipt(returned.Keys.ToArray(), true, null, identities);
        recovery.SaveTransfer(live, receipt);
        var originalReceipt = GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json"));
        var scanner = new RecordingNativeScanner();
        var backups = 0;
        var apply = await ReturnApplier.ApplyAsync(live, recovery, receipt, baseline, scanner, () => { backups++; return Task.FromResult(true); });
        var record = new { apply, backups, scanner.Results, root, diskBefore, diskAfter = GuestBaselineTests.HashFile(disk),
            receiptBefore = originalReceipt, receiptAfter = GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json")),
            limitations = "Owned host fixture through actual return applier and native scanner, including empty/over-1MiB benign files. Backup callback is injected; no Git writes, real project, VM launch/live guest transfer or exhaustive scanner coverage." };
        await Save("native-owned-apply-private.json", record);
        Assert.True(apply.Applied, apply.Message); Assert.Equal(1, backups);
        foreach (var item in returned) Assert.Equal(item.Value, await File.ReadAllBytesAsync(Path.Combine(live, item.Key)));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(recovery.DirectoryPath, "previous", "a.txt")));
        Assert.Equal(diskBefore, record.diskAfter); Assert.Equal(originalReceipt, record.receiptAfter);
        Assert.All(scanner.Results, result => Assert.Equal(FileScanVerdict.Allowed, result.Verdict));
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_RETURN_SCAN", "1")]
    [Trait("Category", "Integration")]
    public async Task ProductionRecoveryRetryUsesTheNativeScannerAndPreservesTheWaitingCopy()
    {
        Directory.CreateDirectory(Reports);
        var root = Path.Combine(Reports, "retry-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var sessions = Path.Combine(root, "sessions");
        var sessionDirectory = Path.Combine(sessions, QemuLayout.ProjectKey(project)); Directory.CreateDirectory(sessionDirectory);
        var disk = Path.Combine(sessionDirectory, "session.qcow2"); await File.WriteAllTextAsync(disk, "Owned saved-state witness; not a VM image.");
        var diskBefore = GuestBaselineTests.HashFile(disk);
        await File.WriteAllTextAsync(Path.Combine(project, "a.txt"), "old");
        var recovery = ReturnRecovery.Create(sessionDirectory);
        var baseline = new SentManifest();
        baseline.Note("a.txt", 3, File.GetLastWriteTimeUtc(Path.Combine(project, "a.txt")).Ticks, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old"))));
        baseline.Save(recovery.Manifest);
        using (var wire = new MemoryStream(Encoding.UTF8.GetBytes("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n")))
            recovery.SaveTransfer(project, ProjectPull.Receive(wire, recovery.Payload, TimeSpan.FromSeconds(5)));
        var receiptBefore = GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json"));
        var scanner = new NativeReadFaultOnceScanner();
        var backups = 0;
        var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "settings"));
        var session = new FenceSession(new SetupLog(paths), sessionsRoot: () => sessions,
            asideRoot: () => Path.Combine(root, "aside"), returnScanner: scanner,
            returnBackup: _ => { backups++; return Task.FromResult(true); });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CopyBackToProjectAsync(project, recovery.DirectoryPath));
        var hostBlocked = await File.ReadAllTextAsync(Path.Combine(project, "a.txt"));
        var payloadBlocked = await File.ReadAllTextAsync(Path.Combine(recovery.Payload, "a.txt"));
        var retryable = Assert.Single(session.ReadRecovery(project).Returns).CanRetry;
        var backupsBlocked = backups;
        await session.CopyBackToProjectAsync(project, recovery.DirectoryPath);
        var hostApplied = await File.ReadAllTextAsync(Path.Combine(project, "a.txt"));
        var stillRetryable = Assert.Single(session.ReadRecovery(project).Returns).CanRetry;
        var record = new { root, error = error.Message, hostBlocked, payloadBlocked, retryable, backupsBlocked,
            hostApplied, stillRetryable, backups, scanner.Results, diskBefore, diskAfter = GuestBaselineTests.HashFile(disk),
            receiptBefore, receiptAfter = GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json")),
            limitations = "Actual host recovery/CopyBack path with native provider. First owned source read callback deliberately fails; OS provider/configuration unchanged. Backup callback is injected, saved disk is a dummy witness, and transfer wire is memory-backed; no live guest transfer or real project." };
        await Save("native-production-recovery-retry-private.json", record);
        Assert.Equal("old", hostBlocked); Assert.Equal("new", payloadBlocked); Assert.True(retryable); Assert.Equal(0, backupsBlocked);
        Assert.Equal("new", hostApplied); Assert.False(stillRetryable); Assert.Equal(1, backups);
        Assert.Equal(diskBefore, record.diskAfter); Assert.Equal(receiptBefore, record.receiptAfter);
        Assert.Equal(new[] { FileScanVerdict.Unavailable, FileScanVerdict.Allowed }, scanner.Results.Select(result => result.Verdict));
    }

    private static async Task Save(string file, object evidence)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These live scanner controls require Windows.");
        Directory.CreateDirectory(Reports);
        using var identity = WindowsIdentity.GetCurrent();
        await File.WriteAllTextAsync(Path.Combine(Reports, file), JsonSerializer.Serialize(new {
            capturedUtc = DateTime.UtcNow, identity = identity.Name,
            assemblySha256 = GuestBaselineTests.HashFile(typeof(WindowsAmsiScanner).Assembly.Location), evidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class RecordingNativeScanner : IReturnFileScanner
    {
        public List<NativeFileScanResult> Results { get; } = new();
        public FileScanVerdict Scan(Stream input, string name) { var result = WindowsAmsiScanner.Scan(input, name); Results.Add(result); return result.Verdict; }
    }
    private sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    { public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7)); }
    private sealed class BrokenRead() : MemoryStream(new byte[68])
    { public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Owned unavailable source control."); }
    private sealed class NativeReadFaultOnceScanner : IReturnFileScanner
    {
        public List<NativeFileScanResult> Results { get; } = new();
        public FileScanVerdict Scan(Stream input, string name)
        {
            NativeFileScanResult result;
            if (Results.Count == 0)
            {
                using var unavailableInput = new ReadFaultLease(input);
                result = WindowsAmsiScanner.Scan(unavailableInput, name);
            }
            else result = WindowsAmsiScanner.Scan(input, name);
            Results.Add(result); return result.Verdict;
        }
    }
    private sealed class ReadFaultLease(Stream input) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Owned source read fault.");
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // This wrapper never owns or closes the readonly source lease.
    }
}
