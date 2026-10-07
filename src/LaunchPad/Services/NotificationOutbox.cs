using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Host-only durable semantic dedupe. No providers, guest credentials or UI inference.</summary>
public sealed class NotificationOutbox
{
    public sealed class Ledger
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public List<NotificationItem> Items { get; set; } = [];
    }

    private readonly string _directory;
    private readonly int _capacity;
    public string LedgerPath => Path.Combine(_directory, "outbox.json");

    public NotificationOutbox(string directory, int capacity = 20000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _directory = Path.GetFullPath(directory);
        _capacity = capacity;
        SafePath("outbox.json");
        Directory.CreateDirectory(_directory);
    }

    public bool Queue(AcceptedAgentActivityEvent accepted, string projectPath, string agentId, NotificationConsent consent)
    {
        var value = accepted.Value;
        if (!consent.GlobalEnabled || !consent.ProjectEnabled || !consent.Configured
            || !Guid.TryParseExact(consent.DestinationReference, "N", out _)) return false;
        if (value.AgentSessionId is null || value.RunId is null || !value.MainRun) return false;
        var kind = value.Kind switch
        {
            AgentEventKind.NeedsAttention => AgentNotificationKind.NeedsAttention,
            AgentEventKind.RunFinished or AgentEventKind.RunFailed or AgentEventKind.Interrupted => AgentNotificationKind.RunEnded,
            _ => (AgentNotificationKind?)null
        };
        if (kind is null) return false;
        if (!AgentChoice.Known(agentId)) throw new ArgumentException("Unknown notification agent.");
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        var outcome = value.Kind switch
        {
            AgentEventKind.NeedsAttention => AgentNotificationOutcome.NeedsAttention,
            AgentEventKind.RunFailed => AgentNotificationOutcome.Failed,
            AgentEventKind.Interrupted => AgentNotificationOutcome.Interrupted,
            _ => AgentNotificationOutcome.Finished
        };
        var id = SemanticId(project, agentId, value.AgentSessionId, value.RunId, kind.Value, value.QuestionId);
        return Change(ledger =>
        {
            if (ledger.Items.Any(item => item.Id == id)) return (false, false);
            if (ledger.Items.Count >= _capacity) throw new IOException("Notification history is full. No history was discarded or alert sent.");
            ledger.Items.Add(new(id, project, agentId, value.AgentSessionId, value.RunId, value.QuestionId,
                kind.Value, outcome, value.OccurredUtc, consent.DestinationReference!, ConsentEpoch: consent.Epoch));
            return (true, true);
        });
    }

    public IReadOnlyList<NotificationItem> Read() => Change(ledger => (ledger.Items.ToArray(), false));

    public int RecoverAbandonedSends() => Change(ledger =>
    {
        var recovered = 0;
        for (var index = 0; index < ledger.Items.Count; index++)
        {
            var item = ledger.Items[index];
            if (item.State != NotificationState.Sending) continue;
            using var lease = TryLease(item.Id);
            if (lease is null) continue; // A live sender still owns this attempt.
            ledger.Items[index] = item with { State = NotificationState.Unknown, Status = "Sender stopped without provider confirmation; automatic resend disabled." };
            recovered++;
        }
        return (recovered, recovered != 0);
    });

    public async Task<bool> DispatchOneAsync(INotificationTransport transport,
        Func<string, NotificationConsent> currentConsent, Func<string, string> displayName, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        FileStream? attempt = null;
        NotificationItem? claimed;
        try
        {
            claimed = Change<NotificationItem?>(ledger =>
            {
                var index = ledger.Items.FindIndex(item => item.State == NotificationState.Pending);
                if (index < 0) return (null, false);
                var item = ledger.Items[index];
                if (!currentConsent(item.ProjectPath).Allows(item.DestinationReference, item.ConsentEpoch))
                {
                    ledger.Items[index] = item with { State = NotificationState.Suppressed, Status = "Notifications disabled or destination changed before dispatch." };
                    return (null, true);
                }
                attempt = TryLease(item.Id);
                if (attempt is null) return (null, false);
                cancellation.ThrowIfCancellationRequested();
                var claim = item with { State = NotificationState.Sending, Status = "Sending; provider acceptance not yet known." };
                ledger.Items[index] = claim;
                return (claim, true); // This durable write must precede transport entry.
            });
            if (claimed is null) return false;
            ProviderAcceptance outcome;
            var suppressed = false;
            try
            {
                var name = CleanName(displayName(claimed.ProjectPath));
                var message = new AgentNotificationMessage(name, AgentChoice.Find(claimed.AgentId)!.Label,
                    claimed.RunId, claimed.Kind, claimed.Outcome, claimed.OccurredUtc);
                // Settings may change after the durable claim or while resolving
                // the display name. Recheck immediately before transport entry.
                // This cannot recall a message already handed to the provider.
                suppressed = !currentConsent(claimed.ProjectPath).Allows(claimed.DestinationReference, claimed.ConsentEpoch);
                outcome = suppressed ? ProviderAcceptance.NotAccepted
                    : await transport.SendAsync(claimed.DestinationReference, message, cancellation).ConfigureAwait(false);
                if (!Enum.IsDefined(outcome)) outcome = ProviderAcceptance.Unknown;
            }
            catch (Exception)
            {
                // Exception text can contain a token-bearing URI or password.
                outcome = ProviderAcceptance.Unknown;
            }
            Change(ledger =>
            {
                var index = ledger.Items.FindIndex(item => item.Id == claimed.Id);
                if (index < 0 || ledger.Items[index].State != NotificationState.Sending)
                    throw new IOException("The notification claim changed during dispatch; acceptance cannot be confirmed.");
                ledger.Items[index] = claimed with
                {
                    State = suppressed ? NotificationState.Suppressed : outcome switch { ProviderAcceptance.Accepted => NotificationState.Accepted,
                        ProviderAcceptance.NotAccepted => NotificationState.NotAccepted, _ => NotificationState.Unknown },
                    Status = suppressed ? "Notifications disabled or destination changed before transport; nothing was sent."
                        : outcome switch { ProviderAcceptance.Accepted => "Accepted by provider; phone delivery is not verified.",
                        ProviderAcceptance.NotAccepted => "Not accepted by provider; no automatic retry.", _ => "Acceptance unknown; automatic resend disabled." }
                };
                return (true, true);
            });
            return !suppressed;
        }
        finally { attempt?.Dispose(); }
    }

    private T Change<T>(Func<Ledger, (T Value, bool Changed)> action)
    {
        using var lease = new FileStream(SafePath("outbox.guard"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = SafePath("outbox.json");
        if (File.Exists(path) && new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("Notification history is too large; it was preserved.");
        var loaded = JsonFile.Load<Ledger>(path);
        Validate(loaded.Value);
        var result = action(loaded.Value);
        if (result.Changed)
        {
            SafePath("outbox.json.lock");
            JsonFile.Save(path, loaded.Value, loaded.Hash);
        }
        return result.Value;
    }

    private void Validate(Ledger ledger)
    {
        if (ledger.Version != 1 || ledger.Items is null || ledger.Items.Count > _capacity
            || ledger.Items.Any(item => item is null || !Enum.IsDefined(item.State) || !Enum.IsDefined(item.Kind)
                || !Enum.IsDefined(item.Outcome) || (item.Kind == AgentNotificationKind.NeedsAttention) != (item.Outcome == AgentNotificationOutcome.NeedsAttention)
                || !AgentChoice.Known(item.AgentId) || !AgentActivityTracker.ValidIdentifier(item.AgentSessionId)
                || !AgentActivityTracker.ValidIdentifier(item.RunId)
                || (item.Kind == AgentNotificationKind.NeedsAttention ? !AgentActivityTracker.ValidIdentifier(item.QuestionId) : item.QuestionId is not null)
                || item.OccurredUtc == default || item.OccurredUtc.Offset != TimeSpan.Zero
                || string.IsNullOrWhiteSpace(item.ProjectPath) || !Path.IsPathFullyQualified(item.ProjectPath)
                || !Guid.TryParseExact(item.DestinationReference, "N", out _)
                || item.Id != SemanticId(item.ProjectPath, item.AgentId, item.AgentSessionId, item.RunId, item.Kind, item.QuestionId))
            || ledger.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != ledger.Items.Count)
            throw new IOException("Notification history is invalid; it was preserved and nothing was sent.");
    }

    private FileStream? TryLease(string id)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) throw new IOException("Invalid notification attempt identity.");
        try { return new FileStream(SafePath(id + ".sending"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private string SafePath(string name) => FenceFiles.TryResolveUnlinked(_directory, name, out var path)
        ? path : throw new IOException("Notification storage contains a link or unsafe path.");

    private static string SemanticId(string project, string agent, string session, string run, AgentNotificationKind kind, string? question)
    {
        project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        if (OperatingSystem.IsWindows()) project = project.ToUpperInvariant();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { project, agent, session, run, kind.ToString(),
            kind == AgentNotificationKind.NeedsAttention ? question ?? "" : "" });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string CleanName(string name)
    {
        name = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length == 0) name = "Project";
        return name.Length <= 128 ? name : name[..127] + "…";
    }
}
