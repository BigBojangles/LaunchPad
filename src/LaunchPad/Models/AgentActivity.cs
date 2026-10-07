namespace LaunchPad.Models;

public enum AgentActivity { Unknown, Idle, Working, NeedsAttention }
public enum AgentEventKind { Ready, RunStarted, Working, NeedsAttention, RunFinished, Interrupted, RunFailed }

/// <summary>A lifecycle event accepted by the host tracker; never a state-only observation.</summary>
public sealed class AcceptedAgentActivityEvent
{
    internal AcceptedAgentActivityEvent(AgentActivityEvent value) => Value = value;
    public AgentActivityEvent Value { get; }
}

/// <summary>Advisory agent telemetry. Never authorizes a host operation or changes agent policy.</summary>
public sealed record AgentActivityEvent(
    int Version,
    string Generation,
    long Sequence,
    string EventId,
    AgentEventKind Kind,
    DateTimeOffset OccurredUtc,
    string? RunId = null,
    string? QuestionId = null,
    bool MainRun = true,
    string? AgentSessionId = null);

/// <summary>Current producer state, including health. This is never a historical run outcome.</summary>
public sealed record AgentStateObservation(
    int Version,
    string Generation,
    long Sequence,
    string ObservationId,
    DateTimeOffset OccurredUtc,
    string AgentSessionId,
    AgentActivity State,
    string? RunId = null,
    string? QuestionId = null,
    bool RunActive = false,
    bool MainRun = true);

public sealed record AgentActivitySnapshot(
    AgentActivity State,
    string? RunId,
    string? QuestionId,
    bool RunActive,
    long LastSequence,
    AgentActivityEvent? LastEvent,
    AgentStateObservation? Observation = null,
    string? RetiredRunIds = null)
{
    public static AgentActivitySnapshot Unavailable { get; } = new(AgentActivity.Unknown, null, null, false, 0, null);
}
