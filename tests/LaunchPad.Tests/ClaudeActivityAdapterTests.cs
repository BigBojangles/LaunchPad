using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class ClaudeActivityAdapterTests
{
    private const string PromptA = "073c0c78-82d8-4f92-9a10-81739bb0f9f4";
    private const string PromptB = "415a6079-b6c2-4273-9a2c-3a6dd0c7cc2c";

    [Fact]
    public void SubmissionIsProvisionalAndConfirmedWaitingNeverBecomesAnOutcomeAlert()
    {
        var f = new Fixture();
        Assert.True(f.Adapter.Ingest(f.Capture("SessionStart", source: "startup"), live: true));
        Assert.Equal(AgentActivity.Idle, f.Adapter.Current().State);
        Assert.True(f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptA), live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.False(f.Adapter.Snapshot.RunActive);
        Assert.True(f.Adapter.Ingest(f.Capture("PreToolUse", PromptA, tool: "toolu_first"), live: true));
        Assert.Equal(AgentActivity.Working, f.Adapter.Current().State);
        Assert.True(f.Adapter.Ingest(f.Capture("PermissionRequest", PromptA), live: true));
        Assert.Equal(AgentActivity.Working, f.Adapter.Snapshot.State); // Request alone is not a displayed question.
        var waiting = f.Capture("Notification", PromptA, notification: "permission_prompt");
        Assert.True(f.Adapter.Ingest(waiting, live: true));
        Assert.Equal(AgentActivity.NeedsAttention, f.Adapter.Current().State);
        Assert.Null(f.Adapter.Snapshot.LastEvent); // State observations cannot queue notifications.
        Assert.True(f.Adapter.Ingest(f.Capture("PostToolUse", PromptA, tool: "toolu_parallel"), live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.Equal(AgentActivity.NeedsAttention, f.Adapter.Snapshot.State);
        Assert.True(f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptB), live: true));
        Assert.True(f.Adapter.Ingest(f.Capture("PreToolUse", PromptB, tool: "toolu_second"), live: true));
        Assert.Equal(PromptB, f.Adapter.Current().RunId);
        Assert.Contains(PromptA, f.Adapter.Snapshot.RetiredRunIds!);
        Assert.Null(f.Adapter.Snapshot.LastEvent);
        Assert.True(f.Adapter.Ingest(f.Capture("Stop", PromptB), live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.True(f.Adapter.Snapshot.RunActive);
        Assert.Null(f.Adapter.Snapshot.LastEvent);
    }

    [Fact]
    public void NewerProvisionalPromptPreventsLateOlderCallbacksFromRenewingState()
    {
        var f = new Fixture();
        f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptA), live: true);
        f.Adapter.Ingest(f.Capture("PreToolUse", PromptA, tool: "toolu_first"), live: true);
        Assert.Equal(AgentActivity.Working, f.Adapter.Current().State);
        f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptB), live: true);
        foreach (var kind in new[] { "PreToolUse", "PostToolUse", "PostToolUseFailure" })
        {
            Assert.True(f.Adapter.Ingest(f.Capture(kind, PromptA, tool: "toolu_late"), live: true));
            Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        }
        Assert.True(f.Adapter.Ingest(f.Capture("Notification", PromptA, notification: "permission_prompt"), live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.Equal(PromptA, f.Adapter.Snapshot.RunId); // Historical snapshot, no revived freshness.
        Assert.True(f.Adapter.Ingest(f.Capture("PostToolUse", PromptB, tool: "toolu_second"), live: true));
        Assert.Equal(PromptB, f.Adapter.Current().RunId);
    }

    [Fact]
    public void RestoredStateCannotContainLifecycleEventsOrInventedRunIdentifiers()
    {
        var f = new Fixture();
        f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptA));
        f.Adapter.Ingest(f.Capture("PreToolUse", PromptA, tool: "toolu_first"));
        var checkpoint = f.Adapter.Checkpoint;
        var forgedEvent = new AgentActivityEvent(1, f.Binding.Generation, 1, "invented-start",
            AgentEventKind.RunStarted, f.Time.GetUtcNow(), PromptA, AgentSessionId: f.Binding.RootSessionId);
        Assert.Throws<ArgumentException>(() => new ClaudeActivityAdapter(f.Binding,
            checkpoint with { Activity = checkpoint.Activity with { LastEvent = forgedEvent } }));
        var observation = checkpoint.Activity.Observation! with { RunId = "invented-non-prompt" };
        Assert.Throws<ArgumentException>(() => new ClaudeActivityAdapter(f.Binding,
            checkpoint with { Activity = checkpoint.Activity with { RunId = observation.RunId, Observation = observation } }));
    }

    [Fact]
    public void FutureDatedObservationCannotBecomeLiveWhenTimeCatchesUp()
    {
        var f = new Fixture();
        f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptA), live: true);
        var future = f.Capture("PreToolUse", PromptA, tool: "toolu_future")
            with { CapturedUtc = f.Time.GetUtcNow().AddSeconds(1) };
        Assert.True(f.Adapter.Ingest(future, live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        f.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.True(f.Adapter.Ingest(f.Capture("PostToolUse", PromptA, tool: "toolu_actual"), live: true));
        Assert.Equal(AgentActivity.Working, f.Adapter.Current().State);
    }

    [Fact]
    public void ReplayReopenExpiredOrDisconnectedDataCannotRenewGreen()
    {
        var f = new Fixture();
        f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptA), live: true);
        var tool = f.Capture("PreToolUse", PromptA, tool: "toolu_live");
        Assert.True(f.Adapter.Ingest(tool, live: true));
        Assert.Equal(AgentActivity.Working, f.Adapter.Current().State);
        f.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.False(f.Adapter.Ingest(tool, live: true));
        Assert.False(f.Adapter.Ingest(tool with { Sequence = tool.Sequence + 10 }, live: true));
        var reopened = new ClaudeActivityAdapter(f.Binding, f.Adapter.Checkpoint, f.Time);
        Assert.Equal(AgentActivity.Unknown, reopened.Current().State);
        Assert.False(reopened.Ingest(tool, live: true));
        var historical = f.Capture("PostToolUse", PromptA, tool: "toolu_live");
        Assert.True(reopened.Ingest(historical));
        Assert.Equal(AgentActivity.Unknown, reopened.Current().State);
        var tooOld = f.Capture("PreToolUse", PromptA, tool: "toolu_old") with { CapturedUtc = f.Time.GetUtcNow().AddSeconds(-10) };
        Assert.True(reopened.Ingest(tooOld, live: true));
        Assert.Equal(AgentActivity.Unknown, reopened.Current().State);
        Assert.Throws<ArgumentException>(() => new ClaudeActivityAdapter(f.Binding with { RootSessionId = "another-session" }, f.Adapter.Checkpoint));
    }

    [Fact]
    public void WrongRootProjectGenerationMalformedIdsAndDuplicateKeysAreRefused()
    {
        var f = new Fixture();
        var input = f.Input("UserPromptSubmit", PromptA);
        foreach (var (key, value) in new (string, object?)[]
        {
            ("session_id", "child-session"), ("cwd", f.Binding.Project + "-other"),
            ("agent_id", "child"), ("agent_id", null), ("parent_session_id", "another-session"),
            ("prompt_id", "not-a-prompt-uuid"), ("prompt_id", null), ("hook_event_name", "SubagentStop")
        })
        {
            var altered = new Dictionary<string, object?>(input) { [key] = value };
            Assert.Null(f.TryCapture(altered));
        }
        input["agent_type"] = "custom-main-agent";
        Assert.NotNull(f.TryCapture(input));
        Assert.Null(ClaudeActivityAdapter.Capture("{\"cwd\":\"a\",\"cwd\":\"b\"}", f.Binding,
            Guid.NewGuid().ToString("N"), 1, f.Time.GetUtcNow()));
        Assert.Null(ClaudeActivityAdapter.Capture(new string(' ', ClaudeActivityAdapter.MaxInputBytes + 1), f.Binding,
            Guid.NewGuid().ToString("N"), 1, f.Time.GetUtcNow()));
        var receipt = f.Capture("UserPromptSubmit", PromptA);
        Assert.False(f.Adapter.Ingest(receipt with { Binding = f.Binding with { Generation = Guid.NewGuid().ToString("N") } }));
        Assert.False(f.Adapter.Ingest(receipt with { Metadata = receipt.Metadata with { NotificationType = "private free text" } }));
        Assert.Equal(0, f.Adapter.Checkpoint.Cursor);
    }

    [Fact]
    public void CaptureAndCheckpointExcludePromptTranscriptToolInputMessagesAndSecrets()
    {
        var f = new Fixture();
        var input = f.Input("UserPromptSubmit", PromptA);
        input["prompt"] = "PRIVATE-PROMPT";
        input["transcript_path"] = "PRIVATE-TRANSCRIPT-PATH";
        input["tool_input"] = new { command = "PRIVATE-COMMAND", password = "PRIVATE-SECRET" };
        input["message"] = "PRIVATE-NOTIFICATION";
        input["last_assistant_message"] = "PRIVATE-ANSWER";
        var receipt = f.TryCapture(input)!;
        Assert.NotNull(receipt);
        Assert.True(f.Adapter.Ingest(receipt, live: true));
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(receipt));
        Assert.DoesNotContain("PRIVATE-", JsonSerializer.Serialize(f.Adapter.Checkpoint));
        var restored = JsonSerializer.Deserialize<ClaudeActivityCheckpoint>(JsonSerializer.Serialize(f.Adapter.Checkpoint))!;
        Assert.Equal(f.Adapter.Checkpoint.Cursor, new ClaudeActivityAdapter(f.Binding, restored).Checkpoint.Cursor);
    }

    [Fact]
    public void UnboundToolIdleFailureAndClosedSessionCannotInventOrResumeAnOutcome()
    {
        var f = new Fixture();
        Assert.True(f.Adapter.Ingest(f.Capture("SessionStart", source: "resume"), live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.True(f.Adapter.Ingest(f.Capture("PreToolUse", PromptA, tool: "toolu_unbound"), live: true));
        Assert.Equal(AgentActivitySnapshot.Unavailable, f.Adapter.Snapshot);
        Assert.True(f.Adapter.Ingest(f.Capture("UserPromptSubmit", PromptA), live: true));
        Assert.True(f.Adapter.Ingest(f.Capture("PostToolUse", PromptA, tool: "toolu_bound"), live: true));
        Assert.True(f.Adapter.Ingest(f.Capture("Notification", PromptA, notification: "idle_prompt"), live: true));
        Assert.True(f.Adapter.Snapshot.RunActive);
        Assert.True(f.Adapter.Ingest(f.Capture("StopFailure", PromptA), live: true));
        Assert.Equal(AgentActivity.Unknown, f.Adapter.Current().State);
        Assert.Null(f.Adapter.Snapshot.LastEvent);
        Assert.True(f.Adapter.Ingest(f.Capture("SessionEnd", PromptA), live: true));
        var reopened = new ClaudeActivityAdapter(f.Binding, f.Adapter.Checkpoint, f.Time);
        Assert.False(reopened.Ingest(f.Capture("PreToolUse", PromptA, tool: "toolu_after_end"), live: true));
        Assert.Equal(AgentActivity.Unknown, reopened.Current().State);
    }

    private sealed class Fixture
    {
        public TestTime Time { get; } = new();
        public ClaudeActivityBinding Binding { get; } = new(Guid.NewGuid().ToString("N"), "owned-root-session",
            Path.Combine(Path.GetTempPath(), "LaunchPad-owned-Claude"));
        public ClaudeActivityAdapter Adapter { get; }
        private long _sequence;
        public Fixture() { Adapter = new(Binding, time: Time); }
        public Dictionary<string, object?> Input(string kind, string? prompt) => new()
        { ["hook_event_name"] = kind, ["session_id"] = Binding.RootSessionId, ["cwd"] = Binding.Project,
            ["prompt_id"] = prompt };
        public ClaudeActivityReceipt? TryCapture(Dictionary<string, object?> input)
        {
            if (input.TryGetValue("prompt_id", out var prompt) && prompt is null) input.Remove("prompt_id");
            return ClaudeActivityAdapter.Capture(JsonSerializer.Serialize(input), Binding, Guid.NewGuid().ToString("N"),
                ++_sequence, Time.GetUtcNow());
        }
        public ClaudeActivityReceipt Capture(string kind, string? prompt = null, string? tool = null,
            string? notification = null, string? source = null)
        {
            var input = Input(kind, prompt);
            if (tool is not null) input["tool_use_id"] = tool;
            if (notification is not null) input["notification_type"] = notification;
            if (source is not null) input["source"] = source;
            return TryCapture(input) ?? throw new InvalidOperationException("Owned callback was not captured.");
        }
    }
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan value) { _now += value; _ticks += value.Ticks; }
    }
}
