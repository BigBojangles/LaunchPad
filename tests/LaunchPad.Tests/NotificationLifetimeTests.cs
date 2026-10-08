using System.Diagnostics;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Trait("Category", "Windows")]
public sealed class NotificationLifetimeTests
{
    private const string Reference = "11111111111111111111111111111111";
    private static AppPaths Fixture()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults",
            "migration", "notification-lifetime-" + Guid.NewGuid().ToString("N"));
        return new(appDataDir: Path.Combine(root, "settings"), exePath: Path.Combine(root, "app", "LaunchPad.exe"));
    }
    private static SettingsStore Enable(AppPaths paths)
    {
        var settings = new SettingsStore(paths);
        settings.SavePreferences(true, "grok", 4096, 2, notifications: new(true, Reference));
        return settings;
    }
    private static void Queue(NotificationOutbox box, SettingsStore settings, string project)
    {
        settings.SaveProjectNotifications(project, true);
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(new(1, generation, 1, "start", AgentEventKind.RunStarted,
            DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1")));
        Assert.True(tracker.TryAccept(new(1, generation, 2, "finish", AgentEventKind.RunFinished,
            DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1"), out var accepted));
        Assert.True(box.Queue(accepted!, project, "grok", settings.NotificationConsentFor(project)));
    }
    private static FileStream Lease(AppPaths paths) => new(Path.Combine(paths.AppDataDir, "notifications", "delivery.owner"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private sealed class Transport(Func<CancellationToken, Task<ProviderAcceptance>>? send = null) : INotificationTransport
    {
        public int Calls;
        public Task<ProviderAcceptance> SendAsync(string reference, AgentNotificationMessage message, CancellationToken cancellation)
        { Calls++; return send?.Invoke(cancellation) ?? Task.FromResult(ProviderAcceptance.Accepted); }
    }

    [Fact]
    public void NativePresenceCountsLiveOwnedTerminalsAndRejectsStoppedOrReusedPids()
    {
        var paths = Fixture();
        using var process = Process.GetCurrentProcess();
        var project = Path.Combine(paths.AppDataDir, "project");
        var other = Path.Combine(paths.AppDataDir, "other");
        var sessions = Path.Combine(paths.AppDataDir, "owned-vm-sessions");
        string Save(string folder, string state, long ticks)
        {
            var directory = Path.Combine(paths.AppDataDir, "windows", "native", QemuLayout.ProjectKey(folder));
            Directory.CreateDirectory(directory);
            NativeAgentTerminal.Save(directory, new(folder, "codex", paths.ExePath, Guid.NewGuid().ToString("N"),
                process.Id, ticks, state, AppDataDirectory: paths.AppDataDir));
            return directory;
        }
        var ticks = process.StartTime.ToUniversalTime().Ticks;
        Assert.False(NotificationTerminalPresence.HasOpenTerminal(paths, sessions)); // Unregistered processes never count.
        Save(project, "running", ticks + 1);
        Assert.False(NotificationTerminalPresence.HasOpenTerminal(paths, sessions));
        Save(project, "running", ticks);
        Save(other, "running", ticks);
        Assert.True(NotificationTerminalPresence.HasOpenTerminal(paths, sessions));
        Save(project, "stopped", ticks);
        Assert.True(NotificationTerminalPresence.HasOpenTerminal(paths, sessions));
        Save(other, "stopped", ticks);
        Assert.False(NotificationTerminalPresence.HasOpenTerminal(paths, sessions));
        var directory = Save(other, "running", ticks);
        var record = NativeAgentTerminal.Read(directory)!;
        NativeAgentTerminal.Save(directory, record with { AppDataDirectory = Path.Combine(paths.AppDataDir, "other-profile") });
        Assert.False(NotificationTerminalPresence.HasOpenTerminal(paths, sessions));
    }

    [Fact]
    public void FencedVmOrDesktopPresenceAloneCannotKeepPagerAlive()
    {
        using var process = Process.GetCurrentProcess();
        var ticks = process.StartTime.ToUniversalTime().Ticks;
        var identity = new SessionOwnerIdentity(process.Id, ticks, process.Id, ticks, 17000,
            Guid.NewGuid().ToString("N"), "grok", "fixture", DesktopPid: process.Id, DesktopStartTicks: ticks);
        Assert.False(NotificationTerminalPresence.HasLiveFencedTerminal(identity, WindowsSessionWindow.MatchesProcess));
        Assert.True(NotificationTerminalPresence.HasLiveFencedTerminal(identity with
            { TerminalPid = process.Id, TerminalStartTicks = ticks }, WindowsSessionWindow.MatchesProcess));
        Assert.False(NotificationTerminalPresence.HasLiveFencedTerminal(identity with
            { TerminalPid = process.Id, TerminalStartTicks = ticks + 1 }, WindowsSessionWindow.MatchesProcess));
    }

    [Fact]
    public void PagerOffAndUnconfiguredNeverStartEvenWithTerminalOrPendingAlerts()
    {
        var paths = Fixture();
        var settings = Enable(paths);
        var box = new NotificationService(paths, () => { }).Outbox;
        Assert.False(NotificationDeliveryOwner.ShouldStart(paths, box, () => false));
        Assert.True(NotificationDeliveryOwner.ShouldStart(paths, box, () => true));
        Queue(box, settings, Path.Combine(paths.AppDataDir, "project"));
        Assert.True(NotificationDeliveryOwner.ShouldStart(paths, box, () => false)); // Bounded final drain.
        settings.SavePreferences(true, "grok", 4096, 2, notifications: new(false, Reference));
        using (var held = new FileStream(Path.Combine(paths.AppDataDir, "notifications", "outbox", "outbox.guard"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(NotificationDeliveryOwner.ShouldStart(paths, box, () => true)); // Off gate precedes queue access.
        settings.Current.NotificationsEnabled = true;
        settings.Current.NotificationDestination = null;
        settings.SaveSettings();
        Assert.False(NotificationDeliveryOwner.ShouldStart(paths, box, () => true));
    }

    [Fact]
    public async Task LastTerminalFinalAlertDrainsOnceThenReleasesOwnerLease()
    {
        var paths = Fixture();
        var settings = Enable(paths);
        var box = new NotificationService(paths, () => { }).Outbox;
        Queue(box, settings, Path.Combine(paths.AppDataDir, "project"));
        var transport = new Transport();
        using var lease = Lease(paths);
        await NotificationDeliveryOwner.RunLoopAsync(paths, box, transport, lease, () => false);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(NotificationState.Accepted, Assert.Single(box.Read()).State);
        using var replacement = Lease(paths);
        Assert.False(NotificationDeliveryOwner.ShouldStart(paths, box, () => false));
    }

    [Fact]
    public async Task CompletionQueuedAfterDrainCannotBeLostDuringOwnerShutdown()
    {
        var paths = Fixture();
        var settings = Enable(paths);
        var service = new NotificationService(paths, () => { });
        var before = service.Outbox.Read().Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        using var lease = Lease(paths);
        Queue(service.Outbox, settings, Path.Combine(paths.AppDataDir, "project"));
        NotificationDeliveryOwner.StartIfEnabled(paths); // Finds our existing lease; no child is started.
        Assert.False(NotificationDeliveryOwner.TryStop(paths, lease, () => false, before));
        Assert.ThrowsAny<IOException>(() => Lease(paths));
        var transport = new Transport();
        await NotificationDeliveryOwner.RunLoopAsync(paths, service.Outbox, transport, lease, () => false);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(NotificationState.Accepted, Assert.Single(service.Outbox.Read()).State);
        using var replacement = Lease(paths);
    }

    [Fact]
    public async Task FinalDrainDeadlineRetainsPendingAndUnknownWithoutRetries()
    {
        var paths = Fixture();
        var settings = Enable(paths);
        var box = new NotificationService(paths, () => { }).Outbox;
        Queue(box, settings, Path.Combine(paths.AppDataDir, "project"));
        Queue(box, settings, Path.Combine(paths.AppDataDir, "other"));
        var transport = new Transport(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ProviderAcceptance.Accepted;
        });
        using var lease = Lease(paths);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await NotificationDeliveryOwner.RunLoopAsync(paths, box, transport, lease, () => false,
            TimeSpan.FromMilliseconds(100), deadline.Token);
        Assert.Equal(1, transport.Calls);
        Assert.Single(box.Read(), item => item.State == NotificationState.Unknown);
        Assert.Single(box.Read(), item => item.State == NotificationState.Pending);
        using var replacement = Lease(paths);
    }

    [Fact]
    public async Task TurningPagerOffCancelsInFlightAndExitsEvenWithTerminalOpen()
    {
        var paths = Fixture();
        var settings = Enable(paths);
        var box = new NotificationService(paths, () => { }).Outbox;
        Queue(box, settings, Path.Combine(paths.AppDataDir, "project"));
        Queue(box, settings, Path.Combine(paths.AppDataDir, "other"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return ProviderAcceptance.Accepted;
        });
        using var lease = Lease(paths);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = NotificationDeliveryOwner.RunLoopAsync(paths, box, transport, lease, () => true, cancellation: deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        settings.SavePreferences(true, "grok", 4096, 2, notifications: new(false, Reference));
        await run.WaitAsync(deadline.Token);
        Assert.Equal(1, transport.Calls);
        Assert.Single(box.Read(), item => item.State == NotificationState.Unknown);
        Assert.Single(box.Read(), item => item.State == NotificationState.Pending);
        using var replacement = Lease(paths);
    }

    [Fact]
    public async Task MainGuiIsNotRequiredButLastTerminalClosureStopsIdleSender()
    {
        var paths = Fixture(); Enable(paths);
        var box = new NotificationService(paths, () => { }).Outbox;
        var terminalCount = 2;
        var transport = new Transport();
        using var lease = Lease(paths);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = NotificationDeliveryOwner.RunLoopAsync(paths, box, transport, lease,
            () => Volatile.Read(ref terminalCount) > 0, cancellation: deadline.Token);
        Assert.False(run.IsCompleted);
        Volatile.Write(ref terminalCount, 1);
        Assert.False(NotificationDeliveryOwner.TryStop(paths, lease, () => Volatile.Read(ref terminalCount) > 0));
        Volatile.Write(ref terminalCount, 0);
        await run.WaitAsync(deadline.Token);
        Assert.Equal(0, transport.Calls);
        using var replacement = Lease(paths);
    }
}
