using System.Net;
using System.Net.Sockets;
using LaunchPad.Models;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class LiveSessionRegistryTests
{
    [Fact]
    public async Task ProjectsKeepIndependentSessionsAndOldCleanupCannotRemoveAReplacement()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var firstLink = new StatusLink(await listener.AcceptTcpClientAsync());
        using var secondClient = new TcpClient();
        await secondClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var secondLink = new StatusLink(await listener.AcceptTcpClientAsync());
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-session-registry-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        try
        {
            LiveSession.Begin(first, Path.Combine(root, "session-a"), 1000, firstLink, 123, AgentChoice.Codex);
            LiveSession.Begin(second, Path.Combine(root, "session-b"), 2000, secondLink, 456, AgentChoice.Claude);
            var firstRecord = LiveSession.Describe(first + Path.DirectorySeparatorChar);
            Assert.NotNull(firstRecord);
            Assert.Equal("vm:" + QemuLayout.ProjectKey(first), firstRecord.Id);
            Assert.Equal(123, firstRecord.ProcessId);
            Assert.Equal(AgentChoice.Codex, firstRecord.AgentId);
            Assert.Equal(SessionLifecycle.Starting, firstRecord.State);
            Assert.Null(firstRecord.WindowHandle);
            Assert.Equal(AgentChoice.Claude, LiveSession.Describe(second)!.AgentId);
            LiveSession.End(second, secondLink);
            Assert.Null(LiveSession.Describe(second));
            Assert.Equal(firstRecord.Id, LiveSession.Describe(first)!.Id);
            LiveSession.Begin(first, Path.Combine(root, "session-a", "upgrade-" + Guid.NewGuid().ToString("N")), 3000, secondLink, 789, AgentChoice.Claude);
            LiveSession.End(first, firstLink);
            Assert.Equal(789, LiveSession.Describe(first)!.ProcessId);
            Assert.Equal(firstRecord.Id, LiveSession.Describe(first)!.Id);
            LiveSession.End(first, secondLink);
            Assert.Null(LiveSession.Describe(first));
        }
        finally { LiveSession.End(first); LiveSession.End(second); }
    }

    [Fact]
    public async Task LostStatusDoesNotLeaveBusyOrReadyOnAnIndependentProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-lamps-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "first");
        var b = Path.Combine(root, "second");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var firstGeneration = Guid.NewGuid().ToString("N");
        var secondGeneration = Guid.NewGuid().ToString("N");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var first = new TcpClient();
        await first.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var firstStatus = new StatusLink(await listener.AcceptTcpClientAsync(), new(a, firstGeneration));
        using var second = new TcpClient();
        await second.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var secondStatus = new StatusLink(await listener.AcceptTcpClientAsync(), new(b, secondGeneration));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            LiveSession.Begin(a, a, 1000, firstStatus);
            LiveSession.Begin(b, b, 2000, secondStatus);
            await first.GetStream().WriteAsync(AgentActivityTests.Line(AgentActivityTests.Event(firstGeneration, 1, AgentEventKind.RunStarted)), timeout.Token);
            await second.GetStream().WriteAsync(AgentActivityTests.Line(AgentActivityTests.Event(secondGeneration, 1, AgentEventKind.RunStarted)), timeout.Token);
            await second.GetStream().WriteAsync(AgentActivityTests.Line(AgentActivityTests.Event(secondGeneration, 2, AgentEventKind.NeedsAttention, question: "question-1")), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => LiveSession.Describe(a)!.State == SessionLifecycle.Busy
                && LiveSession.Describe(b)!.State == SessionLifecycle.NeedsAnswer, timeout.Token);
            first.Dispose();
            await WindowsConsoleProbeTests.Until(() => LiveSession.Describe(a)!.State == SessionLifecycle.Unknown, timeout.Token);
            Assert.False(firstStatus.Snapshot.Connected);
            Assert.Null(firstStatus.Snapshot.Activity);
            Assert.NotNull(LiveSession.Describe(a)!.Error);
            Assert.Equal(SessionLifecycle.NeedsAnswer, LiveSession.Describe(b)!.State);
            await second.GetStream().WriteAsync(AgentActivityTests.Line(AgentActivityTests.Event(secondGeneration, 3, AgentEventKind.RunFinished)), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => LiveSession.Describe(b)!.State == SessionLifecycle.Idle, timeout.Token);
        }
        finally
        {
            LiveSession.End(a); LiveSession.End(b);
            firstStatus.Dispose(); secondStatus.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(b, secondGeneration) is { Connected: false }, timeout.Token);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StatusMessagesDoNotInterpretAuthenticationPayloadAsSessionActivity()
    {
        var buffer = new StatusBuffer();
        var auth = System.Text.Encoding.ASCII.GetBytes("busy\nAUTH 16\nneeds-an-answer\nidle\n");
        buffer.Push(auth, auth.Length);
        Assert.True(buffer.AuthFinished);
        Assert.Equal("needs-an-answer\n", System.Text.Encoding.ASCII.GetString(buffer.AuthBody));
        Assert.Equal("idle", buffer.Activity);
        Assert.Equal(AgentActivity.Unknown, buffer.ActivitySnapshot.State);
    }
}
