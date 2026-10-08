using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

[Trait("Category", "Windows")]
public sealed class NotificationTestPageTests
{
    private static AppPaths Fixture()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults",
            "migration", "notification-test-page-" + Guid.NewGuid().ToString("N"));
        return new(appDataDir: root);
    }

    private static string Channel(NotificationService service) => service.Destinations.Add(new()
    {
        Provider = NotificationProvider.Email, SmtpHost = "smtp.example.invalid",
        Username = "fixture@example.invalid", Password = "private-fixture-password",
        From = "phone@example.invalid", To = "phone@example.invalid"
    });

    private static string ReceiptPath(AppPaths paths) => Assert.Single(Directory.GetFiles(
        Path.Combine(paths.AppDataDir, "notifications"), "channel-test-*.json"));

    private static NotificationTestReceipt Receipt(AppPaths paths) => JsonSerializer.Deserialize<NotificationTestReceipt>(
        File.ReadAllText(ReceiptPath(paths)), JsonFile.Options)!;

    private sealed class Transport(Func<string, AgentNotificationMessage, CancellationToken, Task<ProviderAcceptance>> send)
        : INotificationTransport
    {
        public int Calls;
        public Task<ProviderAcceptance> SendAsync(string destinationReference, AgentNotificationMessage message,
            CancellationToken cancellation)
        { Calls++; return send(destinationReference, message, cancellation); }
    }

    [Fact]
    public async Task ExplicitTestHasDurableAttemptAndFixedContentWithoutEnablingAlerts()
    {
        var paths = Fixture();
        string? reference = null;
        var starts = 0;
        var transport = new Transport((target, message, _) =>
        {
            Assert.Equal(reference, target);
            var receipt = Receipt(paths);
            Assert.Equal(NotificationState.Sending, receipt.State);
            Assert.Equal(message.RunId, receipt.TestId);
            Assert.Equal(target, receipt.DestinationReference);
            Assert.Equal("LaunchPad test page", message.Subject);
            Assert.Equal(AgentNotificationKind.ChannelTest, message.Kind);
            Assert.Contains("does not enable automatic agent alerts", message.Text);
            Assert.DoesNotContain(paths.AppDataDir, message.Text);
            Assert.DoesNotContain("private-fixture-password", message.Text);
            Assert.DoesNotContain("phone@example.invalid", message.Text);
            return Task.FromResult(ProviderAcceptance.Accepted);
        });
        var service = new NotificationService(paths, () => starts++, _ => transport);
        reference = Channel(service);
        var settings = new SettingsStore(paths);
        var original = settings.Current.NotificationsEnabled;
        Assert.Equal("Email to phone@example.invalid", service.TestTargetSummary(reference));
        Assert.Equal(ProviderAcceptance.Accepted, await service.SendTestPageAsync(reference));
        Assert.Equal(NotificationState.Accepted, Receipt(paths).State);
        Assert.NotNull(Receipt(paths).FinishedUtc);
        Assert.Single(Directory.GetFiles(Path.Combine(paths.AppDataDir, "notifications"), "channel-test-*.json"));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(0, starts);
        Assert.Empty(service.Outbox.Read());
        Assert.Equal(original, new SettingsStore(paths).Current.NotificationsEnabled);
        Assert.Null(new SettingsStore(paths).Current.NotificationDestination);
    }

    [Fact]
    public async Task MissingReferenceAndPreCancelledClickCannotEnterTransport()
    {
        var paths = Fixture();
        var factories = 0;
        var service = new NotificationService(paths, createTestTransport: _ =>
        {
            factories++;
            throw new InvalidOperationException("Transport must not be constructed.");
        });
        var reference = Channel(service);
        await Assert.ThrowsAnyAsync<Exception>(() => service.SendTestPageAsync(Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendTestPageAsync(reference, cancellation.Token));
        Assert.Equal(0, factories);
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.AppDataDir, "notifications"), "channel-test-*.json"));
    }

    [Theory]
    [InlineData(ProviderAcceptance.NotAccepted)]
    [InlineData(ProviderAcceptance.Unknown)]
    public async Task ProviderResultIsRecordedWithoutRetryOrOutboxEntry(ProviderAcceptance outcome)
    {
        var paths = Fixture();
        var transport = new Transport((_, _, _) => Task.FromResult(outcome));
        var service = new NotificationService(paths, createTestTransport: _ => transport);
        Assert.Equal(outcome, await service.SendTestPageAsync(Channel(service)));
        Assert.Equal(outcome == ProviderAcceptance.NotAccepted ? NotificationState.NotAccepted : NotificationState.Unknown,
            Receipt(paths).State);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(new NotificationService(paths).Outbox.Read());
    }

    [Fact]
    public async Task CancellationAfterEntryIsUnknownAndCannotLeakProviderException()
    {
        var paths = Fixture();
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport((_, _, token) =>
        {
            cancellation.Cancel();
            Assert.True(token.IsCancellationRequested);
            throw new IOException("private-fixture-password provider-token");
        });
        var service = new NotificationService(paths, createTestTransport: _ => transport);
        Assert.Equal(ProviderAcceptance.Unknown, await service.SendTestPageAsync(Channel(service), cancellation.Token));
        Assert.Equal(NotificationState.Unknown, Receipt(paths).State);
        var saved = File.ReadAllText(ReceiptPath(paths));
        Assert.DoesNotContain("private-fixture-password", saved);
        Assert.DoesNotContain("provider-token", saved);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(service.Outbox.Read());
    }
}
