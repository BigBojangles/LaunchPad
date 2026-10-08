using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class NotificationIntegrationTests
{
    private static readonly string Reference = "11111111111111111111111111111111";
    private static AppPaths Fixture()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "notification-integration-" + Guid.NewGuid().ToString("N")[..12]);
        return new(userProfile: root, grokHome: Path.Combine(root, "grok"), appDataDir: Path.Combine(root, "settings"),
            exePath: Path.Combine(Path.GetDirectoryName(typeof(NotificationService).Assembly.Location)!, "LaunchPad.exe"));
    }
    private static void Enable(SettingsStore settings, bool enabled = true, string? reference = null) =>
        settings.SavePreferences(true, AgentChoice.Codex, 4096, 2, notifications: new(enabled, reference ?? Reference));
    private static AcceptedAgentActivityEvent Finished()
    {
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(new(1, generation, 1, "start", AgentEventKind.RunStarted, DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1")));
        Assert.True(tracker.TryAccept(new(1, generation, 2, "finished", AgentEventKind.RunFinished, DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1"), out var accepted));
        return accepted!;
    }
    private sealed class Transport : INotificationTransport
    {
        public List<AgentNotificationMessage> Messages { get; } = [];
        public Task<ProviderAcceptance> SendAsync(string reference, AgentNotificationMessage message, CancellationToken cancellation)
        { Messages.Add(message); return Task.FromResult(ProviderAcceptance.Accepted); }
    }

    [Fact]
    public async Task ConsentReloadAndEpochsPreventOldAlertsAfterOffOnAndDrainPastSuppression()
    {
        var paths = Fixture();
        var project = Path.Combine(paths.UserProfile, "project");
        var other = Path.Combine(paths.UserProfile, "other");
        var settings = new SettingsStore(paths);
        Assert.False(settings.Current.NotificationsEnabled);
        Assert.False(settings.NotificationConsentFor(project).ProjectEnabled);
        Enable(settings); settings.SaveProjectNotifications(project, true); settings.SaveProjectNotifications(other, true);
        var starts = 0;
        var service = new NotificationService(paths, () => starts++);
        Assert.True(service.Queue(Finished(), project, AgentChoice.Codex));
        settings.SaveProjectNotifications(project, false); settings.SaveProjectNotifications(project, true);
        Assert.True(service.Queue(Finished(), other, AgentChoice.Codex));
        settings.SaveDisplayName(other, "Current name");
        var transport = new Transport();
        Assert.Equal(2, await NotificationDeliveryOwner.TickAsync(paths, service.Outbox, transport));
        Assert.Equal("Current name", Assert.Single(transport.Messages).ProjectName);
        Assert.Equal(NotificationState.Suppressed, service.Outbox.Read().Single(item => item.ProjectPath == project).State);
        Assert.Equal(2, starts);
        var third = Path.Combine(paths.UserProfile, "third"); settings.SaveProjectNotifications(third, true);
        Assert.True(service.Queue(Finished(), third, AgentChoice.Codex));
        Enable(settings, false); Enable(settings);
        Assert.Equal(1, await NotificationDeliveryOwner.TickAsync(paths, service.Outbox, transport));
        Assert.Single(transport.Messages);
        Assert.Equal(NotificationState.Suppressed, service.Outbox.Read().Single(item => item.ProjectPath == third).State);
    }

    [Fact]
    public async Task RetriedQueueIntentCannotAcquireConsentFromAfterAnOptOut()
    {
        var paths = Fixture();
        var settings = new SettingsStore(paths);
        var project = Path.Combine(paths.UserProfile, "project");
        Enable(settings); settings.SaveProjectNotifications(project, true);
        var service = new NotificationService(paths, () => { });
        var accepted = Finished();
        service.Outbox.Read();
        using (var held = new FileStream(Path.Combine(paths.AppDataDir, "notifications", "outbox", "outbox.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => service.Queue(accepted, project, AgentChoice.Codex));
        settings.SaveProjectNotifications(project, false); settings.SaveProjectNotifications(project, true);
        Assert.True(service.Queue(accepted, project, AgentChoice.Codex));
        var transport = new Transport();
        Assert.Equal(1, await NotificationDeliveryOwner.TickAsync(paths, service.Outbox, transport));
        Assert.Empty(transport.Messages);
        Assert.Equal(NotificationState.Suppressed, Assert.Single(service.Outbox.Read()).State);
    }

    [Theory]
    [InlineData("project-off")]
    [InlineData("global-off")]
    [InlineData("destination")]
    [InlineData("off-on")]
    public async Task ConsentChangedAfterDurableClaimSuppressesBeforeTransport(string change)
    {
        var paths = Fixture();
        var project = Path.Combine(paths.UserProfile, "project");
        var settings = new SettingsStore(paths);
        Enable(settings); settings.SaveProjectNotifications(project, true);
        var service = new NotificationService(paths, () => { });
        var accepted = Finished();
        Assert.True(service.Queue(accepted, project, AgentChoice.Codex));
        var original = Assert.Single(service.Outbox.Read());
        var transport = new Transport();
        Assert.False(await service.Outbox.DispatchOneAsync(transport,
            path => new SettingsStore(paths).NotificationConsentFor(path),
            path =>
            {
                Assert.Equal(NotificationState.Sending, Assert.Single(service.Outbox.Read()).State);
                switch (change)
                {
                    case "project-off": settings.SaveProjectNotifications(project, false); break;
                    case "global-off": Enable(settings, false); break;
                    case "destination": Enable(settings, reference: "22222222222222222222222222222222"); break;
                    case "off-on": settings.SaveProjectNotifications(project, false); settings.SaveProjectNotifications(project, true); break;
                }
                return new SettingsStore(paths).DisplayNameFor(path);
            }));
        Assert.Empty(transport.Messages);
        var saved = Assert.Single(new NotificationService(paths, () => { }).Outbox.Read());
        Assert.Equal(NotificationState.Suppressed, saved.State);
        Assert.Equal(original.DestinationReference, saved.DestinationReference);
        Assert.Equal(original.ConsentEpoch, saved.ConsentEpoch);
        Assert.Contains("nothing was sent", saved.Status);
        Enable(settings); settings.SaveProjectNotifications(project, true);
        Assert.False(service.Outbox.Queue(accepted, project, AgentChoice.Codex, new SettingsStore(paths).NotificationConsentFor(project)));
        Assert.Equal(0, await NotificationDeliveryOwner.TickAsync(paths, service.Outbox, transport));
        Assert.Empty(transport.Messages);
    }

    [Fact]
    public void FinalOwnerStopRechecksReenableBeforeReleasingItsLease()
    {
        var paths = Fixture();
        var settings = new SettingsStore(paths); Enable(settings);
        var root = Path.Combine(paths.AppDataDir, "notifications"); Directory.CreateDirectory(root);
        var ownerFile = Path.Combine(root, "delivery.owner");
        bool TryStop(FileStream lease) => NotificationDeliveryOwner.TryStop(paths, lease, () => true);
        using var owned = new FileStream(ownerFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Enable(settings, false);
        var stoppingSnapshot = new SettingsStore(paths);
        Assert.False(stoppingSnapshot.Current.NotificationsEnabled);
        Enable(settings); // Re-enable after the old owner decided it could stop.
        Assert.False(TryStop(owned));
        Assert.Throws<IOException>(() => new FileStream(ownerFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        Enable(settings, false);
        Assert.True(TryStop(owned));
        using var replacement = new FileStream(ownerFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void FailedSettingsAndProjectWritesRollBackFlagsEpochsAndExistingRecords()
    {
        var paths = Fixture();
        var settings = new SettingsStore(paths);
        var project = Path.Combine(paths.UserProfile, "project");
        settings.RememberProject("Folder", project);
        settings.SaveDisplayName(project, "Display label");
        Enable(settings); settings.SaveProjectNotifications(project, true);
        var before = settings.NotificationConsentFor(project);
        using (var lease = new FileStream(paths.SettingsFile + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => Enable(settings, false));
        Assert.Equal(before, settings.NotificationConsentFor(project));
        using (var lease = new FileStream(paths.ProjectsFile + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => settings.SaveProjectNotifications(project, false));
        var reopened = new SettingsStore(paths);
        Assert.Equal(before, reopened.NotificationConsentFor(project));
        Assert.Equal("Display label", reopened.DisplayNameFor(project));
        Assert.True(settings.NotificationConsentFor(project).ProjectEnabled);
    }

    [Fact]
    public async Task OptionalNotificationQueueFailureCannotBreakAuthOrInventEventsFromState()
    {
        var paths = Fixture(); Directory.CreateDirectory(paths.UserProfile);
        var generation = Guid.NewGuid().ToString("N");
        var events = new ConcurrentQueue<AcceptedAgentActivityEvent>();
        var attempts = 0;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var guest = new TcpClient();
        await guest.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var link = new StatusLink(await listener.AcceptTcpClientAsync(timeout.Token), new(paths.UserProfile, generation),
            notify: accepted => { if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture queue failure"); events.Enqueue(accepted); });
        var invalidEnd = new AgentActivityEvent(1, generation, 1, "invalid-end", AgentEventKind.RunFinished, DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1");
        var start = invalidEnd with { EventId = "start", Kind = AgentEventKind.RunStarted };
        static byte[] Line(AgentActivityEvent value) => Encoding.UTF8.GetBytes(AgentActivityTracker.LinePrefix + JsonSerializer.Serialize(value, AgentActivityTracker.JsonOptions) + "\n");
        await guest.GetStream().WriteAsync(Line(invalidEnd), timeout.Token);
        await guest.GetStream().WriteAsync(Line(start), timeout.Token);
        var auth = link.RequestAuthAsync(TimeSpan.FromSeconds(3));
        await guest.GetStream().WriteAsync("AUTH 4\ndemo\n"u8.ToArray(), timeout.Token);
        Assert.Equal("demo", Encoding.UTF8.GetString((await auth)!));
        await WindowsConsoleProbeTests.Until(() => events.Count == 1, timeout.Token);
        Assert.Equal(AgentEventKind.RunStarted, events.Single().Value.Kind);
        var observation = new AgentStateObservation(1, generation, 2, "state-only", DateTimeOffset.UtcNow, "session-1", AgentActivity.Idle, RunId: "run-1");
        await guest.GetStream().WriteAsync(Encoding.UTF8.GetBytes(AgentActivityTracker.StatePrefix + JsonSerializer.Serialize(observation, AgentActivityTracker.JsonOptions) + "\n"), timeout.Token);
        await WindowsConsoleProbeTests.Until(() => link.AgentActivity.LastSequence == 2, timeout.Token);
        Assert.Single(events);
        Assert.Null(link.NotificationError);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task IndependentDeliveryHelperOwnsOneLeaseAndStopsAfterDurableOptOut()
    {
        var paths = Fixture();
        var settings = new SettingsStore(paths); Enable(settings);
        // Disposable metadata uses this test process as its live console owner.
        // This checks worker lifetime, not actual GUI/agent integration.
        using var terminal = Process.GetCurrentProcess();
        var project = Path.Combine(paths.UserProfile, "owned-terminal");
        var directory = Path.Combine(paths.AppDataDir, "windows", "native", QemuLayout.ProjectKey(project));
        Directory.CreateDirectory(directory);
        NativeAgentTerminal.Save(directory, new(project, AgentChoice.Codex, paths.ExePath, Guid.NewGuid().ToString("N"),
            terminal.Id, terminal.StartTime.ToUniversalTime().Ticks, "running", AppDataDirectory: paths.AppDataDir));
        // No events or credential configuration: this process cannot enter any provider transport.
        Process Start()
        {
            var info = new ProcessStartInfo(paths.ExePath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(NotificationDeliveryOwner.Argument); info.ArgumentList.Add(paths.AppDataDir);
            return Process.Start(info)!;
        }
        using var owner = Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await WindowsConsoleProbeTests.Until(() =>
            {
                if (owner.HasExited) throw new IOException("Owned notification helper exited before readiness.");
                var lease = Path.Combine(paths.AppDataDir, "notifications", "delivery.owner");
                if (!File.Exists(lease)) return false;
                try { using var probe = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
                catch (IOException) { return true; }
            }, timeout.Token);
            using var duplicate = Start();
            await duplicate.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, duplicate.ExitCode);
            Assert.False(owner.HasExited);
            Assert.Empty(new NotificationService(paths, () => { }).Outbox.Read());
            Enable(settings, false);
            await owner.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, owner.ExitCode);
        }
        finally { if (!owner.HasExited) { owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync(); } }
    }
}
