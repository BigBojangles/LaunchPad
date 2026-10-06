using System.Text.Json;
using Xunit;

namespace LaunchPad.Tests;

public sealed class SerialMarkerObservationTests
{
    [Fact]
    public async Task LiveMarkerReturnsWithoutCancellationEvidence()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "serial.log");
        await File.WriteAllTextAsync(path, "FILES-END\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await GuestBaselineTests.WaitForSerialMarker(path, "FILES-END", deadline.Token);
        Assert.Empty(Directory.GetFiles(root, "serial-marker-cancelled-*-private.json"));
    }

    [Fact]
    public async Task LateStoppedLogDoesNotRewriteLastLiveObservation()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "serial.log");
        await File.WriteAllTextAsync(path, "FILE earlier.dat 1\n");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GuestBaselineTests.WaitForSerialMarker(path, "FILES-END", deadline.Token, new { stage = "owned-control" }));
        var diagnosticPath = Assert.Single(Directory.GetFiles(root, "serial-marker-cancelled-*-private.json"));
        var diagnosticBytes = await File.ReadAllTextAsync(diagnosticPath);
        await File.AppendAllTextAsync(path, "FILES-END\n");
        using var diagnostic = JsonDocument.Parse(diagnosticBytes);
        var data = diagnostic.RootElement;
        Assert.False(data.GetProperty("markerObservedBeforeCancellation").GetBoolean());
        Assert.True(data.GetProperty("readCount").GetInt32() > 0);
        Assert.Contains("earlier.dat", data.GetProperty("lastTail").GetString());
        Assert.DoesNotContain("FILES-END", data.GetProperty("lastTail").GetString());
        Assert.Equal("owned-control", data.GetProperty("context").GetProperty("stage").GetString());
        Assert.Equal(diagnosticBytes, await File.ReadAllTextAsync(diagnosticPath));
        Assert.Contains("FILES-END", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LiveSharingErrorsAreRetainedRatherThanHidden()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "serial.log");
        await File.WriteAllTextAsync(path, "FILES-END\n");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAsync<OperationCanceledException>(() => GuestBaselineTests.WaitForSerialMarker(path, "FILES-END", deadline.Token));
        using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(
            Assert.Single(Directory.GetFiles(root, "serial-marker-cancelled-*-private.json"))));
        var data = diagnostic.RootElement;
        Assert.True(data.GetProperty("ioErrors").GetInt32() > 0);
        Assert.Equal(0, data.GetProperty("readCount").GetInt32());
        Assert.False(data.GetProperty("markerObservedBeforeCancellation").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, data.GetProperty("lastErrorHresult").ValueKind);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "serial-marker-control-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }
}
