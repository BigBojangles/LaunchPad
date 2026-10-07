using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;

namespace LaunchPad.Services;

/// <summary>Projects explicit main-agent events; idle and process exit are not run completion.</summary>
public sealed class AgentActivityTracker
{
    public const string LinePrefix = "LP-EVENT ";
    public const string StatePrefix = "LP-STATE ";
    public const int MaxEventBytes = 4096;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 8
    };

    private readonly string _generation;
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _eventOrder = new();
    private readonly HashSet<string> _endedRuns = new(StringComparer.Ordinal);
    private readonly Queue<string> _runOrder = new();
    private string? _agentSessionId;

    public AgentActivityTracker(string generation, AgentActivitySnapshot? previous = null)
    {
        if (!Guid.TryParseExact(generation, "N", out _)) throw new ArgumentException("Invalid activity generation.", nameof(generation));
        _generation = generation;
        if (previous is not null && IsValidSnapshot(previous, generation))
        {
            Snapshot = previous;
            _agentSessionId = previous.Observation?.AgentSessionId ?? previous.LastEvent?.AgentSessionId;
            if (previous.LastEvent is { } last) Remember(_eventIds, _eventOrder, last.EventId);
            if (previous.Observation is { } observation) Remember(_eventIds, _eventOrder, observation.ObservationId);
            if (previous.RetiredRunIds is { } retired)
                foreach (var id in retired.Split(',')) Remember(_endedRuns, _runOrder, id, 64);
            if (!previous.RunActive && previous.RunId is { } ended) Remember(_endedRuns, _runOrder, ended, 64);
        }
    }

    public AgentActivitySnapshot Snapshot { get; private set; } = AgentActivitySnapshot.Unavailable;
    public bool HistoryComplete { get; private set; } = true;

    public static SessionLifecycle SessionState(AgentActivitySnapshot value) => value.State switch
    {
        AgentActivity.Working => SessionLifecycle.Busy,
        AgentActivity.NeedsAttention => SessionLifecycle.NeedsAnswer,
        AgentActivity.Idle => SessionLifecycle.Idle,
        _ => SessionLifecycle.Unknown
    };

    public static string? OutcomeText(AgentActivitySnapshot value) => value.RunActive || value.LastEvent?.RunId != value.RunId ? null : value.LastEvent?.Kind switch
    {
        AgentEventKind.RunFailed => "The last agent run failed; the VM remains open.",
        AgentEventKind.Interrupted => "The last agent run was interrupted; the VM remains open.",
        _ => null
    };

    public bool TryAcceptLine(string line, out AgentActivityEvent? accepted)
    {
        accepted = null;
        if (!line.StartsWith(LinePrefix, StringComparison.Ordinal) || System.Text.Encoding.UTF8.GetByteCount(line) > MaxEventBytes) return false;
        try
        {
            var value = JsonSerializer.Deserialize<AgentActivityEvent>(line.AsSpan(LinePrefix.Length), JsonOptions);
            if (value is null || !TryAccept(value)) return false;
            accepted = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public bool TryAccept(AgentActivityEvent value, out AcceptedAgentActivityEvent? accepted)
    {
        accepted = null;
        if (!TryAccept(value)) return false;
        accepted = new AcceptedAgentActivityEvent(value);
        return true;
    }

    public bool TryAccept(AgentActivityEvent value)
    {
        if (!IsValidEvent(value, _generation) || value.Sequence <= Snapshot.LastSequence || _eventIds.Contains(value.EventId)
            || _agentSessionId is not null && value.AgentSessionId != _agentSessionId) return false;
        var state = Snapshot.State;
        var run = Snapshot.RunId;
        var question = Snapshot.QuestionId;
        var active = Snapshot.RunActive;
        switch (value.Kind)
        {
            case AgentEventKind.Ready:
                if (active || value.RunId is not null) return false;
                state = AgentActivity.Idle;
                question = null;
                break;
            case AgentEventKind.RunStarted:
                if (value.RunId is null || _endedRuns.Contains(value.RunId)) return false;
                if (active) return false;
                run = value.RunId;
                active = true;
                state = AgentActivity.Working;
                question = null;
                break;
            case AgentEventKind.Working:
                if (!active || value.RunId != run) return false;
                state = AgentActivity.Working;
                question = null;
                break;
            case AgentEventKind.NeedsAttention:
                if (!active || value.RunId != run || value.QuestionId is null) return false;
                state = AgentActivity.NeedsAttention;
                question = value.QuestionId;
                break;
            case AgentEventKind.RunFinished:
            case AgentEventKind.Interrupted:
            case AgentEventKind.RunFailed:
                if (!active || value.RunId != run) return false;
                active = false;
                state = AgentActivity.Idle;
                question = null;
                Remember(_endedRuns, _runOrder, run!, 64);
                break;
            default: return false;
        }
        if (value.Sequence != Snapshot.LastSequence + 1) HistoryComplete = false;
        _agentSessionId ??= value.AgentSessionId;
        Remember(_eventIds, _eventOrder, value.EventId);
        Snapshot = new(state, run, question, active, value.Sequence, value, RetiredRunIds: RetiredRuns());
        return true;
    }

    public bool TryAcceptStateLine(string line)
    {
        if (!line.StartsWith(StatePrefix, StringComparison.Ordinal) || System.Text.Encoding.UTF8.GetByteCount(line) > MaxEventBytes) return false;
        try
        {
            var value = JsonSerializer.Deserialize<AgentStateObservation>(line.AsSpan(StatePrefix.Length), JsonOptions);
            return value is not null && TryAcceptState(value);
        }
        catch (JsonException) { return false; }
    }

    public bool TryAcceptState(AgentStateObservation value)
    {
        if (!IsValidState(value, _generation) || value.Sequence <= Snapshot.LastSequence || _eventIds.Contains(value.ObservationId)
            || _agentSessionId is not null && value.AgentSessionId != _agentSessionId
            || value.RunActive && _endedRuns.Contains(value.RunId!)) return false;
        if (value.Sequence != Snapshot.LastSequence + 1
            || value.RunActive != Snapshot.RunActive || value.RunId != Snapshot.RunId
            || value.State != Snapshot.State || value.QuestionId != Snapshot.QuestionId) HistoryComplete = false;
        if (Snapshot.RunActive && Snapshot.RunId is { } superseded && (!value.RunActive || superseded != value.RunId))
            Remember(_endedRuns, _runOrder, superseded, 64);
        if (!value.RunActive && value.RunId is { } ended) Remember(_endedRuns, _runOrder, ended, 64);
        _agentSessionId ??= value.AgentSessionId;
        Remember(_eventIds, _eventOrder, value.ObservationId);
        Snapshot = new(value.State, value.RunId, value.QuestionId, value.RunActive, value.Sequence, Snapshot.LastEvent, value, RetiredRuns());
        return true;
    }

    public static bool IsValidState(AgentStateObservation value, string generation) =>
        value.Version == 1 && value.Generation == generation && value.MainRun && value.Sequence > 0
        && ValidIdentifier(value.ObservationId) && ValidIdentifier(value.AgentSessionId)
        && value.OccurredUtc != default && value.OccurredUtc.Offset == TimeSpan.Zero
        && (value.RunId is null || ValidIdentifier(value.RunId))
        && (value.QuestionId is null || ValidIdentifier(value.QuestionId))
        && (value.State switch
        {
            AgentActivity.Idle => !value.RunActive && value.QuestionId is null,
            AgentActivity.Working => value.RunActive && value.RunId is not null && value.QuestionId is null,
            AgentActivity.NeedsAttention => value.RunActive && value.RunId is not null && value.QuestionId is not null,
            _ => false
        });

    public static bool IsValidEvent(AgentActivityEvent value, string generation) =>
        value.Version == 1 && value.Generation == generation && value.MainRun && value.Sequence > 0
        && Enum.IsDefined(value.Kind) && ValidIdentifier(value.EventId)
        && (value.RunId is null || ValidIdentifier(value.RunId))
        && (value.QuestionId is null || ValidIdentifier(value.QuestionId))
        && (value.AgentSessionId is null || ValidIdentifier(value.AgentSessionId))
        && (value.Kind == AgentEventKind.NeedsAttention ? value.QuestionId is not null : value.QuestionId is null)
        && value.OccurredUtc != default && value.OccurredUtc.Offset == TimeSpan.Zero;

    public static bool IsValidSnapshot(AgentActivitySnapshot value, string generation)
    {
        if (!Enum.IsDefined(value.State) || value.LastSequence < 0) return false;
        if (value.RetiredRunIds is { } retired)
        {
            if (retired.Length > 64 * 129) return false;
            var ids = retired.Split(',');
            if (ids.Length > 64 || ids.Any(id => !ValidIdentifier(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
                || value.RunActive && ids.Contains(value.RunId, StringComparer.Ordinal)) return false;
        }
        if (value.LastEvent is not null && (!IsValidEvent(value.LastEvent, generation) || value.LastEvent.Sequence > value.LastSequence)) return false;
        if (value.Observation is { } observation)
            return IsValidState(observation, generation) && observation.Sequence == value.LastSequence
                && observation.State == value.State && observation.RunId == value.RunId
                && observation.QuestionId == value.QuestionId && observation.RunActive == value.RunActive
                && (value.LastEvent?.AgentSessionId is not { } session || session == observation.AgentSessionId);
        if (value.LastEvent is null) return value == AgentActivitySnapshot.Unavailable;
        if (value.LastEvent.Sequence != value.LastSequence) return false;
        if (value.RunId is not null && !ValidIdentifier(value.RunId)) return false;
        if (value.QuestionId is not null && !ValidIdentifier(value.QuestionId)) return false;
        if (value.LastEvent.Kind != AgentEventKind.Ready && value.LastEvent.RunId != value.RunId) return false;
        return value.State switch
        {
            AgentActivity.Unknown => false,
            AgentActivity.Idle => !value.RunActive && value.QuestionId is null
                && value.LastEvent.Kind is AgentEventKind.Ready or AgentEventKind.RunFinished or AgentEventKind.Interrupted or AgentEventKind.RunFailed,
            AgentActivity.Working => value.RunActive && value.RunId is not null && value.QuestionId is null
                && value.LastEvent.Kind is AgentEventKind.RunStarted or AgentEventKind.Working,
            AgentActivity.NeedsAttention => value.RunActive && value.RunId is not null && value.QuestionId is not null
                && value.LastEvent.Kind == AgentEventKind.NeedsAttention && value.LastEvent.QuestionId == value.QuestionId,
            _ => false
        };
    }

    internal static bool ValidIdentifier(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');

    private string? RetiredRuns() => _runOrder.Count == 0 ? null : string.Join(',', _runOrder);

    private static void Remember(HashSet<string> values, Queue<string> order, string value, int limit = 1024)
    {
        if (!values.Add(value)) return;
        order.Enqueue(value);
        if (order.Count > limit) values.Remove(order.Dequeue());
    }
}
