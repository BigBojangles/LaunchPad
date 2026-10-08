using LaunchPad.Models;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

[Trait("Category", "Windows")]
public sealed class NotificationDisconnectTests
{
    private static AppPaths Fixture() => new(appDataDir: Path.Combine(GuestBaselineTests.RepositoryRoot(),
        "tests", "LaunchPad.Tests", "TestResults", "migration", "notification-disconnect-" + Guid.NewGuid().ToString("N")));

    private static string Channel(NotificationService service) => service.Destinations.Add(new()
    {
        Provider = NotificationProvider.Email, SmtpHost = "smtp.example.invalid",
        Username = "fixture@example.invalid", Password = "disconnect-fixture-password",
        From = "fixture@example.invalid", To = "fixture@example.invalid"
    });
    private static SettingsStore Enable(AppPaths paths, string reference)
    {
        var settings = new SettingsStore(paths);
        settings.SavePreferences(true, "grok", 4096, 2, notifications: new(true, reference));
        return settings;
    }
    private static string Secrets(AppPaths paths) => Path.Combine(paths.AppDataDir, "notifications", "destinations");
    private sealed class Transport : INotificationTransport
    {
        public int Calls;
        public Task<ProviderAcceptance> SendAsync(string reference, AgentNotificationMessage message, CancellationToken cancellation)
        { Calls++; return Task.FromResult(ProviderAcceptance.Accepted); }
    }

    [Fact]
    public void DisconnectErasesActiveOldAbandonedAndCorruptSecretsWhilePreservingOtherState()
    {
        var paths = Fixture();
        var starts = 0;
        var service = new NotificationService(paths, () => starts++);
        var reference = Channel(service);
        Channel(service); // Retired channel, no longer referenced by settings.
        var settings = Enable(paths, reference);
        settings.Current.RememberGrokSignIn = true;
        settings.SaveSettings();
        var epoch = settings.Current.NotificationEpoch;
        File.WriteAllText(Path.Combine(Secrets(paths), Guid.NewGuid().ToString("N") + ".bin"), "corrupt-owned-credential");
        File.WriteAllText(Path.Combine(Secrets(paths), Guid.NewGuid().ToString("N") + ".tmp"), "abandoned-owned-credential");
        var unrelated = Path.Combine(Secrets(paths), "unrelated.txt");
        File.WriteAllText(unrelated, "preserve");
        var history = Path.Combine(paths.AppDataDir, "notifications", "channel-test-history.json");
        File.WriteAllText(history, "preserve history");
        Assert.Equal(4, service.Disconnect(settings));
        var reloaded = new SettingsStore(paths);
        Assert.False(reloaded.Current.NotificationsEnabled);
        Assert.Null(reloaded.Current.NotificationDestination);
        Assert.NotEqual(epoch, reloaded.Current.NotificationEpoch);
        Assert.True(reloaded.Current.RememberGrokSignIn);
        Assert.Empty(Directory.GetFiles(Secrets(paths), "*.bin"));
        Assert.Empty(Directory.GetFiles(Secrets(paths), "*.tmp"));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        Assert.Equal("preserve history", File.ReadAllText(history));
        Assert.Equal(0, starts);
        Assert.Equal(0, service.Disconnect(settings)); // Safe even when already disconnected.
    }

