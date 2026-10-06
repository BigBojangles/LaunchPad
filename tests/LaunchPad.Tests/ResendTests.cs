using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class ResendTests
{
    [Theory]
    [InlineData(AgentChoice.Codex)]
    [InlineData(AgentChoice.Claude)]
    [InlineData(AgentChoice.Custom)]
    public async Task ConnectsBeforePauseSendsOnlyFilesAndCommitsBaselineAfterGuestAck(string agent)
    {
        using var fixture = await Fixture.Create(agent);
        fixture.File("nested/new.txt", "changed");
        var old = fixture.Baseline("nested/new.txt", "old-old"); // Same size and timestamp.
        var send = LiveSession.TrySendAsync(fixture.Project, fixture.Token);
        using var peer = await fixture.Accept();
        Assert.Equal("PAUSE", await fixture.Pause());
        Assert.True(SessionGuardian.NeedsRecovery(fixture.Session));
        fixture.Marker("DOOR-OPEN safe-import");
        var wire = await fixture.Wire(peer);
        Assert.Equal("FILE 7 nested/new.txt\nchanged", wire);
        Assert.Equal(old, fixture.Hash("nested/new.txt"));
        Assert.False(send.IsCompleted);
        fixture.Marker("DOOR-CLOSED");
        Assert.Null(await send);
        Assert.Equal(Hash("changed"), fixture.Hash("nested/new.txt"));
        Assert.Equal(agent, LiveSession.Describe(fixture.Project)!.AgentId);
        Assert.False(SessionGuardian.NeedsRecovery(fixture.Session));
        Assert.Contains("7\t7\t1\tnested/new.txt", File.ReadAllText(Path.Combine(fixture.Session, "copy.progress")));
    }

    [Fact]
    public async Task OldOpenAndCloseMarkersCannotAuthorizeANewSend()
    {
        using var fixture = await Fixture.Create();
        fixture.File("a.txt", "new");
        fixture.Marker("DOOR-OPEN safe-import\nDOOR-CLOSED");
        var send = LiveSession.TrySendAsync(fixture.Project, fixture.Token);
        using var peer = await fixture.Accept();
        Assert.Equal("PAUSE", await fixture.Pause());
        await Task.Delay(150, fixture.Token);
        Assert.Equal(0, peer.Available);
        Assert.False(send.IsCompleted);
        // A partial marker is not a complete readiness line.
        File.AppendAllText(fixture.Serial, "DOOR-OP");
        await Task.Delay(150, fixture.Token);
        Assert.Equal(0, peer.Available);
        File.AppendAllText(fixture.Serial, "EN safe-import\n");
        Assert.Equal("FILE 3 a.txt\nnew", await fixture.Wire(peer));
        Assert.False(send.IsCompleted);
        fixture.Marker("DOOR-CLOSED");
        Assert.Null(await send);
    }

    [Theory]
    [InlineData(false, "DOOR-FAILED")]
    [InlineData(false, "AGENT-TERMINAL-FAILED")]
    [InlineData(false, "AGENT-POLICY-FAILED")]
    [InlineData(true, "DOOR-FAILED")]
    public async Task FailedOrCancelledSendPreservesBaselineAndBlocksBlindRetry(bool cancel, string marker)
    {
        using var fixture = await Fixture.Create();
        fixture.File("a.txt", "new");
        var old = fixture.Baseline("a.txt", "old");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var send = LiveSession.TrySendAsync(fixture.Project, stop.Token);
        using var peer = await fixture.Accept();
        Assert.Equal("PAUSE", await fixture.Pause());
        fixture.Marker("DOOR-OPEN safe-import");
        await fixture.Wire(peer);
        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        }
        else
        {
            fixture.Marker(marker);
            Assert.Contains("not confirmed", (await send)!.ToLowerInvariant());
        }
        Assert.Equal(old, fixture.Hash("a.txt"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Project, "a.txt")));
        Assert.True(SessionGuardian.NeedsRecovery(fixture.Session));
        Assert.Equal(LaunchPad.Models.SessionLifecycle.Failed, LiveSession.Describe(fixture.Project)!.State);
        Assert.Contains("previous send", (await LiveSession.TrySendAsync(fixture.Project, fixture.Token))!.ToLowerInvariant());
        Assert.False(fixture.Fence.Pending());
    }

    [Theory]
    [InlineData("AGENT-TERMINAL-FAILED", "terminal")]
    [InlineData("AGENT-POLICY-FAILED", "policy")]
    public async Task ACoalescedOpenFailureAndCloseCannotLoseTheFailureOrCommit(string marker, string message)
    {
        using var fixture = await Fixture.Create();
        fixture.File("a.txt", "new");
        var old = fixture.Baseline("a.txt", "old");
        var send = LiveSession.TrySendAsync(fixture.Project, fixture.Token);
        using var peer = await fixture.Accept();
        Assert.Equal("PAUSE", await fixture.Pause());
        fixture.Marker("DOOR-OPEN safe-import\n" + marker + "\nDOOR-CLOSED");
        await fixture.Wire(peer);
        Assert.Contains(message, await send);
        Assert.Equal(old, fixture.Hash("a.txt"));
        Assert.True(SessionGuardian.NeedsRecovery(fixture.Session));
    }

    [Fact]
    public async Task ConcurrentRequestsWaitForThePriorSendAndUseItsConfirmedBaseline()
    {
        using var fixture = await Fixture.Create();
        fixture.File("a.txt", "new");
        var first = LiveSession.TrySendAsync(fixture.Project, fixture.Token);
        using var peer = await fixture.Accept();
        Assert.Equal("PAUSE", await fixture.Pause());
        var second = LiveSession.TrySendAsync(fixture.Project, fixture.Token);
        await Task.Delay(150, fixture.Token);
        Assert.False(fixture.Fence.Pending());
        Assert.Equal(0, fixture.GuestStatus.Available);
        fixture.Marker("DOOR-OPEN safe-import");
        Assert.Equal("FILE 3 a.txt\nnew", await fixture.Wire(peer));
        fixture.Marker("DOOR-CLOSED");
        Assert.Null(await first);
        using var secondPeer = await fixture.Accept();
        Assert.Equal("PAUSE", await fixture.Pause());
        fixture.Marker("DOOR-OPEN safe-import");
        Assert.Equal("", await fixture.Wire(secondPeer));
        fixture.Marker("DOOR-CLOSED");
        Assert.Null(await second);
    }

    [Fact]
    public async Task MissingTransferPortNeverPausesTheAgent()
    {
        using var fixture = await Fixture.Create();
        fixture.Fence.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveSession.TrySendAsync(fixture.Project, stop.Token));
        Assert.Equal(0, fixture.GuestStatus.Available);
        Assert.False(File.Exists(Path.Combine(fixture.Session, "resend.failed")));
    }

    [Fact]
    public async Task LegacyGuestCannotAuthorizeAnImportOrPauseTheAgent()
    {
        using var fixture = await Fixture.Create();
        File.WriteAllText(fixture.Serial, "DOOR-READY\nDOOR-OPEN\nDOOR-CLOSED\n");
        Assert.Contains("runtime update", (await LiveSession.TrySendAsync(fixture.Project, fixture.Token))!);
        Assert.Equal(0, fixture.GuestStatus.Available);
        Assert.False(fixture.Fence.Pending());
        Assert.False(File.Exists(Path.Combine(fixture.Session, "resend.failed")));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly TcpClient _hostStatus;
        private readonly StatusLink _link;
        private readonly StreamReader _statusReader;
        private readonly string _root;
        public TcpListener Fence { get; }
        public TcpClient GuestStatus { get; }
        public string Project { get; }
        public string Session { get; }
        public string Serial => Path.Combine(Session, "serial.log");
        public CancellationToken Token => _deadline.Token;
        private Fixture(TcpClient host, TcpClient guest, TcpListener fence, string agent)
        {
            _root = Path.Combine(Path.GetTempPath(), "LaunchPad-resend-" + Guid.NewGuid().ToString("N"));
            Project = Path.Combine(_root, "project");
            Session = Path.Combine(_root, "session");
            Directory.CreateDirectory(Project);
            Directory.CreateDirectory(Session);
            System.IO.File.WriteAllText(Serial, "DOOR-READY safe-import\n");
            _hostStatus = host;
            GuestStatus = guest;
            Fence = fence;
            _link = new StatusLink(host);
            _statusReader = new StreamReader(guest.GetStream(), leaveOpen: true);
            LiveSession.Begin(Project, Session, ((IPEndPoint)Fence.LocalEndpoint).Port - 1, _link, agentId: agent);
        }
        public static async Task<Fixture> Create(string agent = AgentChoice.Claude)
        {
            using var status = new TcpListener(IPAddress.Loopback, 0);
            status.Start();
            var host = new TcpClient();
            await host.ConnectAsync((IPEndPoint)status.LocalEndpoint);
            var guest = await status.AcceptTcpClientAsync();
            var fence = new TcpListener(IPAddress.Loopback, 0);
            fence.Start();
            return new Fixture(host, guest, fence, agent);
        }
        public Task<TcpClient> Accept() => Fence.AcceptTcpClientAsync(Token).AsTask();
        public async Task<string?> Pause() => await _statusReader.ReadLineAsync(Token);
        public async Task<string> Wire(TcpClient peer)
        {
            using var output = new MemoryStream();
            await peer.GetStream().CopyToAsync(output, Token);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        public void Marker(string value) => System.IO.File.AppendAllText(Serial, value + "\n");
        public void File(string name, string value)
        {
            var path = Path.Combine(Project, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, value);
        }
        public string Baseline(string name, string contents)
        {
            var manifest = new SentManifest();
            var hash = ResendTests.Hash(contents);
            manifest.Note(name, Encoding.UTF8.GetByteCount(contents), System.IO.File.GetLastWriteTimeUtc(Path.Combine(Project, name)).Ticks, hash);
            manifest.Save(Path.Combine(Session, "sent.manifest"));
            return hash;
        }
        public string? Hash(string name) => SentManifest.Load(Path.Combine(Session, "sent.manifest")).ContentHash(name);
        public void Dispose()
        {
            _deadline.Cancel();
            LiveSession.End(Project, _link);
            _link.Dispose();
            _hostStatus.Dispose();
            _statusReader.Dispose();
            GuestStatus.Dispose();
            Fence.Stop();
            _deadline.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
