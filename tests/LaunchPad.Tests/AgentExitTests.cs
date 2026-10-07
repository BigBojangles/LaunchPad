using System.Net;
using System.Net.Sockets;
using System.Text;
using LaunchPad.Models;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class AgentExitTests
{
    [Fact]
    public void ExitMarkerInsideAuthHomeOrDiagnosticCannotStartReturn()
    {
        var buffer = new StatusBuffer(Guid.NewGuid().ToString("N"));
        var wire = Encoding.UTF8.GetBytes("AUTH 13\nAGENT-EXITED\nHOME 13\nAGENT-EXITED\nLP-DIAG AGENT-EXITED\nidle\n");
        foreach (var value in wire) buffer.Push([value], 1);
        Assert.False(buffer.AgentExited);
        Assert.Empty(buffer.TakeAcceptedEvents());
        Assert.Equal("idle", buffer.Activity);
        var marker = Encoding.ASCII.GetBytes("AGENT-EXITED\n");
        foreach (var value in marker) buffer.Push([value], 1);
        Assert.True(buffer.AgentExited);
        Assert.Empty(buffer.TakeAcceptedEvents());
        Assert.Equal(AgentActivitySnapshot.Unavailable, buffer.ActivitySnapshot);
    }

    [Fact]
    public async Task ActualExitStartsReturnBeforeTerminalEofAndSurvivesDisconnect()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var host = new TcpClient();
        var connecting = host.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var guest = await listener.AcceptTcpClientAsync(deadline.Token);
        await connecting;
        using var link = new StatusLink(host);
        try
        {
            var waiting = FenceSession.WaitForConsole(root, Environment.ProcessId, link);
            await guest.GetStream().WriteAsync("idle\n"u8.ToArray(), deadline.Token);
            await Task.Delay(250, deadline.Token);
            Assert.False(waiting.IsCompleted);
            await guest.GetStream().WriteAsync("AGENT-EX"u8.ToArray(), deadline.Token);
            await Task.Delay(250, deadline.Token);
            Assert.False(waiting.IsCompleted);
            await guest.GetStream().WriteAsync("ITED\n"u8.ToArray(), deadline.Token);
            Assert.True(await waiting.WaitAsync(deadline.Token));
            Assert.False(File.Exists(Path.Combine(root, "console.done")));
            guest.Dispose();
            while (link.Snapshot.Connected) await Task.Delay(25, deadline.Token);
            Assert.True(link.AgentExited);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