    [Fact]
    public async Task ReconnectCannotReplayPendingAlertsAndHistoryIsRetained()
    {
        var paths = Fixture();
        var service = new NotificationService(paths, () => { });
        var reference = Channel(service);
        var settings = Enable(paths, reference);
        var project = Path.Combine(paths.AppDataDir, "project");
        settings.SaveProjectNotifications(project, true);
        var generation = Guid.NewGuid().ToString("N");
        var tracker = new AgentActivityTracker(generation);
        Assert.True(tracker.TryAccept(new(1, generation, 1, "start", AgentEventKind.RunStarted,
            DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1")));
        Assert.True(tracker.TryAccept(new(1, generation, 2, "finish", AgentEventKind.RunFinished,
            DateTimeOffset.UtcNow, "run-1", AgentSessionId: "session-1"), out var accepted));
        Assert.True(service.Outbox.Queue(accepted!, project, "grok", settings.NotificationConsentFor(project)));
        var original = Assert.Single(service.Outbox.Read());
        service.Disconnect(settings);
        Assert.Equal(original, Assert.Single(service.Outbox.Read()));
        Assert.True(Assert.Single(settings.KnownProjects).NotificationsEnabled);
        var replacement = Channel(service);
        settings.SavePreferences(true, "grok", 4096, 2, notifications: new(true, replacement));
        var transport = new Transport();
        Assert.False(await service.Outbox.DispatchOneAsync(transport,
            path => new SettingsStore(paths).NotificationConsentFor(path), _ => "Project"));
        Assert.Equal(0, transport.Calls);
        Assert.Equal(NotificationState.Suppressed, Assert.Single(service.Outbox.Read()).State);
    }

    [Fact]
    public void ConflictingSettingsSavePreservesSecretsAndDoesNotOverwriteNewerPreferences()
    {
        var paths = Fixture();
        var service = new NotificationService(paths, () => { });
        var reference = Channel(service);
        var settings = Enable(paths, reference);
        var newer = new SettingsStore(paths);
        newer.SavePreferences(false, "codex", 4096, 2);
        Assert.ThrowsAny<IOException>(() => service.Disconnect(settings));
        Assert.True(settings.Current.NotificationsEnabled);
        Assert.Equal(reference, settings.Current.NotificationDestination);
        Assert.Equal("disconnect-fixture-password", service.Destinations.Read(reference).Password);
        var reloaded = new SettingsStore(paths);
        Assert.False(reloaded.Current.ShowTips);
        Assert.Equal("codex", reloaded.Current.DefaultAgent);
        Assert.True(reloaded.Current.NotificationsEnabled);
    }

    [Fact]
    public void DeletionFailureLeavesAlertsDisabledAndReportsUnremovedSecret()
    {
        var paths = Fixture();
        var service = new NotificationService(paths, () => { });
        var reference = Channel(service);
        var settings = Enable(paths, reference);
        using var blocked = new FileStream(Path.Combine(Secrets(paths), reference + ".bin"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.Throws<NotificationDisconnectException>(() => service.Disconnect(settings));
        Assert.DoesNotContain("disconnect-fixture-password", error.ToString());
        var reloaded = new SettingsStore(paths);
        Assert.False(reloaded.Current.NotificationsEnabled);
        Assert.Null(reloaded.Current.NotificationDestination);
        Assert.True(File.Exists(Path.Combine(Secrets(paths), reference + ".bin")));
        blocked.Dispose();
        Assert.Equal(1, service.Disconnect(settings));
    }

    [Fact]
    public void CredentialGuardBlocksConcurrentMutationWithoutDeletingOtherEntries()
    {
        var paths = Fixture();
        var service = new NotificationService(paths, () => { });
        var reference = Channel(service);
        using var guard = new FileStream(Path.Combine(Secrets(paths), "destinations.guard"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => service.Destinations.ForgetAll());
        Assert.ThrowsAny<IOException>(() => Channel(service));
        Assert.True(File.Exists(Path.Combine(Secrets(paths), reference + ".bin")));
    }

    [Fact]
    public void NewUsersMustOptIntoHostLoginReuseAndExistingChoiceSurvivesRestart()
    {
        var paths = Fixture();
        var settings = new SettingsStore(paths);
        Assert.False(settings.Current.RememberGrokSignIn);
        settings.SavePreferences(true, "grok", 4096, 2, rememberGrokSignIn: true);
        Assert.True(new SettingsStore(paths).Current.RememberGrokSignIn);
    }

    [Fact]
    public void ChannelPublicationCannotRaceTheDisconnectCredentialGuard()
    {
        var paths = Fixture();
        var service = new NotificationService(paths, () => { });
        var reference = Channel(service);
        var settings = new SettingsStore(paths);
        using (var guard = new FileStream(Path.Combine(Secrets(paths), "destinations.guard"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => settings.SavePreferences(true, "grok", 4096, 2,
                notifications: new(true, reference)));
            Assert.False(settings.Current.NotificationsEnabled);
            Assert.Null(settings.Current.NotificationDestination);
        }
        settings.SavePreferences(true, "grok", 4096, 2, notifications: new(true, reference));
        settings.PreferencesChanged += () => Assert.ThrowsAny<IOException>(() => Channel(service));
        service.Disconnect(settings); // Guard stays held while revocation is published.
        Assert.False(new SettingsStore(paths).Current.NotificationsEnabled);
        Assert.Empty(Directory.GetFiles(Secrets(paths), "*.bin"));
    }
}
