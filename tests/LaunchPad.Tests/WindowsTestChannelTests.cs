using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsTestChannelTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task StalledTransferExpiresWithoutEndingOwnerLifetime(string operation)
    {
        using var owner = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var type = typeof(WindowsTestChannel).GetNestedType("IdleTransferStream", System.Reflection.BindingFlags.NonPublic)!;
        using var transfer = (Stream)Activator.CreateInstance(type, new StalledStream(), TimeSpan.FromMilliseconds(100))!;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (operation == "write") await transfer.WriteAsync(new byte[1], owner.Token);
            else if (operation == "read") await transfer.ReadAsync(new byte[1], owner.Token);
            else await transfer.FlushAsync(owner.Token);
        });
        Assert.False(owner.IsCancellationRequested);
    }

    [Fact]
    public async Task AuthenticatedOwnedSocketRunsBackendAndReturnsSignedMetadata()
    {
        var fixture = new Fixture();
        using var pair = await Pair.Create();
        WindowsQemuPeer.Require(pair.Host, Environment.ProcessId);
        Assert.Throws<InvalidDataException>(() => WindowsQemuPeer.Require(pair.Host, -1));
        using var auth = new WindowsTestChannel(fixture.Generation);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = new Executor();
        var bridge = new WindowsTestBridge(fixture.Runs, fixture.Generation, executor);
        var serving = auth.ServeAsync(pair.Host.GetStream(), bridge, stop.Token);
        var device = pair.Guest.GetStream();
        var hello = (await Line(device, stop.Token)).Split(' ');
        Assert.Equal(fixture.Generation, hello[2]);
        var key = Convert.FromHexString(hello[3]);
        await Send(device, "LP-WINDOWS-GUEST 1 " + Sign(key, fixture.Generation, "ready", []), stop.Token);
        var wire = await fixture.Packet();
        var metadata = Metadata(wire);
        await Send(device, "LP-WINDOWS-PROOF 1 " + Sign(key, fixture.Generation, "request", metadata), stop.Token);
        await device.WriteAsync(wire, stop.Token);
        var proof = await Line(device, stop.Token);
        var header = await Line(device, stop.Token);
        var responseMetadata = new byte[int.Parse(header.Split(' ')[2])];
        await device.ReadExactlyAsync(responseMetadata, stop.Token);
        Assert.Equal("LP-WINDOWS-RESULT-PROOF 1 " + Sign(key, fixture.Generation, "response", responseMetadata), proof);
        var result = await serving;
        Assert.Equal("finished", result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, executor.Calls);
        Assert.Equal("LP-WINDOWS-FILES-END", await Line(device, stop.Token));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("changed-metadata")]
    [InlineData("wrong-generation")]
    public async Task UnauthenticatedMetadataNeverStagesOrExecutes(string attack)
    {
        var fixture = new Fixture();
        using var pair = await Pair.Create();
        using var auth = new WindowsTestChannel(fixture.Generation);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = new Executor();
        var serving = auth.ServeAsync(pair.Host.GetStream(), new WindowsTestBridge(fixture.Runs, fixture.Generation, executor), stop.Token);
        var device = pair.Guest.GetStream();
        var hello = (await Line(device, stop.Token)).Split(' ');
        var key = Convert.FromHexString(hello[3]);
        await Send(device, "LP-WINDOWS-GUEST 1 " + Sign(key, fixture.Generation, "ready", []), stop.Token);
        var wire = await fixture.Packet();
        var bytes = Metadata(wire);
        var proof = Sign(attack == "wrong-key" ? new byte[32] : key,
            attack == "wrong-generation" ? Guid.NewGuid().ToString("N") : fixture.Generation, "request", bytes);
        if (attack == "changed-metadata")
        {
            var text = Encoding.UTF8.GetString(wire).Replace("dotnet", "python");
            wire = Encoding.UTF8.GetBytes(text);
        }
        await Send(device, "LP-WINDOWS-PROOF 1 " + proof, stop.Token);
        await device.WriteAsync(wire, stop.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => serving);
        Assert.Equal(0, executor.Calls);
        Assert.False(Directory.Exists(fixture.Runs));
    }

    [Fact]
    public async Task OwnerCancellationStopsIdleAuthenticatedChannelWithoutStartingWork()
    {
        var fixture = new Fixture();
        using var pair = await Pair.Create();
        using var auth = new WindowsTestChannel(fixture.Generation);
        using var stop = new CancellationTokenSource();
        var executor = new Executor();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = auth.ServeAsync(pair.Host.GetStream(), new WindowsTestBridge(fixture.Runs, fixture.Generation, executor), stop.Token,
            () => ready.SetResult());
        var device = pair.Guest.GetStream();
        var hello = (await Line(device, default)).Split(' ');
        await Send(device, "LP-WINDOWS-GUEST 1 " + Sign(Convert.FromHexString(hello[3]), fixture.Generation, "ready", []), default);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
        Assert.Equal(0, executor.Calls);
        Assert.False(Directory.Exists(fixture.Runs));
    }

    [Fact]
    public void AdditionalPortPreservesExistingOffsetsAndCannotOverlapNextVm()
    {
        Assert.Equal(20001, QemuCommand.FencePort(20000));
        Assert.Equal(20002, QemuCommand.StatusPort(20000));
        Assert.Equal(20003, QemuCommand.TuiPort(20000));
        Assert.Equal(20004, QemuCommand.WindowsTestPort(20000));
        Assert.Equal(20005, PortChoice.Next([20000]));
        for (var offset = -4; offset <= 4; offset++) Assert.True(PortChoice.Overlaps(20000 + offset, [20000]));
        Assert.False(PortChoice.Overlaps(20005, [20000]));
        var command = QemuCommand.Build("whpx", "session.qcow2", 0, 20000, "fence", null);
        Assert.Contains("virtserialport,bus=vserial0.0,chardev=wtestch,name=launchpad-windows-test", command);
    }

    [Fact]
    public void TrustedLedgerRootCannotOverlapAccountWritableSession()
    {
        var fixture = new Fixture();
        var paths = new AppPaths(userProfile: fixture.Root, appDataDir: Path.Combine(fixture.Root, "host-data"));
        var root = WindowsTestSessionHost.ManagedRoot(paths, Path.Combine(fixture.Root, "project"), Path.Combine(fixture.Root, "qemu-session"));
        Assert.StartsWith(paths.AppDataDir, root);
        Assert.Throws<InvalidDataException>(() => WindowsTestSessionHost.ManagedRoot(paths, Path.Combine(fixture.Root, "project"), root));
        Assert.Throws<InvalidDataException>(() => WindowsTestSessionHost.ManagedRoot(paths, Path.Combine(fixture.Root, "project"), Path.Combine(root, "child")));
    }

    private static string Sign(byte[] key, string generation, string direction, byte[] metadata)
    {
        var prefix = Encoding.ASCII.GetBytes("LaunchPad.WindowsTest.v1\n" + generation + "\n" + direction + "\n");
        return Convert.ToHexString(HMACSHA256.HashData(key, prefix.Concat(SHA256.HashData(metadata)).ToArray())).ToLowerInvariant();
    }
    private static byte[] Metadata(byte[] wire)
    {
        var start = Array.IndexOf(wire, (byte)'\n') + 1;
        var length = int.Parse(Encoding.ASCII.GetString(wire, 0, start - 1).Split(' ')[2]);
        return wire.AsSpan(start, length).ToArray();
    }
    private static Task Send(Stream stream, string line, CancellationToken token) => stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), token).AsTask();
    private static async Task<string> Line(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < 192)
        {
            await stream.ReadExactlyAsync(next, token);
            if (next[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add(next[0]);
        }
        throw new InvalidDataException();
    }
    private sealed class Executor : IWindowsTestExecutor
    {
        public int Calls;
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string copy, CancellationToken token)
        { Calls++; return Task.FromResult(new WindowsTestExecution("finished", 0)); }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => new(Task.Delay(Timeout.Infinite, token));
        public override Task FlushAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Fixture
    {
        public string Root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-transport-20261007", "fixture-" + Guid.NewGuid().ToString("N"));
        public string Generation = Guid.NewGuid().ToString("N");
        public string Runs => Path.Combine(Root, "runs");
        public async Task<byte[]> Packet()
        {
            using var packet = new MemoryStream();
            await WindowsTestProtocol.WriteRequestAsync(packet, new(1, Generation, Guid.NewGuid().ToString("N"), "dotnet", null, ".", ["build"], false, 30, [], []), Root, default);
            return packet.ToArray();
        }
    }
    private sealed class Pair : IDisposable
    {
        public TcpClient Host = new(), Guest = null!;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public static async Task<Pair> Create()
        {
            var pair = new Pair();
            pair._listener.Start();
            var accept = pair._listener.AcceptTcpClientAsync();
            await pair.Host.ConnectAsync((IPEndPoint)pair._listener.LocalEndpoint);
            pair.Guest = await accept;
            return pair;
        }
        public void Dispose() { Host.Dispose(); Guest.Dispose(); _listener.Stop(); }
    }
}
