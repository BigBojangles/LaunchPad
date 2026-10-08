using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class GrokHookLampTests
{
    private static HookDiagnostic Record(string kind, string? prompt = null, string notice = "absent") =>
        new(2, new string('a', 32), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, new(),
            new string('b', 64), null, notice, false, 0, 0, true, prompt, false);

    [Fact]
    public void RootPromptAttentionAndIdleAreStateOnlyAndRejectLateOrChildReports()
    {
        var generation = Guid.NewGuid().ToString("N");
        var lamp = new GrokHookLamp(generation);
        var first = new string('c', 64);
        var second = new string('d', 64);
        Assert.False(lamp.Observe(Record("UserPromptSubmit", first)));
        Assert.True(lamp.Observe(Record("SessionStart")));
        var submitted = Record("UserPromptSubmit", first);
        Assert.True(lamp.Observe(submitted));
        Assert.False(lamp.Observe(submitted));
        Assert.Equal(AgentActivity.Working, lamp.Snapshot.State);
        Assert.False(lamp.Observe(Record("Stop", first)));
        Assert.Equal(AgentActivity.Working, lamp.Snapshot.State);
        Assert.True(lamp.Observe(Record("Notification", first, "permission_prompt")));
        Assert.Equal(AgentActivity.NeedsAttention, lamp.Snapshot.State);
        Assert.False(lamp.Observe(Record("PostToolUse", first)));
        Assert.Equal(AgentActivity.NeedsAttention, lamp.Snapshot.State);
        Assert.True(lamp.Observe(Record("UserPromptSubmit", second)));
        Assert.False(lamp.Observe(Record("Notification", first, "idle_prompt")));
        Assert.False(lamp.Observe(Record("Notification", second, "idle_prompt") with { ChildSession = true }));
        Assert.False(lamp.Observe(Record("Notification", second, "idle_prompt") with { SessionHash = new string('e', 64) }));
        Assert.True(lamp.Observe(Record("Notification", notice: "idle_prompt")));
        Assert.Equal(AgentActivity.Idle, lamp.Snapshot.State);
        Assert.Null(lamp.Snapshot.LastEvent);
        Assert.True(AgentActivityTracker.IsValidSnapshot(lamp.Snapshot, generation));
        Assert.True(lamp.Observe(Record("SessionEnd")));
        Assert.False(lamp.Observe(Record("UserPromptSubmit", first)));
    }

    [Fact]
    public void OldCaptureMissingIdentityAndStaleDataCannotStartALamp()
    {
        var lamp = new GrokHookLamp(Guid.NewGuid().ToString("N"));
        Assert.False(lamp.Observe(Record("SessionStart") with { Version = 1 }));
        Assert.False(lamp.Observe(Record("SessionStart") with { ChildSession = null }));
        Assert.False(lamp.Observe(Record("SessionStart") with { CwdMatchesProject = false }));
        Assert.False(lamp.Observe(Record("SessionStart") with { CapturedUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds() }));
        Assert.Equal(AgentActivity.Unknown, lamp.Snapshot.State);
        Assert.True(lamp.Observe(Record("SessionStart")));
        Assert.False(lamp.Observe(Record("UserPromptSubmit")));
    }

    [Fact]
    public async Task LiveGrokStatusPublishesLampsWithoutAcceptedEventsPagesOrAuthInterference()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-hook-lamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var generation = Guid.NewGuid().ToString("N");
        var accepted = new List<AcceptedAgentActivityEvent>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var guest = new TcpClient();
            await guest.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var link = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token),
                new(root, generation, root, AgentChoice.Grok), notify: value => accepted.Add(value));
            static byte[] Wire(params HookDiagnostic[] rows) => Encoding.UTF8.GetBytes(string.Concat(rows.Select(row =>
                HookDiagnostics.Prefix + JsonSerializer.Serialize(row) + "\n")));
            var prompt = new string('c', 64);
            await guest.GetStream().WriteAsync(Wire(Record("SessionStart"), Record("UserPromptSubmit", prompt)), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => link.AgentActivity.State == AgentActivity.Working, timeout.Token);
            await guest.GetStream().WriteAsync(Wire(Record("Notification", prompt, "elicitation_dialog")), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => link.AgentActivity.State == AgentActivity.NeedsAttention, timeout.Token);
            await guest.GetStream().WriteAsync(Wire(Record("Notification", prompt, "idle_prompt")), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation)?.Activity.State == AgentActivity.Idle, timeout.Token);
            Assert.True(link.UsesHookLamp);
            Assert.Empty(accepted);
            Assert.False(File.Exists(Path.Combine(root, SessionActivityStore.EventsFile)));
            Assert.False(SessionActivityStore.Read(root, generation)!.HistoryComplete);
            Assert.False(link.AgentExited);
            guest.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: false }, timeout.Token);
            Assert.Equal(AgentActivity.Unknown, link.AgentActivity.State);
            using var nextGuest = new TcpClient();
            await nextGuest.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var nextLink = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token),
                new(root, generation, root, AgentChoice.Grok), notify: value => accepted.Add(value));
            Assert.Equal(AgentActivity.Unknown, nextLink.AgentActivity.State);
            await nextGuest.GetStream().WriteAsync(Wire(Record("SessionStart"), Record("UserPromptSubmit", prompt)), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => nextLink.AgentActivity.State == AgentActivity.Working, timeout.Token);
            nextGuest.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: false }, timeout.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
