using System.Net;
using System.Net.Sockets;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class SessionCompletionTests
{
    [Fact]
    public void CompletionCannotCarryAcrossAnotherMachineOrGeneration()
    {
        using var fixture = new Fixture();
        var owner = Owner();
        SessionCompletion.RecordReturn(fixture.Root, owner, new([], true, null, []), "owned-return");
        SessionCompletion.RecordShutdown(fixture.Root, owner, true);
        Assert.True(SessionCompletion.Returned(fixture.Root, owner));
        Assert.True(SessionCompletion.ShutdownObserved(fixture.Root, owner));
        foreach (var other in new[] { owner with { Generation = Guid.NewGuid().ToString("N") }, owner with { MachinePid = 99 }, owner with { MachineStartTicks = 44 } })
        {
            Assert.False(SessionCompletion.Returned(fixture.Root, other));
            Assert.False(SessionCompletion.ShutdownObserved(fixture.Root, other));
            Assert.Null(SessionCompletion.RecoveryDirectory(fixture.Root, other));
        }
        SessionCompletion.Clear(fixture.Root);
        Assert.False(SessionCompletion.Returned(fixture.Root, owner));
        Assert.False(SessionCompletion.ShutdownObserved(fixture.Root, owner));
    }

    [Fact]
    public void IncompleteCorruptAndConsoleEofAreNotCompletion()
    {
        using var fixture = new Fixture();
        var owner = Owner();
        File.WriteAllText(Path.Combine(fixture.Root, "console.finished"), "1");
        Assert.False(SessionCompletion.Returned(fixture.Root, owner));
        Assert.False(SessionCompletion.ShutdownObserved(fixture.Root, owner));
        SessionCompletion.RecordReturn(fixture.Root, owner, new([], false, "truncated", []), "owned-return");
        SessionCompletion.RecordShutdown(fixture.Root, owner, false);
        Assert.False(SessionCompletion.Returned(fixture.Root, owner));
        SessionCompletion.RecordReturn(fixture.Root, owner, new(["unverified.txt"], true, null), "owned-return");
        Assert.False(SessionCompletion.Returned(fixture.Root, owner));
        Assert.False(SessionCompletion.WaitForShutdownEvidence(fixture.Root, owner, TimeSpan.FromMilliseconds(100)));
        File.WriteAllText(Path.Combine(fixture.Root, "session-shutdown-completion.json"), "{broken");
        Assert.False(SessionCompletion.ShutdownObserved(fixture.Root, owner));
        File.WriteAllText(Path.Combine(fixture.Root, "session-return-completion.json"), new string('a', 8193));
        Assert.False(SessionCompletion.Returned(fixture.Root, owner));
    }

    [Fact]
    public async Task OwnerCanObserveShutdownPublishedJustAfterTheMachineExits()
    {
        using var fixture = new Fixture();
        var owner = Owner();
        var writer = Task.Run(async () => { await Task.Delay(60); SessionCompletion.RecordShutdown(fixture.Root, owner, true); });
        Assert.True(SessionCompletion.WaitForShutdownEvidence(fixture.Root, owner, TimeSpan.FromSeconds(2)));
        await writer;
        Assert.False(SessionCompletion.WaitForShutdownEvidence(fixture.Root, owner with { Generation = "old" }, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task CancellingAnIncompleteQmpHandshakeReleasesItsSocket()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connecting = ConsoleSizeLink.ConnectAsync(((IPEndPoint)server.LocalEndpoint).Port, stop.Token);
        using var peer = await server.AcceptTcpClientAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connecting);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], deadline.Token));
    }

    [Fact]
    public async Task TwoResizeRelaysWaitForReadinessAndRemainIndependent()
    {
        using var first = new Fixture(); using var second = new Fixture();
        using var left = new QmpPeer(); using var right = new QmpPeer();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        File.WriteAllText(Path.Combine(first.Root, "winsize.txt"), "28 92");
        File.WriteAllText(Path.Combine(second.Root, "winsize.txt"), "30 100");
        var firstRelay = SessionSizeRelay.RunAsync(first.Root, left.Port, stop.Token);
        var secondRelay = SessionSizeRelay.RunAsync(second.Root, right.Port, stop.Token);
        await Task.WhenAll(left.Accept(stop.Token), right.Accept(stop.Token));
        await Task.Delay(250, stop.Token);
        Assert.False(left.Stream!.DataAvailable); Assert.False(right.Stream!.DataAvailable);
        File.WriteAllText(Path.Combine(first.Root, "console.ready"), "1");
        File.WriteAllText(Path.Combine(second.Root, "console.ready"), "1");
        Assert.Contains("\"cols\":92,\"rows\":28", await left.Reader!.ReadLineAsync(stop.Token));
        Assert.Contains("\"cols\":100,\"rows\":30", await right.Reader!.ReadLineAsync(stop.Token));
        File.WriteAllText(Path.Combine(first.Root, "winsize.txt"), "-1 600");
        await Task.Delay(250, stop.Token);
        Assert.False(left.Stream.DataAvailable); Assert.False(right.Stream.DataAvailable);
        File.WriteAllText(Path.Combine(first.Root, "winsize.txt"), "26 88");
        Assert.Contains("\"cols\":88,\"rows\":26", await left.Reader.ReadLineAsync(stop.Token));
        Assert.False(right.Stream.DataAvailable);
        stop.Cancel();
        await Task.WhenAll(firstRelay, secondRelay);
        using var end = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Null(await left.Reader.ReadLineAsync(end.Token));
        Assert.Null(await right.Reader.ReadLineAsync(end.Token));
    }

    private static SessionOwnerIdentity Owner() => new(10, 11, 20, 21, 22000, Guid.NewGuid().ToString("N"), AgentChoice.Custom, "owned-project");
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class QmpPeer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _client;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public NetworkStream? Stream { get; private set; }
        public StreamReader? Reader { get; private set; }
        public QmpPeer() => _listener.Start();
        public async Task Accept(CancellationToken token)
        {
            _client = await _listener.AcceptTcpClientAsync(token); Stream = _client.GetStream();
            Reader = new(Stream, Encoding.UTF8, leaveOpen: true);
            await Stream.WriteAsync(Encoding.ASCII.GetBytes("{\"QMP\":{}}\n"), token);
            Assert.Contains("qmp_capabilities", await Reader.ReadLineAsync(token));
            await Stream.WriteAsync(Encoding.ASCII.GetBytes("{\"return\":{}}\n"), token);
        }
        public void Dispose() { Reader?.Dispose(); _client?.Dispose(); _listener.Stop(); }
    }
}
