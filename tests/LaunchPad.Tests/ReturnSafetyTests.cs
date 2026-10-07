using System.Security.Cryptography;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class ReturnSafetyTests
{
    [Theory]
    [InlineData("PROJECT 1\nFILE 3 a.txt\nnewPROJECT-END\n", true)]
    [InlineData("PROJECT 1\nFILE 3 a.txt\nne", false)]
    [InlineData("PROJECT 1\nFILE 3 a.txt\nnew", false)]
    [InlineData("PROJECT 2\nFILE 3 a.txt\nnewPROJECT-END\n", false)]
    [InlineData("PROJECT 0\nPROJECT-END", false)]
    [InlineData("PROJECT 0\nPROJECT-END\n", true)]
    [InlineData("PROJECT 0\nFILE 0 a.txt\nPROJECT-END\n", false)]
    [InlineData("FILE 3 a.txt\nnewPROJECT-END\n", false)]
    [InlineData("PROJECT 2\nFILE 3 a.txt\nnewFILE 3 A.txt\noldPROJECT-END\n", false)]
    public void ReceiverRequiresWholeDeclaredBatchAndCompletionMarker(string wire, bool complete)
    {
        using var fixture = new Fixture();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var receipt = ProjectPull.Receive(stream, fixture.Recovery.Payload, TimeSpan.FromSeconds(5));
        Assert.Equal(complete, receipt.Complete);
        if (!complete) Assert.NotNull(receipt.Error);
        if (wire.StartsWith("PROJECT ", StringComparison.Ordinal) && wire.Contains("FILE 3 a.txt\nnew", StringComparison.Ordinal))
            Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Recovery.Payload, "a.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("nested/NUL.txt")]
    [InlineData("nested/name.")]
    [InlineData("nested/name ")]
    public void UnsafePathsRejectTheWholeReturn(string path)
    {
        using var fixture = new Fixture();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"PROJECT 1\nFILE 3 {path}\nnewPROJECT-END\n"));
        var receipt = ProjectPull.Receive(stream, fixture.Recovery.Payload, TimeSpan.FromSeconds(5));
        Assert.False(receipt.Complete);
        Assert.Empty(receipt.Files);
        Assert.Empty(Directory.EnumerateFiles(fixture.Recovery.Payload, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(FileScanVerdict.Rejected)]
    [InlineData(FileScanVerdict.Unavailable)]
    public async Task ARejectedOrUnavailableScanLeavesEveryWindowsFileUnchanged(FileScanVerdict verdict)
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        fixture.Existing("b.txt", "old-b", "new-b");
        var scanner = new Scanner((name, _) => name == "b.txt" ? verdict : FileScanVerdict.Allowed);
        var result = await fixture.Apply(scanner);
        Assert.False(result.Applied);
        Assert.Equal(0, fixture.BackupCalls);
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.Equal("old-b", fixture.Read("b.txt"));
        Assert.True(File.Exists(Path.Combine(fixture.Recovery.Payload, "a.txt")));
    }

    [Fact]
    public async Task SameSizeHostEditWithRestoredTimestampStillBlocksReturn()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        var path = Path.Combine(fixture.Live, "a.txt");
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "own"); File.SetLastWriteTimeUtc(path, stamp);
        var result = await fixture.Apply(new Scanner());
        Assert.False(result.Applied);
        Assert.Equal("own", fixture.Read("a.txt"));
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public async Task MissingLegacyHashAndIncompleteReceiptCannotOverwriteHostFiles()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        fixture.Manifest.Note("a.txt", 3, 1);
        Assert.False((await fixture.Apply(new Scanner())).Applied);
        Assert.False((await fixture.Apply(new Scanner(), complete: false)).Applied);
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public async Task BackupFailurePreservesHostAndRecoveryFiles()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        fixture.Backup = () => Task.FromResult(false);
        Assert.False((await fixture.Apply(new Scanner())).Applied);
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Recovery.Payload, "a.txt")));
        var second = ReturnRecovery.Create(fixture.Session);
        Assert.NotEqual(fixture.Recovery.DirectoryPath, second.DirectoryPath);
        Assert.True(Directory.Exists(fixture.Recovery.Payload));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("nested/.git/config")]
    [InlineData("nested/.GIT")]
    public async Task RepositoryMetadataBlocksTheWholeBatchBeforeBackupOrWrites(string metadata)
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        fixture.New(metadata, "guest metadata");
        var result = await fixture.Apply(new Scanner());
        Assert.False(result.Applied);
        Assert.Contains("repository metadata", result.Message);
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(fixture.Live, metadata)));
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public async Task VerifiedApplyPreservesLiveOnlyFilesAndOriginalContentForRecovery()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        File.WriteAllText(Path.Combine(fixture.Live, "host-only.txt"), "keep");
        fixture.New("nested/new.txt", "guest");
        var result = await fixture.Apply(new Scanner());
        Assert.True(result.Applied, result.Message);
        Assert.Equal(1, fixture.BackupCalls);
        Assert.Equal("new", fixture.Read("a.txt"));
        Assert.Equal("guest", fixture.Read("nested/new.txt"));
        Assert.Equal("keep", fixture.Read("host-only.txt"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Recovery.DirectoryPath, "previous", "a.txt")));
    }

    [Fact]
    public async Task LaterWriteFailureRestoresEarlierFileAndRetainsBothVersions()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        fixture.New("z.txt", "guest");
        fixture.Backup = () => { Directory.CreateDirectory(Path.Combine(fixture.Live, "z.txt")); return Task.FromResult(true); };
        var result = await fixture.Apply(new Scanner());
        Assert.False(result.Applied);
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Recovery.Payload, "a.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.Recovery.DirectoryPath, "previous", "a.txt")));
        var previousPath = Path.Combine(fixture.Recovery.DirectoryPath, "previous", "a.txt");
        var savedOriginal = File.ReadAllBytes(previousPath);
        Directory.Delete(Path.Combine(fixture.Live, "z.txt"));
        fixture.Backup = () => Task.FromResult(true);
        var retry = await fixture.Apply(new Scanner());
        Assert.True(retry.Applied, retry.Message);
        Assert.Equal("new", fixture.Read("a.txt"));
        Assert.Equal("guest", fixture.Read("z.txt"));
        Assert.Equal(savedOriginal, File.ReadAllBytes(previousPath));
        Assert.Equal(2, fixture.BackupCalls);
    }

    [Fact]
    public async Task ChangedPreviousBackupBlocksRetryWithoutReplacingEitherSavedVersion()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        var previousPath = Path.Combine(fixture.Recovery.DirectoryPath, "previous", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
        File.WriteAllText(previousPath, "different original");
        var result = await fixture.Apply(new Scanner());
        Assert.False(result.Applied);
        Assert.Contains("does not match", result.Message);
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Recovery.Payload, "a.txt")));
        Assert.Equal("different original", File.ReadAllText(previousPath));
    }

    [Fact]
    public async Task LockedHostFileBlocksAllChanges()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        using var writer = new FileStream(Path.Combine(fixture.Live, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False((await fixture.Apply(new Scanner())).Applied);
        Assert.Equal(0, fixture.BackupCalls);
        writer.Dispose();
        Assert.Equal("old", fixture.Read("a.txt"));
    }

    [Fact]
    public void AReadErrorAfterAnInitialPieceCannotPassTheFile()
    {
        using var input = new FailingReadStream(new byte[1024 * 1024 + 1]);
        var content = new AmsiContentStream(input, "owned-read-failure.txt");
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)input.Length);
        try
        {
            Assert.True(content.Read(0, (uint)input.Length, buffer, out _) < 0);
            Assert.True(content.ReadFailed);
            Assert.Equal(2, input.Calls);
            Assert.Equal(65536, content.BytesRead);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    private sealed class FailingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Calls;
        public override int Read(byte[] buffer, int offset, int count) => ++Calls == 2
            ? throw new IOException("Owned read failure after one piece.") : base.Read(buffer, offset, count);
    }

    [Fact]
    public async Task HardlinkedHostFileCannotChangeAnotherPath()
    {
        using var fixture = new Fixture();
        fixture.Existing("a.txt", "old", "new");
        var alias = Path.Combine(fixture.Root, "outside-alias.txt");
        Assert.True(CreateHardLink(alias, Path.Combine(fixture.Live, "a.txt"), IntPtr.Zero));
        Assert.False((await fixture.Apply(new Scanner())).Applied);
        Assert.Equal("old", File.ReadAllText(alias));
        Assert.Equal("old", fixture.Read("a.txt"));
        Assert.Equal(0, fixture.BackupCalls);
    }

    [Fact]
    public void JunctionReturnPathCannotWriteOutsideRecovery()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(fixture.Recovery.Payload, "link");
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("PROJECT 1\nFILE 3 link/a.txt\nnewPROJECT-END\n"));
            Assert.False(ProjectPull.Receive(input, fixture.Recovery.Payload, TimeSpan.FromSeconds(5)).Complete);
            Assert.False(File.Exists(Path.Combine(outside, "a.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr security);

    private sealed class Scanner(Func<string, Stream, FileScanVerdict>? verdict = null) : IReturnFileScanner
    {
        public FileScanVerdict Scan(Stream content, string name) => verdict?.Invoke(name, content) ?? FileScanVerdict.Allowed;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        public string Live => Path.Combine(Root, "live");
        public string Session => Path.Combine(Root, "session");
        public ReturnRecovery Recovery { get; }
        public SentManifest Manifest { get; } = new();
        public List<string> Files { get; } = new();
        public List<ReturnedContent> Contents { get; } = new();
        public int BackupCalls;
        public Func<Task<bool>> Backup = () => Task.FromResult(true);
        public Fixture() { Directory.CreateDirectory(Live); Recovery = ReturnRecovery.Create(Session); }
        public void Existing(string path, string old, string changed)
        {
            var target = Path.Combine(Live, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, old);
            Manifest.Note(path, Encoding.UTF8.GetByteCount(old), File.GetLastWriteTimeUtc(target).Ticks,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
            New(path, changed);
        }
        public void New(string path, string content)
        {
            var source = Path.Combine(Recovery.Payload, path);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, content);
            Files.Add(path);
            Contents.Add(new ReturnedContent(path, Encoding.UTF8.GetByteCount(content), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))));
        }
        public string Read(string path) => File.ReadAllText(Path.Combine(Live, path));
        public Task<ReturnApplyResult> Apply(IReturnFileScanner scanner, bool complete = true) =>
            ReturnApplier.ApplyAsync(Live, Recovery, new ProjectReturnReceipt(Files, complete, null, Contents), Manifest, scanner,
                () => { BackupCalls++; return Backup(); });
        public void Dispose() { Directory.Delete(Root, recursive: true); }
    }
}
