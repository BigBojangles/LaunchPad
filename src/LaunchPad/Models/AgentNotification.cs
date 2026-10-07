namespace LaunchPad.Models;

public enum AgentNotificationKind { NeedsAttention, RunEnded }
public enum NotificationState { Pending, Sending, Accepted, NotAccepted, Unknown, Suppressed }
public enum ProviderAcceptance { Accepted, NotAccepted, Unknown }
public enum AgentNotificationOutcome { NeedsAttention, Finished, Failed, Interrupted }

public sealed record NotificationConsent(bool GlobalEnabled = false, bool ProjectEnabled = false,
    bool Configured = false, string? DestinationReference = null, string? Epoch = null)
{
    public bool Allows(string reference, string? epoch = null) => GlobalEnabled && ProjectEnabled && Configured
        && DestinationReference == reference && Epoch == epoch;
}

// Delivery payload intentionally excludes project paths, transcripts and credentials.
public sealed record AgentNotificationMessage(string ProjectName, string AgentName, string RunId,
    AgentNotificationKind Kind, AgentNotificationOutcome Outcome, DateTimeOffset OccurredUtc)
{
    public string Subject => ProjectName + (Kind == AgentNotificationKind.NeedsAttention ? ": agent needs attention" : ": agent run ended");
    public string Text => $"Project: {ProjectName}\nAgent: {AgentName}\nRun: {RunId}\n{OutcomeText}\nTime: {OccurredUtc:u}";
    public string OutcomeText => Outcome switch
    {
        AgentNotificationOutcome.NeedsAttention => "The agent is waiting for a question, choice or approval.",
        AgentNotificationOutcome.Failed => "The agent run failed.",
        AgentNotificationOutcome.Interrupted => "The agent run was interrupted.",
        _ => "The agent run finished. Project completion and tests are separate."
    };
}

public sealed record NotificationItem(string Id, string ProjectPath, string AgentId, string AgentSessionId,
    string RunId, string? QuestionId, AgentNotificationKind Kind, AgentNotificationOutcome Outcome, DateTimeOffset OccurredUtc,
    string DestinationReference, NotificationState State = NotificationState.Pending, string? Status = null, string? ConsentEpoch = null);

public interface INotificationTransport
{
    // Accepted means the provider accepted the message, not phone delivery/read.
    Task<ProviderAcceptance> SendAsync(string destinationReference, AgentNotificationMessage message, CancellationToken cancellation);
}
