using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class GrokUpdateProjectionTests
{
    [Fact]
    public void RetainedRealStreamProjectsMatchedTurnWithoutMakingHistoryLive()
    {
        using var discovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(GuestBaselineTests.RepositoryRoot(),
            "tests/LaunchPad.Tests/TestResults/migration/activity-native-20261006/grok-ui-source-discovery-private.json")));
        var fixture = new Fixture("540918ec-13ff-4364-b87d-e69dfbcc046d");
        File.Copy(discovery.RootElement.GetProperty("source").GetString()!, fixture.Source, true);
        var capture = fixture.Capture.Poll(); fixture.Projection.Ingest(capture, fixture.Capture.LiveCapturedIds);
        var state = fixture.Projection.Read();
        Assert.False(state.Activity.RunActive); Assert.Equal(AgentActivity.Idle, state.Activity.State);
        Assert.Single(state.Pending, row => row.Event.Kind == AgentEventKind.RunFinished);
        Assert.DoesNotContain(state.Pending, row => row.Event.Kind == AgentEventKind.NeedsAttention);
        Assert.All(state.Pending, row => Assert.Null(row.Consent)); Assert.False(fixture.Projection.IsCurrent(state));
        Assert.Equal(0, fixture.ConsentReads); Assert.False(NativeGrokActivity.NativeRunLifecycleVerified);
        Assert.False(NativeGrokActivity.NativeOutcomeAlertsVerified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCursorCommitReopensWithOriginalConsentAndNoNewFreshness(bool initiallyEnabled)
    {
        var fixture = new Fixture(); fixture.Consent = fixture.Consent with { GlobalEnabled = initiallyEnabled };
        fixture.Capture.Poll(); fixture.Append("start", "user_prompt_submit"); fixture.Append("end", "turn_completed");
        var original = fixture.Consent;
        using (var held = new FileStream(fixture.CaptureState, FileMode.Open, FileAccess.Read, FileShare.Read))
            AssertWriteFailure(() => fixture.Capture.Poll());
        Assert.Equal(2, fixture.ConsentReads);
        fixture.Consent = fixture.Consent with { GlobalEnabled = true, Epoch = "new-opt-in" };
        fixture.Capture = fixture.RecreateCapture();
        var captured = fixture.Capture.Poll(); Assert.Equal(2, fixture.ConsentReads); Assert.Empty(fixture.Capture.LiveCapturedIds);
        Assert.All(captured.Pending, row => Assert.Equal(original, row.CapturedConsent));
        fixture.Projection.Ingest(captured, fixture.Capture.LiveCapturedIds);
        Assert.False(fixture.Projection.IsCurrent(fixture.Projection.Read()));
        fixture.Projection.Publish(true, fixture.Context, fixture.Outbox, _ => { });
        if (initiallyEnabled) Assert.Equal(original.Epoch, Assert.Single(fixture.Outbox.Read()).ConsentEpoch);
        else Assert.Empty(fixture.Outbox.Read());
    }

    [Fact]
    public void FailedAttemptJournalWriteKeepsFirstConsentInTheSameOwner()
    {
        var fixture = new Fixture(); fixture.Capture.Poll(); fixture.Append("start", "user_prompt_submit");
        var attemptPath = Path.Combine(fixture.Directory, GrokUpdateCapture.AttemptFile);
        using (var held = new FileStream(attemptPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            AssertWriteFailure(() => fixture.Capture.Poll());
        var first = fixture.Consent; fixture.Consent = first with { Epoch = "later" };
        var captured = fixture.Capture.Poll();
        Assert.Equal(1, fixture.ConsentReads); Assert.Equal(first, Assert.Single(captured.Pending).CapturedConsent);
        Assert.Empty(fixture.Capture.LiveCapturedIds);
    }

    [Fact]
    public void StaleAndFutureEventsNeverAcquireSettingsConsent()
    {
        var fixture = new Fixture(); fixture.Append("old", "user_prompt_submit", occurred: fixture.Time.Now.AddHours(-1));
        fixture.Append("future", "turn_completed", occurred: fixture.Time.Now.AddMinutes(1));
        var captured = fixture.Capture.Poll(); fixture.Projection.Ingest(captured, fixture.Capture.LiveCapturedIds);
        Assert.Equal(0, fixture.ConsentReads); Assert.All(captured.Pending, row => { Assert.False(row.LiveEligible); Assert.Null(row.CapturedConsent); });
        Assert.False(fixture.Projection.IsCurrent(fixture.Projection.Read()));
        fixture.Projection.Publish(true, fixture.Context, fixture.Outbox, _ => { }); Assert.Empty(fixture.Outbox.Read());
    }

    [Fact]
    public void StopLateForeignCompletionAndGeneratedWakeupCannotFinishCurrentPrompt()
    {
        var fixture = new Fixture(); var newer = Guid.NewGuid().ToString("D");
        fixture.Append("start", "user_prompt_submit"); fixture.Append("stop9", "stop");
        fixture.Ingest(); Assert.True(fixture.Projection.Read().Activity.RunActive);
        Assert.DoesNotContain(fixture.Projection.Read().Pending, row => row.Event.Kind == AgentEventKind.RunFinished);
        fixture.Append("new", "user_prompt_submit", newer); fixture.Append("late-old-end", "turn_completed");
        fixture.Append("unbound-wakeup", "user_prompt_submit", "task-completed-" + Guid.NewGuid().ToString("D"));
        fixture.Append("foreign-message", "agent_message_chunk"); fixture.Ingest();
        var state = fixture.Projection.Read(); Assert.True(state.Activity.RunActive); Assert.Equal(newer, state.Activity.RunId);
        Assert.False(state.HistoryComplete); Assert.DoesNotContain(state.Pending, row => row.Event.Kind == AgentEventKind.RunFinished);
        fixture.Append("current-end", "turn_completed", newer); fixture.Ingest();
        var end = Assert.Single(fixture.Projection.Read().Pending, row => row.Event.Kind == AgentEventKind.RunFinished);
        Assert.Equal(newer, end.Event.RunId);
        Assert.False(fixture.RecreateProjection().Read().HistoryComplete);
    }

    [Theory]
    [InlineData("end_turn", AgentEventKind.RunFinished)]
    [InlineData("error", AgentEventKind.RunFailed)]
    [InlineData("cancelled", AgentEventKind.Interrupted)]
    [InlineData("max_tokens", AgentEventKind.Interrupted)]
    public void MatchedCompletionRetainsObservedOutcome(string reason, AgentEventKind expected)
    {
        var fixture = new Fixture(); fixture.Append("start", "user_prompt_submit"); fixture.Append("end", "turn_completed", reason: reason);
        fixture.Ingest(); Assert.Equal(expected, fixture.Projection.Read().Activity.LastEvent!.Kind);
        Assert.False(fixture.Projection.Read().Activity.RunActive);
    }

    [Fact]
    public void PartialPublicationRetryKeepsConsentAndQueuesOneSemanticNotification()
    {
        var fixture = new Fixture(); fixture.Append("start", "user_prompt_submit"); fixture.Append("end", "turn_completed"); fixture.Ingest();
        var original = fixture.Consent; fixture.Consent = original with { Epoch = "new" };
        var ledgerPath = Path.Combine(fixture.Directory, GrokUpdateProjection.StateFile);
        using (var held = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            AssertWriteFailure(() => fixture.Projection.Publish(true, fixture.Context, fixture.Outbox, _ => { }));
        Assert.Equal(original.Epoch, Assert.Single(fixture.Outbox.Read()).ConsentEpoch);
        var reopened = fixture.RecreateProjection(); Assert.False(reopened.IsCurrent(reopened.Read()));
        reopened.Publish(true, fixture.Context, fixture.Outbox, _ => { });
        Assert.Single(fixture.Outbox.Read()); Assert.Empty(reopened.Read().Pending); Assert.Equal(2, fixture.ConsentReads);
    }

    [Fact]
    public void ProjectionFailureCannotAdvanceCaptureAcknowledgement()
    {
        var fixture = new Fixture(); fixture.Projection.Ingest(fixture.Capture.Poll());
        fixture.Append("start", "user_prompt_submit"); var captured = fixture.Capture.Poll();
        var path = Path.Combine(fixture.Directory, GrokUpdateProjection.StateFile); var before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            AssertWriteFailure(() => fixture.Projection.Ingest(captured, fixture.Capture.LiveCapturedIds));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Single(fixture.Capture.Read().Pending);
        fixture.Projection.Ingest(captured); Assert.True(fixture.Capture.Acknowledge(captured.Pending.Select(row => row.EventId).ToArray()));
        fixture.Projection.Ingest(captured); Assert.Single(fixture.Projection.Read().Pending);
    }

    [Fact]
    public void UnverifiedShadowDoesNotAccumulateOrPublishNativeRunEvents()
    {
        var fixture = new Fixture(); fixture.Capture = fixture.RecreateCapture(allowLive: false);
        for (var i = 0; i < 140; i++)
        {
            var prompt = Guid.NewGuid().ToString("D"); fixture.Append("start-" + i, "user_prompt_submit", prompt);
            fixture.Append("end-" + i, "turn_completed", prompt);
            var captured = fixture.Capture.Poll(); fixture.Projection.Ingest(captured, fixture.Capture.LiveCapturedIds, retainEvents: false);
            Assert.True(fixture.Capture.Acknowledge(captured.Pending.Select(row => row.EventId).ToArray()));
        }
        Assert.Empty(fixture.Projection.Read().Pending); Assert.Empty(fixture.Outbox.Read()); Assert.Equal(0, fixture.ConsentReads);
        Assert.False(fixture.Projection.IsCurrent(fixture.Projection.Read()));
    }

    [Fact]
    public void WrongBindingAndCorruptionPreserveTheProjection()
    {
        var fixture = new Fixture(); fixture.Append("start", "user_prompt_submit"); fixture.Ingest();
        var path = Path.Combine(fixture.Directory, GrokUpdateProjection.StateFile); var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => new GrokUpdateProjection(fixture.Directory,
            fixture.Binding with { Generation = Guid.NewGuid().ToString("N") }).Read()); Assert.Equal(before, File.ReadAllBytes(path));
        File.WriteAllText(path, "{owned-corrupt"); Assert.Throws<InvalidDataException>(() => fixture.RecreateProjection().Read());
        Assert.Equal("{owned-corrupt", File.ReadAllText(path));
    }

    private static void AssertWriteFailure(Action action) => Assert.True(Record.Exception(action) is IOException or UnauthorizedAccessException);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests/LaunchPad.Tests/TestResults/migration/grok-projection-20261007/owned-" + Guid.NewGuid().ToString("N"));
        public string Directory { get; }
        public string Source { get; }
        public string CaptureState => Path.Combine(Directory, GrokUpdateCapture.StateFile);
        public GrokActivityBinding Binding { get; }
        public GrokUpdateCapture Capture { get; set; }
        public GrokUpdateProjection Projection { get; }
        public Clock Time { get; } = new();
        public NotificationConsent Consent { get; set; } = new(true, true, true, Guid.NewGuid().ToString("N"), "original-epoch");
        public int ConsentReads { get; private set; }
        public NotificationOutbox Outbox { get; }
        public SessionActivityContext Context => new(Directory, Binding.Generation, Binding.HostProject, AgentChoice.Grok);
        private readonly string _prompt = Guid.NewGuid().ToString("D");
        public Fixture(string? session = null)
        {
            Directory = Path.Combine(Root, "projection");
            Binding = new(Guid.NewGuid().ToString("N"), session ?? Guid.NewGuid().ToString("D"), Path.Combine(Root, "project"), Path.Combine(Root, "project"), "1.0.46");
            Source = Path.Combine(Root, "source", Binding.RootSessionId, "updates.jsonl");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Source)!); File.WriteAllText(Source, "");
            Capture = RecreateCapture(); Projection = RecreateProjection(); Outbox = new(Path.Combine(Root, "outbox"));
        }
        public GrokUpdateCapture RecreateCapture(bool allowLive = true) => new(Directory, Binding, Source, Time,
            () => { ConsentReads++; return Consent; }, allowLive);
        public GrokUpdateProjection RecreateProjection() => new(Directory, Binding, Time);
        public void Ingest()
        {
            var capture = Capture.Poll(); Projection.Ingest(capture, Capture.LiveCapturedIds);
            Assert.True(Capture.Acknowledge(capture.Pending.Select(row => row.EventId).ToArray()));
        }
        public void Append(string id, string kind, string? prompt = null, string reason = "end_turn", DateTimeOffset? occurred = null)
        {
            var hook = kind is "user_prompt_submit" or "stop";
            File.AppendAllText(Source, JsonSerializer.Serialize(new { method = hook || kind == "turn_completed" ? "_x.ai/session/update" : "session/update",
                @params = new { sessionId = Binding.RootSessionId, update = new { sessionUpdate = hook ? "hook_execution" : kind,
                    event_name = kind, prompt_id = prompt ?? _prompt, stop_reason = reason, elapsed_ms = 1, content = "PRIVATE-TRANSCRIPT" },
                    _meta = new { eventId = id, agentTimestampMs = (occurred ?? Time.Now).ToUnixTimeMilliseconds() } } }) + "\n");
        }
    }
}
