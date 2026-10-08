using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class NotificationOutboxTests
{
    private static readonly NotificationConsent Enabled = new(true, true, true, "11111111111111111111111111111111");
    private static string Fixture()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "notifications-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static AcceptedAgentActivityEvent Accepted(AgentEventKind kind, string run = "run-1", string? question = null, string? session = "agent-session-1")
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(new(1, generation, 1, "start", AgentEventKind.RunStarted, DateTimeOffset.UtcNow, run, AgentSessionId: session)));
        Assert.True(tracker.TryAccept(new(1, generation, 2, "outcome", kind, DateTimeOffset.UtcNow, run, question, AgentSessionId: session), out var accepted));
        return accepted!;
    }

    private sealed class FakeTransport(Func<AgentNotificationMessage, Task<ProviderAcceptance>>? send = null) : INotificationTransport
    {
        public List<AgentNotificationMessage> Messages { get; } = [];
        public Task<ProviderAcceptance> SendAsync(string destinationReference, AgentNotificationMessage message, CancellationToken cancellation)
        {
            Messages.Add(message);
            return send?.Invoke(message) ?? Task.FromResult(ProviderAcceptance.Accepted);
        }
    }

    [Fact]
    public void AllOptInGatesAndStableAgentSessionAreRequired()
    {
        var root = Fixture();
        var box = new NotificationOutbox(Path.Combine(root, "outbox"));
        var project = Path.Combine(root, "project");
        var done = Accepted(AgentEventKind.RunFinished);
        Assert.False(box.Queue(done, project, "codex", new()));
        Assert.False(box.Queue(done, project, "codex", Enabled with { GlobalEnabled = false }));
        Assert.False(box.Queue(done, project, "codex", Enabled with { ProjectEnabled = false }));
        Assert.False(box.Queue(done, project, "codex", Enabled with { Configured = false }));
        Assert.False(box.Queue(Accepted(AgentEventKind.RunFinished, session: null), project, "codex", Enabled));
        Assert.Empty(box.Read());
        Assert.True(box.Queue(done, project, "codex", Enabled));
    }

    [Fact]
    public void RestartReconnectAndDestinationChangeDoNotReplayRunOrQuestionAlerts()
    {
        var root = Fixture();
        var store = Path.Combine(root, "outbox");
        var project = Path.Combine(root, "project");
        var box = new NotificationOutbox(store);
        Assert.True(box.Queue(Accepted(AgentEventKind.NeedsAttention, question: "question-1"), project, "codex", Enabled));
        box = new NotificationOutbox(store);
        Assert.False(box.Queue(Accepted(AgentEventKind.NeedsAttention, question: "question-1"), project, "codex",
            Enabled with { DestinationReference = "22222222222222222222222222222222" }));
        Assert.True(box.Queue(Accepted(AgentEventKind.NeedsAttention, question: "question-2"), project, "codex", Enabled));
        Assert.True(box.Queue(Accepted(AgentEventKind.RunFinished), project, "codex", Enabled));
        Assert.False(new NotificationOutbox(store).Queue(Accepted(AgentEventKind.RunFailed), project, "codex", Enabled));
        Assert.True(box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "other-project"), "codex", Enabled));
        Assert.Equal(4, box.Read().Count);
    }

    [Fact]
    public async Task DispatchUsesCurrentDisplayNameAndNeverIncludesProjectPath()
    {
        var root = Fixture();
        var box = new NotificationOutbox(Path.Combine(root, "outbox"));
        var project = Path.Combine(root, "private-source-folder");
        box.Queue(Accepted(AgentEventKind.RunFinished), project, "codex", Enabled);
        var transport = new FakeTransport();
        Assert.True(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Renamed project"));
        var message = Assert.Single(transport.Messages);
        Assert.Equal("Renamed project", message.ProjectName);
        Assert.Contains("run-1", message.Text);
        Assert.DoesNotContain("private-source-folder", message.Text);
        Assert.Contains("Project completion and tests are separate", message.Text);
        Assert.Equal(NotificationState.Accepted, Assert.Single(box.Read()).State);
        Assert.False(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Renamed again"));
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task UnknownAcceptanceIsPersistedWithoutLeakingExceptionOrRetrying()
    {
        var root = Fixture();
        var store = Path.Combine(root, "outbox");
        var box = new NotificationOutbox(store);
        box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "claude", Enabled);
        var transport = new FakeTransport(_ => throw new IOException("secret-token-in-uri"));
        Assert.True(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Equal(NotificationState.Unknown, Assert.Single(box.Read()).State);
        Assert.DoesNotContain("secret-token-in-uri", File.ReadAllText(box.LedgerPath));
        Assert.False(await new NotificationOutbox(store).DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task OptOutAfterQueueSuppressesPermanentlyWithoutSending()
    {
        var root = Fixture();
        var box = new NotificationOutbox(Path.Combine(root, "outbox"));
        box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "codex", Enabled);
        var transport = new FakeTransport();
        Assert.False(await box.DispatchOneAsync(transport, _ => Enabled with { ProjectEnabled = false }, _ => "Project"));
        Assert.Equal(NotificationState.Suppressed, Assert.Single(box.Read()).State);
        Assert.False(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Empty(transport.Messages);
    }

    [Fact]
    public async Task RecoveryNeverStealsALiveSenderButAbandonedSendBecomesUnknown()
    {
        var root = Fixture();
        var store = Path.Combine(root, "outbox");
        var box = new NotificationOutbox(store);
        box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "codex", Enabled);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderAcceptance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(_ => { entered.SetResult(); return release.Task; });
        var delivery = box.DispatchOneAsync(transport, _ => Enabled, _ => "Project");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var other = new NotificationOutbox(store);
            Assert.Equal(0, other.RecoverAbandonedSends());
            Assert.False(await other.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        }
        finally { release.TrySetResult(ProviderAcceptance.Accepted); }
        Assert.True(await delivery);
        // Owned crash fixture: simulate the persisted claim with no live lease.
        var ledger = new NotificationOutbox.Ledger { Items = [Assert.Single(box.Read()) with { State = NotificationState.Sending }] };
        File.WriteAllText(box.LedgerPath, JsonSerializer.Serialize(ledger, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(1, new NotificationOutbox(store).RecoverAbandonedSends());
        Assert.Equal(NotificationState.Unknown, Assert.Single(box.Read()).State);
        Assert.False(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Single(transport.Messages);
    }

    [Fact]
    public void StructurallyValidCompletionWithoutAnActiveRunCannotBecomeAnAcceptedNotification()
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        var completion = new AgentActivityEvent(1, generation, 1, "outcome", AgentEventKind.RunFinished,
            DateTimeOffset.UtcNow, "run-1", AgentSessionId: "agent-session-1");
        Assert.True(AgentActivityTracker.IsValidEvent(completion, generation));
        Assert.False(tracker.TryAccept(completion, out var accepted));
        Assert.Null(accepted);
    }

    [Fact]
    public async Task CancellationBeforeTransportPreservesPendingButAfterEntryCannotBlindlyRetry()
    {
        var root = Fixture();
        var box = new NotificationOutbox(Path.Combine(root, "outbox"));
        box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "codex", Enabled);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var transport = new FakeTransport(_ => Task.FromCanceled<ProviderAcceptance>(stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            box.DispatchOneAsync(transport, _ => Enabled, _ => "Project", stopped.Token));
        Assert.Empty(transport.Messages);
        Assert.Equal(NotificationState.Pending, Assert.Single(box.Read()).State);
        Assert.True(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Equal(NotificationState.Unknown, Assert.Single(box.Read()).State);
        Assert.False(await box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task CorruptHistoryOrUnavailableDurableWriteCannotSend()
    {
        var root = Fixture();
        var store = Path.Combine(root, "outbox");
        var box = new NotificationOutbox(store);
        box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "codex", Enabled);
        var transport = new FakeTransport();
        using (var lease = new FileStream(Path.Combine(store, "outbox.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Empty(transport.Messages);
        File.WriteAllText(box.LedgerPath, "{broken history");
        await Assert.ThrowsAsync<IOException>(() => box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Equal("{broken history", File.ReadAllText(box.LedgerPath));
        Assert.Empty(transport.Messages);
        File.WriteAllText(box.LedgerPath, "{}");
        Assert.Throws<IOException>(() => box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "codex", Enabled));
        Assert.Equal("{}", File.ReadAllText(box.LedgerPath));
        Assert.Empty(transport.Messages);
    }

    [Theory]
    [InlineData(AgentNotificationKind.RunEnded)]
    [InlineData(AgentNotificationKind.ChannelTest)]
    public async Task ManualTestRowsCannotEnterAutomaticDispatch(AgentNotificationKind kind)
    {
        var root = Fixture();
        var box = new NotificationOutbox(Path.Combine(root, "outbox"));
        box.Queue(Accepted(AgentEventKind.RunFinished), Path.Combine(root, "project"), "codex", Enabled);
        var item = Assert.Single(box.Read()) with { Kind = kind, Outcome = AgentNotificationOutcome.ChannelTest };
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.ProjectPath));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { normalized, item.AgentId, item.AgentSessionId,
            item.RunId, kind.ToString(), "" });
        item = item with { Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
        var saved = JsonSerializer.Serialize(new NotificationOutbox.Ledger { Items = [item] }, JsonFile.Options);
        File.WriteAllText(box.LedgerPath, saved);
        var transport = new FakeTransport();
        await Assert.ThrowsAsync<IOException>(() => box.DispatchOneAsync(transport, _ => Enabled, _ => "Project"));
        Assert.Empty(transport.Messages);
        Assert.Equal(saved, File.ReadAllText(box.LedgerPath));
    }
}
