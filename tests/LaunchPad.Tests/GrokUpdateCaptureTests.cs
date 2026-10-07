using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class GrokUpdateCaptureTests
{
    [Fact]
    public void RetainedActualStreamKeepsFileOrderAndDropsTranscriptPayload()
    {
        var discoveryPath = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests/LaunchPad.Tests/TestResults/migration/activity-native-20261006/grok-ui-source-discovery-private.json");
        using var discovery = JsonDocument.Parse(File.ReadAllText(discoveryPath));
        var source = discovery.RootElement.GetProperty("source").GetString()!;
        Assert.Equal(discovery.RootElement.GetProperty("sourceSha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant());
        var fixture = new Fixture("540918ec-13ff-4364-b87d-e69dfbcc046d"); File.Copy(source, fixture.Source, true);
        var state = fixture.Capture.Poll();
        Assert.Equal(6, state.Pending.Length); Assert.True(state.HistoryComplete);
        var ids = state.Pending.Select(row => row.EventId).ToArray();
        Assert.True(Array.IndexOf(ids, fixture.Binding.RootSessionId + "-9") < Array.IndexOf(ids, fixture.Binding.RootSessionId + "-6"));
        var completed = Assert.Single(state.Pending.Where(row => row.Kind == "turn_completed"));
        Assert.Equal("end_turn", completed.StopReason); Assert.NotNull(completed.PromptId);
        var json = File.ReadAllText(fixture.State);
        Assert.DoesNotContain("content", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("runs", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(NativeGrokActivity.NativeRunLifecycleVerified); Assert.False(NativeGrokActivity.NativeOutcomeAlertsVerified);
        Assert.Equal(state.Cursor, fixture.Recreate().Poll().Cursor); Assert.Equal(state.Pending, fixture.Recreate().Read().Pending);
    }

    [Fact]
    public void PartialUtf8IsRereadAndOnlyMetadataCommits()
    {
        var fixture = new Fixture(); var bytes = Encoding.UTF8.GetBytes(fixture.Line("event-a", secret: "PRIVATE-SECRET-é"));
        var split = Array.IndexOf(bytes, (byte)0xC3) + 1;
        File.WriteAllBytes(fixture.Source, bytes[..split]); var partial = fixture.Capture.Poll();
        Assert.Equal(0, partial.Cursor); Assert.Empty(partial.Pending); Assert.DoesNotContain("PRIVATE", File.ReadAllText(fixture.State));
        using (var append = new FileStream(fixture.Source, FileMode.Append)) { append.Write(bytes.AsSpan(split)); append.WriteByte((byte)'\n'); }
        var state = fixture.Recreate().Poll(); Assert.Single(state.Pending); Assert.Equal(bytes.Length + 1, state.Cursor);
        Assert.DoesNotContain("PRIVATE", File.ReadAllText(fixture.State));
    }

    [Fact]
    public void AtomicWriteFailureCannotSkipMetadataOnRetry()
    {
        var fixture = new Fixture(); fixture.Capture.Poll(); var before = File.ReadAllBytes(fixture.State);
        File.AppendAllText(fixture.Source, fixture.Line("owned-retry") + "\n");
        using (var held = new FileStream(fixture.State, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => fixture.Capture.Poll());
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(before, File.ReadAllBytes(fixture.State));
        var pending = fixture.Recreate().Poll().Pending; Assert.Single(pending); Assert.Equal("owned-retry", pending[0].EventId);
        Assert.Equal(pending, fixture.Recreate().Poll().Pending);
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("replace")]
    [InlineData("prefix")]
    public void ChangedStreamRefusesAndPreservesCommittedState(string change)
    {
        var fixture = new Fixture(); File.WriteAllText(fixture.Source, fixture.Line("original") + "\n"); fixture.Capture.Poll();
        var original = File.ReadAllBytes(fixture.State);
        if (change == "truncate") File.WriteAllText(fixture.Source, "");
        else if (change == "replace") { var bytes = File.ReadAllBytes(fixture.Source); File.Move(fixture.Source, fixture.Source + ".retained"); File.WriteAllBytes(fixture.Source, bytes); }
        else { using var edit = new FileStream(fixture.Source, FileMode.Open, FileAccess.Write); edit.WriteByte((byte)'x'); }
        Assert.Throws<InvalidDataException>(() => fixture.Recreate().Poll()); Assert.Equal(original, File.ReadAllBytes(fixture.State));
    }

    [Fact]
    public void OversizedPartialLineDiscardsInBoundsWithoutStoringItsText()
    {
        var fixture = new Fixture(); File.WriteAllText(fixture.Source, new string('x', GrokUpdateCapture.MaxLineBytes + 100));
        var first = fixture.Capture.Poll(); Assert.True(first.DiscardingOversizedLine); Assert.False(first.HistoryComplete); Assert.Empty(first.Pending);
        Assert.True(new FileInfo(fixture.State).Length < 4096);
        File.AppendAllText(fixture.Source, "\n" + fixture.Line("after-oversize") + "\n");
        var next = fixture.Recreate().Poll(); Assert.False(next.DiscardingOversizedLine); Assert.Single(next.Pending); Assert.False(next.HistoryComplete);
    }

    [Fact]
    public void DuplicateMetadataIsDedupedButConflictingIdentityPreservesCursor()
    {
        var fixture = new Fixture(); var first = fixture.Line("id-9", "stop");
        File.WriteAllText(fixture.Source, first + "\n" + fixture.Line("id-6", "user_prompt_submit") + "\n" + first + "\n");
        var state = fixture.Capture.Poll(); Assert.Equal(new[] { "id-9", "id-6" }, state.Pending.Select(row => row.EventId));
        var before = File.ReadAllBytes(fixture.State); File.AppendAllText(fixture.Source, fixture.Line("id-9", "stop_failure") + "\n");
        Assert.Throws<InvalidDataException>(() => fixture.Recreate().Poll()); Assert.Equal(before, File.ReadAllBytes(fixture.State));
    }

    [Fact]
    public void BackpressureAndPrefixAcknowledgementDoNotLoseTheNextEvent()
    {
        var fixture = new Fixture(); File.WriteAllText(fixture.Source, string.Concat(Enumerable.Range(0, GrokUpdateCapture.Capacity + 1).Select(i => fixture.Line("event-" + i) + "\n")));
        var state = fixture.Capture.Poll(); Assert.Equal(GrokUpdateCapture.Capacity, state.Pending.Length); Assert.True(state.Cursor < new FileInfo(fixture.Source).Length);
        Assert.False(fixture.Capture.Acknowledge(["wrong-prefix"]));
        Assert.True(fixture.Recreate().Acknowledge(state.Pending.Select(row => row.EventId).ToArray()));
        var next = fixture.Recreate().Poll(); Assert.Single(next.Pending); Assert.Equal("event-128", next.Pending[0].EventId);
    }

    [Fact]
    public void ForeignIgnoredContentAndUnknownSchemaMarkIncompleteWithoutCreatingEvents()
    {
        var fixture = new Fixture();
        var ignored = fixture.Line("foreign-content", "user_message_chunk").Replace(fixture.Binding.RootSessionId, Guid.NewGuid().ToString("D"));
        File.WriteAllText(fixture.Source, ignored + "\n"); var state = fixture.Capture.Poll(); Assert.False(state.HistoryComplete); Assert.Empty(state.Pending);
        Assert.False(GrokUpdateCapture.TryProject(fixture.Binding, Encoding.UTF8.GetBytes(fixture.Line("bad") .Replace("\"sessionId\":", "\"sessionId\":\"wrong\",\"sessionId\":")), out _));
        Assert.False(GrokUpdateCapture.TryProject(fixture.Binding with { CliVersion = "unknown" }, Encoding.UTF8.GetBytes(fixture.Line("bad")), out _));
    }

    [Fact]
    public void SelfConsistentFingerprintCannotLegitimizeImpossibleSavedMetadata()
    {
        var fixture = new Fixture(); File.WriteAllText(fixture.Source, fixture.Line("original") + "\n"); var state = fixture.Capture.Poll();
        var impossible = state.Pending[0] with { Kind = "turn_completed", PromptId = null, StopReason = "end_turn", ElapsedMs = 1 };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(impossible)));
        var corrupt = state with { Pending = [impossible], Recent = [new(impossible.EventId, fingerprint)] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(corrupt, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllBytes(fixture.State, bytes);
        Assert.Throws<InvalidDataException>(() => fixture.Recreate().Read()); Assert.Equal(bytes, File.ReadAllBytes(fixture.State));
    }

    [Fact]
    public void WrongBindingAndCorruptStateNeverResetTheCursor()
    {
        var fixture = new Fixture(); File.WriteAllText(fixture.Source, fixture.Line("original") + "\n"); fixture.Capture.Poll();
        var before = File.ReadAllBytes(fixture.State);
        var wrong = new GrokUpdateCapture(fixture.Directory, fixture.Binding with { Generation = Guid.NewGuid().ToString("N") }, fixture.Source);
        Assert.Throws<InvalidDataException>(() => wrong.Read()); Assert.Equal(before, File.ReadAllBytes(fixture.State));
        File.WriteAllText(fixture.State, "{malformed-owned-state");
        Assert.Throws<InvalidDataException>(() => fixture.Recreate().Poll()); Assert.Equal("{malformed-owned-state", File.ReadAllText(fixture.State));
    }

    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests/LaunchPad.Tests/TestResults/migration/grok-updates-20261007/owned-" + Guid.NewGuid().ToString("N"));
        public string Directory { get; }
        public string Source { get; }
        public string State => Path.Combine(Directory, GrokUpdateCapture.StateFile);
        public GrokActivityBinding Binding { get; }
        public GrokUpdateCapture Capture { get; }
        public Fixture(string? session = null)
        {
            Directory = Path.Combine(Root, "host-capture");
            Binding = new(Guid.NewGuid().ToString("N"), session ?? Guid.NewGuid().ToString("D"), Path.Combine(Root, "project"), Path.Combine(Root, "project"), "1.0.46");
            Source = Path.Combine(Root, "grok-home", "sessions", Binding.RootSessionId, "updates.jsonl");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Source)!); File.WriteAllBytes(Source, []);
            Capture = Recreate();
        }
        public GrokUpdateCapture Recreate() => new(Directory, Binding, Source);
        public string Line(string id, string kind = "user_prompt_submit", string secret = "PRIVATE-TRANSCRIPT")
        {
            var hook = kind is "user_prompt_submit" or "stop" or "stop_failure";
            return JsonSerializer.Serialize(new { method = hook ? "_x.ai/session/update" : "session/update",
                @params = new { sessionId = Binding.RootSessionId, update = new { sessionUpdate = hook ? "hook_execution" : kind,
                    event_name = kind, prompt_id = "a5dca8d3-ad64-43c8-8890-ff1e2561433a", runs = new[] { new { command = secret } }, content = secret },
                    _meta = new { eventId = id, agentTimestampMs = 1791340766449L } } });
        }
    }
}
