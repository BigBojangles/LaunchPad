using LaunchPad.Models;
using System.Runtime.CompilerServices;

namespace LaunchPad.Services;

public sealed record NotificationPreference(bool Enabled, string? DestinationReference);

public sealed class NotificationService
{
    public NotificationService(AppPaths paths, Action? startDelivery = null)
    {
        Paths = paths;
        _startDelivery = startDelivery ?? (() => NotificationDeliveryOwner.StartIfEnabled(paths));
    }
    private readonly Action _startDelivery;
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
