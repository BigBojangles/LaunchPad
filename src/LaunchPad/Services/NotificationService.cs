using LaunchPad.Models;
using System.Runtime.CompilerServices;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record NotificationPreference(bool Enabled, string? DestinationReference);
public sealed class NotificationDisconnectException() : IOException("Alerts are disabled, but some locally saved notification credentials could not be removed.") { }
public sealed record NotificationTestReceipt(int Version, string TestId, string DestinationReference,
    DateTimeOffset RequestedUtc, NotificationState State, DateTimeOffset? FinishedUtc = null);

public sealed class NotificationService
{
    public NotificationService(AppPaths paths, Action? startDelivery = null,
        Func<NotificationDestinationStore, INotificationTransport>? createTestTransport = null)
    {
        Paths = paths;
        _startDelivery = startDelivery ?? (() => NotificationDeliveryOwner.StartIfEnabled(paths));
        _createTestTransport = createTestTransport ?? (store => NotificationTransport.Create(store));
    }
    private readonly Action _startDelivery;
    private readonly Func<NotificationDestinationStore, INotificationTransport> _createTestTransport;
    private sealed record CapturedConsent(NotificationConsent? Value);
    private sealed class Captures
    {
        public Dictionary<(string Project, string Agent), CapturedConsent> Values { get; } = [];
    }
    private readonly ConditionalWeakTable<AcceptedAgentActivityEvent, Captures> _consents = new();
    public AppPaths Paths { get; }
    public NotificationDestinationStore Destinations => new(Path.Combine(Paths.AppDataDir, "notifications", "destinations"), new WindowsNotificationSecretProtector());
    public NotificationOutbox Outbox => new(Path.Combine(Paths.AppDataDir, "notifications", "outbox"));
    public void StartDelivery() => _startDelivery();

    public int Disconnect(SettingsStore settings)
    {
        if (!Path.GetFullPath(settings.Paths.AppDataDir).Equals(Path.GetFullPath(Paths.AppDataDir),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Notification settings belong to a different profile.");
        // Persist revocation before deleting. A failed settings save must not
        // remove credentials, while an erase failure must leave delivery off.
        var destinations = Destinations;
        using var guard = destinations.AcquireGuard();
        settings.DisconnectNotifications();
        try { return destinations.ForgetAllUnderGuard(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new NotificationDisconnectException(); }
    }

    public string TestTargetSummary(string reference)
    {
        var destination = Destinations.Read(reference);
        return destination.Provider switch
        {
            NotificationProvider.Email => "Email to " + destination.To,
            NotificationProvider.Telegram => "Telegram chat " + destination.ChatId,
            NotificationProvider.Discord => "Saved Discord webhook",
            _ => "ntfy topic " + destination.Topic
        };
    }

    // Only the explicit Settings click calls this. No activity event, opt-in
    // mutation or outbox item is fabricated to test a provider.
    public async Task<ProviderAcceptance> SendTestPageAsync(string reference, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var store = Destinations;
        store.Read(reference); // Validate the pinned reference before transport entry.
        var root = Path.Combine(Paths.AppDataDir, "notifications");
        if (!FenceFiles.TryResolveUnlinked(root, "channel-test.lease", out var leasePath))
            throw new IOException("Test page storage is unavailable.");
        using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var testId = Guid.NewGuid().ToString("N");
        if (!FenceFiles.TryResolveUnlinked(root, "channel-test-" + testId + ".json", out var receiptPath))
            throw new IOException("Test page storage is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var receipt = new NotificationTestReceipt(1, testId, reference, DateTimeOffset.UtcNow, NotificationState.Sending);
        var transport = _createTestTransport(store);
        using var disposable = transport as IDisposable;
        ReturnRecovery.SaveAtomic(receiptPath, receipt); // Durable before a possible send.
        ProviderAcceptance result;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            result = await transport.SendAsync(reference, new AgentNotificationMessage("LaunchPad", "Channel test", testId,
                AgentNotificationKind.ChannelTest, AgentNotificationOutcome.ChannelTest, receipt.RequestedUtc), timeout.Token).ConfigureAwait(false);
            if (!Enum.IsDefined(result)) result = ProviderAcceptance.Unknown;
        }
        catch { result = ProviderAcceptance.Unknown; } // Do not retain credential-bearing exceptions or retry.
        var state = result switch
        {
            ProviderAcceptance.Accepted => NotificationState.Accepted,
            ProviderAcceptance.NotAccepted => NotificationState.NotAccepted,
            _ => NotificationState.Unknown
        };
        ReturnRecovery.SaveAtomic(receiptPath, receipt with { State = state, FinishedUtc = DateTimeOffset.UtcNow });
        return result;
    }
    public bool Queue(AcceptedAgentActivityEvent accepted, string project, string agent)
    {
        if (accepted.Value.Kind is not (AgentEventKind.NeedsAttention or AgentEventKind.RunFinished or AgentEventKind.RunFailed or AgentEventKind.Interrupted)) return false;
        try
        {
            // Capture once, even when a disk failure requires retrying this exact accepted event.
            var captures = _consents.GetValue(accepted, _ => new());
            var binding = (OperatingSystem.IsWindows() ? Path.GetFullPath(project).ToUpperInvariant() : Path.GetFullPath(project), agent);
            NotificationConsent? consent;
            lock (captures)
            {
                if (!captures.Values.TryGetValue(binding, out var captured))
                {
                    try { captured = new(new SettingsStore(Paths).NotificationConsentFor(project)); }
                    catch { captured = new(null); }
                    captures.Values[binding] = captured;
                }
                consent = captured.Value;
            }
            if (consent is null) { NotificationDeliveryOwner.ReportWarning(Paths); return false; }
            if (!consent.Allows(consent.DestinationReference ?? "", consent.Epoch)) return false;
            var queued = Outbox.Queue(accepted, project, agent, consent);
            StartDelivery();
            return queued;
        }
        catch { NotificationDeliveryOwner.ReportWarning(Paths); throw; }
    }
}
