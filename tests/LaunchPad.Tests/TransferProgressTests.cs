using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;

namespace LaunchPad.Tests;

public sealed class TransferProgressTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(131073)]
    public async Task FinalProgressMatchesExactWireAndUnavailableProgressDoesNotBlockData(int size)
    {
        var root = Fixture();
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        byte[] content = size < 0 ? [] : new byte[size];
        if (size >= 0) await File.WriteAllBytesAsync(Path.Combine(project, "fixture.bin"), content);
        var progress = Path.Combine(root, "copy.progress");
        using var wire = new MemoryStream();
        var manifest = await FenceHost.SendProjectAsync(wire, project, progress, null, CancellationToken.None);
        byte[] expected = size < 0 ? [] : Encoding.UTF8.GetBytes(FenceFiles.Header(size, "fixture.bin")).Concat(content).ToArray();
        Assert.Equal(expected, wire.ToArray());
        var fields = (await File.ReadAllTextAsync(progress)).Split('\t');
        Assert.Equal((Math.Max(0, size)).ToString(), fields[0]);
        Assert.Equal(fields[0], fields[1]);
        Assert.Equal(size < 0 ? "0" : "1", fields[2]);
        Assert.Equal(size < 0 ? "" : "fixture.bin", fields[3]);
        Assert.Equal("1", fields[4]);
        if (size >= 0) Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), manifest.ContentHash("fixture.bin"));
        using var withoutProgress = new MemoryStream();
        await FenceHost.SendProjectAsync(withoutProgress, project, Path.Combine(root, "missing", "copy.progress"), null, CancellationToken.None);
        Assert.Equal(expected, withoutProgress.ToArray());
    }

    [Fact]
    public async Task InterruptedPayloadCannotPublishFinalCountersOrOverwriteTheSavedBaseline()
    {
        var root = Fixture();
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllBytesAsync(Path.Combine(project, "fixture.bin"), new byte[262144]);
        var saved = new SentManifest();
        saved.Note("fixture.bin", 1, 1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })));
        var manifestPath = Path.Combine(root, "sent.manifest");
        saved.Save(manifestPath);
        var baseline = await File.ReadAllBytesAsync(manifestPath);
        var progress = Path.Combine(root, "copy.progress");
        using var stop = new CancellationTokenSource();
        using var wire = new DigestStream(() => stop.Cancel(), cancelAtWrite: 2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FenceHost.SendProjectAsync(wire, project, progress, manifestPath, stop.Token));
        var fields = (await File.ReadAllTextAsync(progress)).Split('\t');
        Assert.True(long.Parse(fields[0]) < 262144);
        Assert.Equal("262144", fields[1]);
        Assert.Equal("0", fields[2]);
        Assert.Equal("0", fields[4]);
        Assert.Equal(baseline, await File.ReadAllBytesAsync(manifestPath));
    }

    [EnvironmentFact("LAUNCHPAD_TRANSFER_BENCH", "1")]
    [Trait("Category", "Integration")]
    public async Task OwnedHostSenderBenchmarkKeepsWireAndManifestIdentity()
    {
        var phase = Path.GetFullPath(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "transfer-progress-20261007"));
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_TRANSFER_BENCH_ROOT")!);
        var stage = Environment.GetEnvironmentVariable("LAUNCHPAD_TRANSFER_BENCH_STAGE");
        Assert.True(root.StartsWith(phase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(stage is "before" or "after");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        const int count = 10000, length = 10486;
        var content = Enumerable.Repeat((byte)'a', length).ToArray();
        var contentHash = Convert.ToHexString(SHA256.HashData(content));
        if (stage == "before")
        {
            Assert.Empty(Directory.EnumerateFiles(project));
            for (var i = 0; i < count; i++)
                await File.WriteAllBytesAsync(Path.Combine(project, $"f{i:D5}.dat"), content);
        }
        Assert.Equal(count, Directory.EnumerateFiles(project).Count());
        using (var warmup = new DigestStream())
            await FenceHost.SendProjectAsync(warmup, project, null, null, CancellationToken.None);
        var samples = new List<Sample>();
        for (var i = 0; i < 3; i++)
        {
            foreach (var showProgress in new[] { true, false })
            {
                using var wire = new DigestStream();
                var watch = Stopwatch.StartNew();
                var progress = showProgress ? Path.Combine(root, "copy.progress") : null;
                var sent = await FenceHost.SendProjectAsync(wire, project, progress, null, CancellationToken.None);
                watch.Stop();
                Assert.Equal(contentHash, sent.ContentHash("f00000.dat"));
                Assert.Equal(contentHash, sent.ContentHash("f09999.dat"));
                var baselineHash = Convert.ToHexString(SHA256.HashData(sent.GuestBaseline()));
                samples.Add(new(showProgress, watch.Elapsed.TotalMilliseconds, wire.Bytes, wire.Finish(), baselineHash));
                if (showProgress)
                {
                    var fields = (await File.ReadAllTextAsync(progress!)).Split('\t');
                    Assert.Equal(((long)count * length).ToString(), fields[0]);
                    Assert.Equal(fields[0], fields[1]);
                    Assert.Equal(count.ToString(), fields[2]);
                    Assert.Equal("1", fields[4]);
                }
            }
        }
        Assert.Single(samples.Select(row => row.WireSha256).Distinct());
        Assert.Single(samples.Select(row => row.ManifestSha256).Distinct());
        if (stage == "after")
        {
            var previous = JsonSerializer.Deserialize<Benchmark>(await File.ReadAllTextAsync(Path.Combine(root, "before-private.json")))!;
            Assert.Equal(previous.Samples[0].WireSha256, samples[0].WireSha256);
            Assert.Equal(previous.Samples[0].ManifestSha256, samples[0].ManifestSha256);
            Assert.Equal(previous.Samples[0].WireBytes, samples[0].WireBytes);
        }
        var report = new Benchmark(stage!, count, (long)count * length, samples.ToArray(),
            "Warm host-sender microbenchmark with hashing discard sink; no socket/VM/import/readiness/return or end-to-end user timing");
        await File.WriteAllTextAsync(Path.Combine(root, stage + "-private.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed record Sample(bool Progress, double Milliseconds, long WireBytes, string WireSha256, string ManifestSha256);
    public sealed record Benchmark(string Stage, int Files, long PayloadBytes, Sample[] Samples, string Scope);
    private static string Fixture()
    {
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "transfer-progress-20261007", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private sealed class DigestStream(Action? onWrite = null, int cancelAtWrite = 0) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _writes;
        public long Bytes { get; private set; }
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Append(buffer.AsSpan(offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Append(buffer.Span); return ValueTask.CompletedTask; }
        private void Append(ReadOnlySpan<byte> bytes)
        { _hash.AppendData(bytes); Bytes += bytes.Length; if (++_writes == cancelAtWrite) onWrite?.Invoke(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
