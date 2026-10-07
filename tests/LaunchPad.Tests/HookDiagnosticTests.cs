using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class HookDiagnosticTests
{
    private static HookDiagnostic Record() => new(1, new string('a', 32), 1000, "Notification",
        new() { ["message"] = "str", ["notificationType"] = "str" }, null, null,
        "permission_prompt", null, -1, -1, true);
    private static string Line(HookDiagnostic record) => HookDiagnostics.Prefix + JsonSerializer.Serialize(record);

    [Fact]
    public void FragmentedDiagnosticsCannotBecomeActivityOrConsumeAuthHomeBodies()
    {
        var line = Line(Record());
        var wire = Encoding.UTF8.GetBytes("AUTH " + line.Length + "\n" + line + "HOME 3\nabc" + line + "\nSIZE-OK 30 80\n");
        var buffer = new StatusBuffer(Guid.NewGuid().ToString("N"));
        foreach (var part in wire.Chunk(7)) buffer.Push(part, part.Length);
        Assert.Equal(line, Encoding.UTF8.GetString(buffer.AuthBody));
        Assert.Equal("abc", Encoding.UTF8.GetString(buffer.HomeBody));
        Assert.Single(buffer.TakeDiagnostics());
        Assert.Empty(buffer.TakeAcceptedEvents());
        Assert.Equal(0, buffer.ActivitySnapshot.LastSequence);
        Assert.True(buffer.SizeAccepted);
    }

    [Fact]
    public void ProjectionRejectsFreeTextTokensAndNeverLogsUnknownRawFields()
    {
        Assert.Null(HookDiagnostics.Decode(Line(Record() with { NotificationType = "secret prompt" })));
        Assert.Null(HookDiagnostics.Decode(Line(Record() with { Fields = new() { ["secret"] = "str" } })));
        var raw = Line(Record());
        raw = raw[..^1] + ",\"toolInput\":\"sensitive command\"}";
        var parsed = HookDiagnostics.Decode(raw);
        Assert.NotNull(parsed);
        Assert.DoesNotContain("sensitive command", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void DiagnosticQueueIsBoundedWithoutBlockingRequiredMessages()
    {
        var buffer = new StatusBuffer(Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(Line(Record()) + "\n", 100)) + "AUTH 0\nHOME 0\n");
        buffer.Push(bytes, bytes.Length);
        Assert.Equal(64, buffer.TakeDiagnostics().Length);
        Assert.True(buffer.AuthFinished);
        Assert.True(buffer.HomeFinished);
        Assert.Empty(buffer.TakeAcceptedEvents());
    }
}
