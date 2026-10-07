using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class AgentActivityTests
{
    internal static AgentActivityEvent Event(string generation, long sequence, AgentEventKind kind, string? run = "run-1", string? question = null) =>
        new(1, generation, sequence, "event-" + sequence, kind, DateTimeOffset.UtcNow, run, question);
    internal static byte[] Line(AgentActivityEvent value) => Encoding.UTF8.GetBytes(
        AgentActivityTracker.LinePrefix + JsonSerializer.Serialize(value, AgentActivityTracker.JsonOptions) + "\n");
    private static AgentStateObservation State(string generation, long sequence, AgentActivity state,
        string? run = "run-1", string? question = null) =>
        new(1, generation, sequence, "state-" + sequence, DateTimeOffset.UtcNow, "agent-session-1", state,
            run, question, state is AgentActivity.Working or AgentActivity.NeedsAttention);
    private static byte[] Line(AgentStateObservation value) => Encoding.UTF8.GetBytes(
        AgentActivityTracker.StatePrefix + JsonSerializer.Serialize(value, AgentActivityTracker.JsonOptions) + "\n");

    [Fact]
    public void CurrentStateRecoversMissedStartsWithoutInventingHistoricalOutcomes()
    {
        var generation = Guid.NewGuid().ToString("N");
        var buffer = new StatusBuffer(generation);
        var attention = State(generation, 7, AgentActivity.NeedsAttention, question: "question-1");
        var payload = Line(attention);
        foreach (var chunk in payload.Chunk(11)) buffer.Push(chunk, chunk.Length);
        Assert.Equal(AgentActivity.NeedsAttention, buffer.ActivitySnapshot.State);
        Assert.True(buffer.ActivitySnapshot.RunActive);
        Assert.Null(buffer.ActivitySnapshot.LastEvent);
        Assert.Empty(buffer.TakeEvents());
        Assert.False(buffer.HistoryComplete);
        Assert.True(AgentActivityTracker.IsValidSnapshot(buffer.ActivitySnapshot, generation));
        var replacement = Line(State(generation, 8, AgentActivity.Working, run: "run-2"));
        buffer.Push(replacement, replacement.Length);
        Assert.Equal("run-2", buffer.ActivitySnapshot.RunId);
        var reopened = new AgentActivityTracker(generation, buffer.ActivitySnapshot);
        Assert.False(reopened.TryAcceptState(State(generation, 9, AgentActivity.Working, run: "run-1")));
        var idle = Line(State(generation, 9, AgentActivity.Idle, run: "run-2"));
        buffer.Push(idle, idle.Length);
        Assert.False(buffer.ActivitySnapshot.RunActive);
        Assert.Empty(buffer.TakeEvents());
        Assert.Null(buffer.ActivitySnapshot.LastEvent);
    }

    [Fact]
    public void StateOnlyTransitionsKeepHistoryIncompleteAndIdleWithoutARunRetiresItsPreviousRun()
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(Event(generation, 1, AgentEventKind.RunStarted) with { AgentSessionId = "agent-session-1" }));
        Assert.True(tracker.TryAcceptState(State(generation, 2, AgentActivity.Working)));
        Assert.True(tracker.HistoryComplete);
        Assert.True(tracker.TryAcceptState(State(generation, 3, AgentActivity.NeedsAttention, question: "question-1")));
        Assert.False(tracker.HistoryComplete);
        Assert.True(tracker.TryAcceptState(State(generation, 4, AgentActivity.Idle, run: null)));
        var roundTrip = JsonSerializer.Deserialize<AgentActivitySnapshot>(JsonSerializer.Serialize(tracker.Snapshot, AgentActivityTracker.JsonOptions), AgentActivityTracker.JsonOptions)!;
        Assert.Equal(tracker.Snapshot, roundTrip);
        var reopened = new AgentActivityTracker(generation, roundTrip);
        Assert.False(reopened.TryAcceptState(State(generation, 5, AgentActivity.Working)));
        Assert.False(reopened.TryAccept(Event(generation, 5, AgentEventKind.RunStarted) with { AgentSessionId = "agent-session-1" }));
        Assert.Equal(AgentEventKind.RunStarted, reopened.Snapshot.LastEvent!.Kind);
        Assert.False(AgentActivityTracker.IsValidSnapshot(roundTrip with { RetiredRunIds = "run-1,run-1" }, generation));
    }

    [Fact]
    public void ResyncPreservesActualFailuresAndRejectsReplayOtherSessionsAndEndedRuns()
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(Event(generation, 1, AgentEventKind.RunStarted) with { AgentSessionId = "agent-session-1" }));
        var failure = Event(generation, 2, AgentEventKind.RunFailed) with { AgentSessionId = "agent-session-1" };
        Assert.True(tracker.TryAccept(failure));
        var idle = State(generation, 3, AgentActivity.Idle);
        Assert.True(tracker.TryAcceptState(idle));
        Assert.Equal(failure, tracker.Snapshot.LastEvent);
        Assert.Contains("failed", AgentActivityTracker.OutcomeText(tracker.Snapshot));
        Assert.True(tracker.HistoryComplete);
        var reopened = new AgentActivityTracker(generation, tracker.Snapshot);
        Assert.False(reopened.TryAcceptState(idle));
        Assert.False(reopened.TryAcceptState(State(generation, 4, AgentActivity.Working)));
        Assert.False(reopened.TryAcceptState(State(generation, 4, AgentActivity.Working, "run-2") with { AgentSessionId = "other-session" }));
        Assert.False(reopened.TryAcceptState(State(generation, 4, AgentActivity.Working, "run-2") with { MainRun = false }));
        Assert.True(reopened.TryAcceptState(State(generation, 4, AgentActivity.Working, "run-2")));
        Assert.Null(AgentActivityTracker.OutcomeText(reopened.Snapshot));
        Assert.False(reopened.HistoryComplete);
        Assert.False(reopened.TryAccept(Event(generation, 5, AgentEventKind.RunFinished, "run-2")));
        Assert.True(reopened.TryAccept(Event(generation, 5, AgentEventKind.RunFinished, "run-2") with { AgentSessionId = "agent-session-1" }));
    }

    [Fact]
    public void SequenceGapsRemainIncompleteAndARepeatedStartCannotClearAnActualQuestion()
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(Event(generation, 1, AgentEventKind.RunStarted)));
        Assert.True(tracker.TryAccept(Event(generation, 3, AgentEventKind.NeedsAttention, question: "question-1")));
        Assert.False(tracker.HistoryComplete);
        Assert.False(tracker.TryAccept(Event(generation, 4, AgentEventKind.RunStarted)));
        Assert.Equal("question-1", tracker.Snapshot.QuestionId);
        Assert.True(tracker.TryAccept(Event(generation, 4, AgentEventKind.Working)));
        Assert.False(tracker.HistoryComplete);
    }

    private sealed class ActivityClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    [Fact]
    public async Task AConnectedRelayCannotKeepDeadProducerStateCurrentAndResyncDoesNotJournalCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-producer-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var generation = Guid.NewGuid().ToString("N");
        var clock = new ActivityClock();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var guest = new TcpClient();
            await guest.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var link = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token), new(root, generation), clock);
            var started = Event(generation, 1, AgentEventKind.RunStarted) with { AgentSessionId = "agent-session-1" };
            await guest.GetStream().WriteAsync(Line(started), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Synchronized: true }, timeout.Token);
            clock.Advance(SessionActivityStore.Freshness + TimeSpan.FromTicks(1));
            await guest.GetStream().WriteAsync("SIZE-OK 24 80\nAUTH 4\ndemo\n"u8.ToArray(), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: true, Synchronized: false }, timeout.Token);
            Assert.Equal(AgentActivity.Unknown, link.AgentActivity.State);
            Assert.Equal(AgentActivity.Unknown, SessionActivityStore.Current(root, generation, DateTimeOffset.UtcNow).State);
            Assert.True(SessionActivityStore.Read(root, generation)!.Activity.RunActive);
            await guest.GetStream().WriteAsync(Line(started), timeout.Token);
            Assert.Equal(AgentActivity.Unknown, link.AgentActivity.State);
            await guest.GetStream().WriteAsync(Line(State(generation, 2, AgentActivity.NeedsAttention, question: "question-1")), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => link.AgentActivity.State == AgentActivity.NeedsAttention, timeout.Token);
            Assert.Equal("question-1", link.AgentActivity.QuestionId);
            await guest.GetStream().WriteAsync(Line(State(generation, 3, AgentActivity.Idle)), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation)?.Activity.State == AgentActivity.Idle, timeout.Token);
            Assert.Single(File.ReadAllLines(Path.Combine(root, SessionActivityStore.EventsFile)));
            Assert.Equal(started, SessionActivityStore.Read(root, generation)!.Activity.LastEvent);
            Assert.False(SessionActivityStore.Read(root, generation)!.HistoryComplete);
            guest.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: false }, timeout.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(AgentEventKind.RunFinished)]
    [InlineData(AgentEventKind.Interrupted)]
    [InlineData(AgentEventKind.RunFailed)]
    public void OnlyExplicitMainRunEventsCanEndTheMatchingRun(AgentEventKind ending)
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.False(tracker.TryAccept(Event(generation, 1, ending)));
        Assert.True(tracker.TryAccept(Event(generation, 1, AgentEventKind.Ready, run: null)));
        Assert.Equal(AgentActivity.Idle, tracker.Snapshot.State);
        Assert.Equal(AgentEventKind.Ready, tracker.Snapshot.LastEvent!.Kind);
        Assert.True(tracker.TryAccept(Event(generation, 2, AgentEventKind.RunStarted)));
        Assert.True(tracker.TryAccept(Event(generation, 3, AgentEventKind.NeedsAttention, question: "question-1")));
        Assert.Equal(AgentActivity.NeedsAttention, tracker.Snapshot.State);
        Assert.False(tracker.TryAccept(Event(generation, 4, ending) with { MainRun = false }));
        Assert.False(tracker.TryAccept(Event(generation, 4, ending, run: "other-run")));
        Assert.False(tracker.TryAccept(Event(generation, 4, AgentEventKind.Ready, run: null)));
        Assert.True(tracker.TryAccept(Event(generation, 4, AgentEventKind.Working)));
        Assert.Null(tracker.Snapshot.QuestionId);
        Assert.True(tracker.TryAccept(Event(generation, 5, ending)));
        Assert.False(tracker.Snapshot.RunActive);
        Assert.Equal(AgentActivity.Idle, tracker.Snapshot.State);
        Assert.Equal(ending, tracker.Snapshot.LastEvent!.Kind);
        if (ending == AgentEventKind.RunFinished) Assert.Null(AgentActivityTracker.OutcomeText(tracker.Snapshot));
        else Assert.Contains("VM remains open", AgentActivityTracker.OutcomeText(tracker.Snapshot));
        Assert.False(tracker.TryAccept(Event(generation, 6, AgentEventKind.RunStarted)));
        Assert.True(tracker.TryAccept(Event(generation, 6, AgentEventKind.RunStarted, run: "run-2")));
    }

    [Fact]
    public void StaleWrongGenerationReplayAndMalformedEventsDoNotChangeTheObservedRun()
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        var started = Event(generation, 10, AgentEventKind.RunStarted);
        Assert.True(tracker.TryAccept(started));
        Assert.False(tracker.TryAccept(Event(Guid.NewGuid().ToString("N"), 11, AgentEventKind.RunFinished)));
        Assert.False(tracker.TryAccept(Event(generation, 9, AgentEventKind.RunFinished)));
        Assert.False(tracker.TryAccept(started with { Sequence = 11, Kind = AgentEventKind.RunFinished }));
        Assert.False(tracker.TryAccept(Event(generation, 11, AgentEventKind.NeedsAttention)));
        Assert.False(tracker.TryAccept(Event(generation, 11, AgentEventKind.Working) with { EventId = null! }));
        Assert.False(tracker.TryAccept(Event(generation, 11, AgentEventKind.Working) with { Version = 2 }));
        Assert.False(tracker.TryAcceptLine("LP-EVENT {}", out _));
        Assert.False(tracker.TryAcceptLine("LP-EVENT {", out _));
        Assert.False(tracker.TryAcceptLine("LP-EVENT " + new string(' ', AgentActivityTracker.MaxEventBytes), out _));
        Assert.Equal(started, tracker.Snapshot.LastEvent);
    }

    [Fact]
    public void FragmentedEventsAndOversizedHeadersPreserveAuthAndHomeBodyFraming()
    {
        var generation = Guid.NewGuid().ToString("N");
        var buffer = new StatusBuffer(generation);
        var ready = Line(Event(generation, 1, AgentEventKind.Ready, run: null));
        var body = Line(Event(generation, 2, AgentEventKind.RunStarted));
        var payload = Encoding.ASCII.GetBytes("AUTH " + body.Length + "\n").Concat(body)
            .Concat(Encoding.ASCII.GetBytes("HOME " + body.Length + "\n")).Concat(body)
            .Concat(Encoding.ASCII.GetBytes("busy\nneeds-an-answer\n")).Concat(ready).ToArray();
        foreach (var piece in payload.Chunk(7)) buffer.Push(piece, piece.Length);
        Assert.Equal(body, buffer.AuthBody);
        Assert.Equal(body, buffer.HomeBody);
        Assert.Equal(AgentActivity.Idle, buffer.ActivitySnapshot.State);
        Assert.Single(buffer.TakeEvents());
        var oversized = Encoding.ASCII.GetBytes(new string('x', AgentActivityTracker.MaxEventBytes + 1));
        buffer.Push(oversized, oversized.Length);
        var recovery = "ignored-rest\n"u8.ToArray().Concat(Line(Event(generation, 2, AgentEventKind.RunStarted))).ToArray();
        buffer.Push(recovery, recovery.Length);
        Assert.Equal(AgentActivity.Working, buffer.ActivitySnapshot.State);
    }

    [Fact]
    public void SharedObservationRejectsOtherGenerationsStaleConnectionsAndInvalidState()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var generation = Guid.NewGuid().ToString("N");
        try
        {
            var tracker = new AgentActivityTracker(generation);
            var started = Event(generation, 1, AgentEventKind.RunStarted);
            Assert.True(tracker.TryAccept(started));
            SessionActivityStore.Publish(new(root, generation), tracker.Snapshot, true, new[] { started });
            var observation = SessionActivityStore.Read(root, generation)!;
            Assert.Equal(tracker.Snapshot, SessionActivityStore.Current(root, generation, observation.ObservedUtc));
            Assert.Equal(AgentActivity.Unknown, SessionActivityStore.Current(root, generation, observation.ObservedUtc + SessionActivityStore.Freshness + TimeSpan.FromTicks(1)).State);
            Assert.Null(SessionActivityStore.Read(root, Guid.NewGuid().ToString("N")));
            var reopened = new AgentActivityTracker(generation, observation.Activity);
            Assert.False(reopened.TryAccept(started));
            var finished = Event(generation, 2, AgentEventKind.RunFinished);
            Assert.True(reopened.TryAccept(finished));
            SessionActivityStore.Publish(new(root, generation), reopened.Snapshot, false, new[] { finished });
            Assert.Equal(AgentActivity.Unknown, SessionActivityStore.Current(root, generation, DateTimeOffset.UtcNow).State);
            var history = File.ReadAllLines(Path.Combine(root, SessionActivityStore.EventsFile));
            Assert.Equal(2, history.Length);
            Assert.Equal(AgentEventKind.RunFinished, JsonSerializer.Deserialize<AgentActivityEvent>(history[1], AgentActivityTracker.JsonOptions)!.Kind);
            File.WriteAllText(Path.Combine(root, SessionActivityStore.StateFile), JsonSerializer.Serialize(observation with
            { Activity = reopened.Snapshot with { RunActive = true, State = AgentActivity.Working } }, AgentActivityTracker.JsonOptions));
            Assert.Null(SessionActivityStore.Read(root, generation));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AStatusReconnectRestoresTheRunAndDoesNotAppendReplayedEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-activity-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var generation = Guid.NewGuid().ToString("N");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var first = new TcpClient();
            await first.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var firstLink = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token), new(root, generation));
            var started = Event(generation, 1, AgentEventKind.RunStarted);
            await first.GetStream().WriteAsync(Line(started), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation)?.Activity.RunActive == true, timeout.Token);
            first.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: false }, timeout.Token);
            Assert.Equal(AgentActivity.Unknown, firstLink.AgentActivity.State);
            var retained = SessionActivityStore.Read(root, generation)!;
            SessionActivityStore.Publish(new(root, generation), retained.Activity, false, historyComplete: false);
            using var second = new TcpClient();
            await second.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var secondLink = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token), new(root, generation));
            Assert.Equal(AgentActivity.Unknown, secondLink.AgentActivity.State);
            await second.GetStream().WriteAsync(Line(started), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: true, Synchronized: false }, timeout.Token);
            Assert.Equal(AgentActivity.Unknown, SessionActivityStore.Current(root, generation, DateTimeOffset.UtcNow).State);
            await second.GetStream().WriteAsync(Line(Event(generation, 2, AgentEventKind.NeedsAttention, question: "question-1")), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation)?.Activity.State == AgentActivity.NeedsAttention, timeout.Token);
            Assert.Equal(AgentActivity.NeedsAttention, secondLink.AgentActivity.State);
            Assert.False(SessionActivityStore.Read(root, generation)!.HistoryComplete);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(root, SessionActivityStore.EventsFile)).Length);
            second.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: false }, timeout.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AnUnavailableJournalRetainsEventsForRetryWithoutBreakingAuthTraffic()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-activity-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blockedJournal = Path.Combine(root, SessionActivityStore.EventsFile);
        Directory.CreateDirectory(blockedJournal);
        var generation = Guid.NewGuid().ToString("N");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var guest = new TcpClient();
            await guest.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var link = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token), new(root, generation));
            await guest.GetStream().WriteAsync(Line(Event(generation, 1, AgentEventKind.RunStarted)), timeout.Token);
            await WindowsConsoleProbeTests.Until(() => link.ObservationError is not null, timeout.Token);
            Assert.Equal(AgentActivity.Working, link.AgentActivity.State);
            var auth = link.RequestAuthAsync(TimeSpan.FromSeconds(3));
            await guest.GetStream().WriteAsync("AUTH 4\ndemo"u8.ToArray(), timeout.Token);
            Assert.Equal("demo", Encoding.ASCII.GetString((await auth)!));
            Directory.Delete(blockedJournal);
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Synchronized: true }, timeout.Token);
            Assert.Null(link.ObservationError);
            Assert.Single(File.ReadAllLines(blockedJournal));
            guest.Dispose();
            await WindowsConsoleProbeTests.Until(() => SessionActivityStore.Read(root, generation) is { Connected: false }, timeout.Token);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
