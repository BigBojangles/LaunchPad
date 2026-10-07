using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class GrokActivityFeedTests
{
    private const string Cwd = "/home/builder/in/project";
    private static string Input(string kind, string? run = null, object[]? tasks = null)
        => JsonSerializer.Serialize(new { hookEventName = kind, sessionId = "known-root", cwd = Cwd,
            workspaceRoot = Cwd, promptId = run, stopHookActive = false, backgroundTasks = tasks ?? [], sessionCrons = Array.Empty<object>(),
            prompt = "PRIVATE-PROMPT-NEVER-STORED", response = "PRIVATE-RESPONSE-NEVER-STORED" });

    [Fact]
    public void PendingBackgroundProvenanceAndOutcomesMoveTogetherAcrossReopen()
    {
        var fixture = Create();
        const string task = "01a113f1-3214-70d0-9549-4760e9fcf795";
        Assert.True(fixture.Feed.Ingest(Receipt(1, Input("UserPromptSubmit", "real-run"), fixture.Clock)));
        Assert.True(fixture.Feed.Ingest(Receipt(2, Input("Stop", "real-run", [new { id = task, type = "shell", status = "running", command = "PRIVATE-COMMAND" }]), fixture.Clock)));
        Assert.Contains(task, fixture.Feed.Read().Checkpoint.BackgroundTaskIds);
        var reopened = new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock);
        Assert.True(reopened.Ingest(Receipt(3, Input("UserPromptSubmit", "task-completed-" + task), fixture.Clock)));
        Assert.True(reopened.Ingest(Receipt(4, Input("Stop", "task-completed-" + task), fixture.Clock)));
        var state = reopened.Read();
        var ended = Assert.Single(state.Pending, item => item.Event.Kind == AgentEventKind.RunFinished);
        Assert.Equal("real-run", ended.Event.RunId);
        Assert.False(state.Checkpoint.Activity.RunActive);
        Assert.Empty(state.Checkpoint.BackgroundTaskIds);
        var json = File.ReadAllText(Path.Combine(fixture.Root, GrokActivityFeed.StateFile));
        Assert.DoesNotContain("PRIVATE-", json);
        Assert.Equal(fixture.Binding.HostProject, state.Binding.HostProject);
        Assert.Equal(Cwd, state.Checkpoint.Project);
    }

    [Fact]
    public void FailedStateWriteCanRetryWithoutLosingCursorOrUnpublishedCompletion()
    {
        var fixture = Create();
        var originalConsent = new NotificationConsent(true, true, true, "original-destination", "original-epoch");
        Assert.True(fixture.Feed.Ingest(Receipt(1, Input("UserPromptSubmit", "run"), fixture.Clock)));
        var receipt = Receipt(2, Input("Stop", "run"), fixture.Clock, originalConsent);
        var path = Path.Combine(fixture.Root, GrokActivityFeed.StateFile);
        var before = File.ReadAllBytes(path);
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => fixture.Feed.Ingest(receipt));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        // A recreated writer cannot acquire later Settings consent on retry.
        var reopened = new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock);
        Assert.True(reopened.Ingest(receipt));
        Assert.False(fixture.Feed.Ingest(receipt));
        var state = fixture.Feed.Read();
        Assert.Equal(2, state.Cursor);
        Assert.Single(state.Pending, item => item.Event.Kind == AgentEventKind.RunFinished);
        Assert.Equal(originalConsent, state.Pending.Single(item => item.Event.Kind == AgentEventKind.RunFinished).Consent);
        Assert.False(fixture.Feed.Acknowledge(["wrong-id"]));
        var prefix = state.Pending.Select(item => item.Event.EventId).ToArray();
        Assert.True(fixture.Feed.Ingest(Receipt(3, Input("UserPromptSubmit", "next-run"), fixture.Clock)));
        Assert.True(fixture.Feed.Acknowledge(prefix));
        Assert.Equal("next-run", Assert.Single(fixture.Feed.Read().Pending).Event.RunId);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, GrokActivityFeed.StateFile + ".*.tmp"));
    }

    [Fact]
    public void ResyncPersistsTheActualObservationWithoutInventingAStartOrFinish()
    {
        var fixture = Create();
        fixture.Feed.Ingest(Receipt(1, Input("UserPromptSubmit", "previous-run"), fixture.Clock));
        fixture.Feed.Ingest(Receipt(2, Input("UserPromptSubmit", "new-run"), fixture.Clock));
        var state = new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock).Read();
        Assert.Equal("new-run", state.Checkpoint.Activity.Observation!.RunId);
        Assert.Equal("new-run", state.Checkpoint.Activity.RunId);
        Assert.False(state.Checkpoint.HistoryComplete);
        Assert.Single(state.Pending);
        Assert.Equal("previous-run", state.Pending[0].Event.RunId);
        Assert.DoesNotContain(state.Pending, item => item.Event.Kind == AgentEventKind.RunFinished);
    }

    [Fact]
    public void ReplayStaleReceiptsAndSourceGapsCannotCreateFreshActivityOrAcquireNewConsent()
    {
        var consent = new NotificationConsent(false, false, false, "old-destination", "old-epoch");
        var fixture = Create();
        fixture.Feed.Ingest(Receipt(1, Input("UserPromptSubmit", "live-run"), fixture.Clock, consent));
        Assert.Equal(AgentActivity.Working, fixture.Feed.Current().State);
        Assert.Equal(AgentActivity.Unknown, new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock).Current().State);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentActivity.Unknown, fixture.Feed.Current().State);
        fixture.Clock.Utc -= TimeSpan.FromSeconds(5);
        Assert.Equal(AgentActivity.Unknown, fixture.Feed.Current().State);
        var old = new GrokActivityReceipt(Guid.NewGuid().ToString("N"), 3, fixture.Clock.Utc - TimeSpan.FromMinutes(5), Input("Stop", "live-run"));
        fixture.Feed.Ingest(old, replay: true);
        var state = fixture.Feed.Read();
        Assert.False(state.Checkpoint.HistoryComplete);
        Assert.Equal(AgentActivity.Unknown, fixture.Feed.Current().State);
        Assert.Null(state.Pending.Single(item => item.Event.Kind == AgentEventKind.RunFinished).Consent);
        Assert.Equal(old.CapturedUtc, state.Pending.Single(item => item.Event.Kind == AgentEventKind.RunFinished).Event.OccurredUtc);
        consent = new(true, true, true, "new-destination", "new-epoch");
        Assert.False(state.Pending[0].Consent!.GlobalEnabled);
        Assert.Equal("old-epoch", state.Pending[0].Consent!.Epoch);
    }

    [Fact]
    public void WrongRootOrProjectCorruptStateAndCompetingOwnerDoNotReplaceHistory()
    {
        var fixture = Create();
        Assert.False(fixture.Feed.Ingest(Receipt(1, Input("SessionStart").Replace("known-root", "another-root"), fixture.Clock)));
        Assert.False(fixture.Feed.Ingest(Receipt(1, Input("SessionStart").Replace(Cwd, "/different/project"), fixture.Clock)));
        Assert.Equal(0, fixture.Feed.Read().Cursor);
        fixture.Feed.Ingest(Receipt(1, Input("UserPromptSubmit", "run"), fixture.Clock));
        var path = Path.Combine(fixture.Root, GrokActivityFeed.StateFile);
        var before = File.ReadAllBytes(path);
        using (var owner = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => fixture.Feed.Ingest(Receipt(2, Input("Stop", "run"), fixture.Clock)));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => new GrokActivityFeed(fixture.Root, fixture.Binding with { HostProject = Path.Combine(fixture.Root, "other-project") }, fixture.Clock).Read());
        File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => fixture.Feed.Ingest(Receipt(2, Input("Stop", "run"), fixture.Clock)));
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => new GrokActivityFeed(fixture.Root, fixture.Binding with { CliVersion = "unverified-version" }));
    }

    [Fact]
    public void ReplayIdentitySurvivesHandoffAndAFullFeedPreservesItsUnpublishedEvents()
    {
        var fixture = Create();
        fixture.Feed.Ingest(Receipt(1, Input("SessionStart"), fixture.Clock));
        fixture.Feed.Ingest(Receipt(2, Input("UserPromptSubmit", "run"), fixture.Clock));
        fixture.Feed.Ingest(Receipt(3, Input("Stop", "run"), fixture.Clock));
        var reopened = new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock);
        reopened.Ingest(Receipt(4, Input("SessionStart"), fixture.Clock));
        Assert.Equal(3, reopened.Read().Checkpoint.Activity.LastSequence);
        Assert.Single(reopened.Read().Pending, item => item.Event.Kind == AgentEventKind.Ready);
        var bounded = Create();
        for (var run = 0; run < GrokActivityFeed.MaxPending / 2; run++)
        {
            bounded.Feed.Ingest(Receipt(2 * run + 1, Input("UserPromptSubmit", "run-" + run), bounded.Clock));
            bounded.Feed.Ingest(Receipt(2 * run + 2, Input("Stop", "run-" + run), bounded.Clock));
        }
        var path = Path.Combine(bounded.Root, GrokActivityFeed.StateFile); var before = File.ReadAllBytes(path);
        var next = Receipt(GrokActivityFeed.MaxPending + 1, Input("UserPromptSubmit", "run-overflow"), bounded.Clock);
        Assert.Throws<IOException>(() => bounded.Feed.Ingest(next));
        Assert.Equal(before, File.ReadAllBytes(path));
        var state = bounded.Feed.Read();
        Assert.Equal(GrokActivityFeed.MaxPending, state.Cursor);
        Assert.Equal(GrokActivityFeed.MaxPending, state.Pending.Length);
        Assert.True(bounded.Feed.Acknowledge(state.Pending.Take(2).Select(item => item.Event.EventId).ToArray()));
        Assert.True(bounded.Feed.Ingest(next));
        Assert.Equal("run-overflow", bounded.Feed.Read().Checkpoint.Activity.RunId);
    }

    [Fact]
    public void RelayPublishesResyncAndPreservesOutcomesUntilItsStatusAndOutboxSinksSucceed()
    {
        var fixture = Create();
        var consent = new NotificationConsent(true, true, true, Guid.NewGuid().ToString("N"), "owned-epoch");
        fixture.Feed.Ingest(Receipt(1, Input("UserPromptSubmit", "first-run"), fixture.Clock));
        fixture.Feed.Ingest(Receipt(2, Input("UserPromptSubmit", "current-run"), fixture.Clock));
        var context = new SessionActivityContext(fixture.Root, fixture.Binding.Generation, fixture.Binding.HostProject, AgentChoice.Grok);
        var outbox = new NotificationOutbox(Path.Combine(fixture.Root, "owned-outbox"));
        GrokActivityPublication? last = null;
        Action<GrokActivityPublication> saveStatus = value =>
        {
            last = value;
            SessionActivityStore.Publish(value.Context, value.Activity, value.Connected, value.Events, value.Synchronized, value.HistoryComplete);
        };
        new GrokActivityRelay(fixture.Feed, context, outbox, saveStatus).Publish(true);
        Assert.Equal("current-run", last!.Activity.Observation!.RunId);
        Assert.Single(last.Events);
        Assert.Equal("first-run", last.Events[0].RunId);
        fixture.Feed.Ingest(Receipt(3, Input("Stop", "current-run"), fixture.Clock, consent));
        var failStatus = new GrokActivityRelay(fixture.Feed, context, outbox, _ => throw new IOException("Owned publication failure"));
        Assert.Throws<IOException>(() => failStatus.Publish(true));
        Assert.Single(fixture.Feed.Read().Pending);
        Assert.Throws<InvalidOperationException>(() => new GrokActivityRelay(fixture.Feed, context with { ProjectPath = fixture.Root }, outbox).Publish(true));
        var noQueue = new GrokActivityRelay(fixture.Feed, context, publish: value => last = value);
        Assert.Throws<IOException>(() => noQueue.Publish(true));
        Assert.NotNull(last);
        Assert.Equal("current-run", last!.Activity.RunId);
        Assert.False(last.HistoryComplete);
        Assert.Single(fixture.Feed.Read().Pending);
        var reopened = new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock);
        var relay = new GrokActivityRelay(reopened, context, outbox, saveStatus);
        using (var heldState = new FileStream(Path.Combine(fixture.Root, GrokActivityFeed.StateFile), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => relay.Publish(true));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Single(outbox.Read()); // Queue succeeded before final ack failed.
            Assert.Single(reopened.Read().Pending);
        }
        relay.Publish(true);
        Assert.False(last!.Synchronized);
        Assert.Empty(reopened.Read().Pending);
        relay.Publish(true);
        Assert.Single(outbox.Read());
        Assert.Equal("current-run", outbox.Read()[0].RunId);
    }

    [EnvironmentFact("LAUNCHPAD_GROK_FEED_REPLAY", "1")]
    public void RetainedRealGrokCallbacksUseTheDurableFeedWithoutAppearingLive()
    {
        var root = Repository();
        var entries = (Environment.GetEnvironmentVariable("LAUNCHPAD_GROK_FEED_ROOT") ?? throw new InvalidOperationException("Explicit owned retained fixture required.")).Split(';');
        Assert.InRange(entries.Length, 1, 4);
        foreach (var entry in entries)
        {
            var guest = Path.GetFullPath(entry);
            Assert.Equal(Path.Combine(root, "tests", "LaunchPad.Tests", "TestResults", "guest"), Path.GetDirectoryName(guest), ignoreCase: true);
            Assert.Matches("^[a-f0-9]{12}$", Path.GetFileName(guest));
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(guest, "agent-tools-private.json")));
            Assert.True(receipt.RootElement.GetProperty("complete").GetBoolean()); Assert.True(receipt.RootElement.GetProperty("shutdown").GetBoolean());
            using var proof = JsonDocument.Parse(File.ReadAllText(Path.Combine(guest, "fixture-output", "grok-hook-proof.json")));
            Assert.True(proof.RootElement.GetProperty("completed").GetBoolean()); Assert.Null(proof.RootElement.GetProperty("failure").GetString());
            using var inspection = JsonDocument.Parse(File.ReadAllText(Path.Combine(guest, "fixture-output", "grok-hook-inspect.json")));
            Assert.Equal("1.0.46", inspection.RootElement.GetProperty("grokVersion").GetString());
            var fixture = Create(rootSession: proof.RootElement.GetProperty("sessionId").GetString()!);
            var callbacks = Directory.EnumerateFiles(Path.Combine(guest, "fixture-output", "grok-hooks"), "*.json").Select(path => JsonDocument.Parse(File.ReadAllText(path))).ToArray();
            try
            {
                long sequence = 0;
                foreach (var row in callbacks.OrderBy(row => row.RootElement.GetProperty("capturedNs").GetInt64()))
                {
                    var value = row.RootElement;
                    var captured = DateTimeOffset.FromUnixTimeMilliseconds(value.GetProperty("capturedNs").GetInt64() / 1_000_000);
                    // Recreate the consumer every time to prove pending Stop
                    // bindings and unpublished events survive ownership handoff.
                    var feed = new GrokActivityFeed(fixture.Root, fixture.Binding, fixture.Clock);
                    Assert.True(feed.Ingest(new(Guid.NewGuid().ToString("N"), ++sequence, captured, value.GetProperty("adapterInput").GetRawText()), replay: true));
                    Assert.Equal(AgentActivity.Unknown, feed.Current().State);
                }
                var state = fixture.Feed.Read();
                Assert.Equal(2, state.Pending.Count(item => item.Event.Kind == AgentEventKind.RunStarted));
                Assert.Equal(2, state.Pending.Count(item => item.Event.Kind == AgentEventKind.RunFinished));
                Assert.DoesNotContain(state.Pending, item => item.Event.RunId?.StartsWith("task-completed-") == true);
                Assert.All(state.Pending, item => Assert.Null(item.Consent));
                Assert.False(state.Checkpoint.Activity.RunActive);
            }
            finally { foreach (var callback in callbacks) callback.Dispose(); }
        }
    }

    private static GrokActivityReceipt Receipt(long sequence, string input, Clock clock, NotificationConsent? consent = null) => new(Guid.NewGuid().ToString("N"), sequence, clock.Utc, input, consent);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan age) { Utc += age; _timestamp += age.Ticks; }
    }
    private static (string Root, GrokActivityBinding Binding, Clock Clock, GrokActivityFeed Feed) Create(string rootSession = "known-root")
    {
        var root = Path.Combine(Repository(), "tests", "LaunchPad.Tests", "TestResults", "migration", "activity-feed-20261006", "owned-" + Guid.NewGuid().ToString("N"));
        var binding = new GrokActivityBinding(Guid.NewGuid().ToString("N"), rootSession, Cwd, Path.Combine(root, "host-project"), "1.0.46");
        var clock = new Clock(); return (root, binding, clock, new(root, binding, clock));
    }
    private static string Repository()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "installer", "LaunchPad.iss"))) return parent.FullName;
        throw new DirectoryNotFoundException("LaunchPad repository fixture root not found.");
    }
}
