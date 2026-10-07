using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record GrokUpdateProjectionState([property: JsonRequired] int Schema,
    [property: JsonRequired] GrokActivityBinding Binding, [property: JsonRequired] AgentActivitySnapshot Activity,
    [property: JsonRequired] bool HistoryComplete, [property: JsonRequired] GrokUpdateFingerprint[] Recent,
    [property: JsonRequired] GrokPendingActivity[] Pending);

/// <summary>Durable current-prompt projection. Stop hooks are diagnostic only;
/// only matched turn_completed metadata supplies a run outcome. No agent writes.</summary>
public sealed class GrokUpdateProjection
{
    public const string StateFile = "grok-update-projection.json";
    private readonly string _directory;
    private readonly GrokActivityBinding _binding;
    private readonly TimeProvider _time;
    private long _liveSequence;
    private long _liveTimestamp;
    private TimeSpan _initialAge;
    private bool _live;

    public GrokUpdateProjection(string directory, GrokActivityBinding binding, TimeProvider? time = null)
    {
        _ = new GrokActivityFeed(directory, binding, time); // shared binding validation
        _directory = Path.GetFullPath(directory); _binding = binding; _time = time ?? TimeProvider.System;
    }
    public GrokUpdateProjectionState Read() { using var lease = Lease(); return ReadLocked(); }

    public void Ingest(GrokUpdateCaptureState capture, IReadOnlyList<string>? newlyCapturedLiveIds = null, bool retainEvents = true)
    {
        if (capture.Binding != _binding || capture.Pending.Any(row => !GrokUpdateCapture.ValidMetadata(row)))
            throw new InvalidDataException("Update projection does not match its source binding.");
        using var lease = Lease(); var previous = ReadLocked();
        var tracker = new AgentActivityTracker(_binding.Generation, previous.Activity);
        var recent = previous.Recent.ToList(); var pending = previous.Pending.ToList();
        var complete = previous.HistoryComplete && capture.HistoryComplete;
        GrokUpdateMetadata? advancedLive = null;
        foreach (var row in capture.Pending)
        {
            // Include original capture fields here: retries must retain the same
            // host consent and eligibility, not merely the same source body.
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row)));
            var known = recent.FirstOrDefault(item => item.EventId == row.EventId);
            if (known is not null)
            { if (known.Hash != hash) throw new InvalidDataException("Conflicting captured update; projection preserved."); continue; }
            var before = tracker.Snapshot.LastSequence;
            AgentEventKind? kind = null;
            if (row.PromptId?.StartsWith("task-completed-", StringComparison.Ordinal) == true) complete = false;
            else switch (row.Kind)
            {
                case "session_start":
                    if (before == 0) kind = AgentEventKind.Ready;
                    break;
                case "user_prompt_submit":
                    if (tracker.Snapshot.RunActive && tracker.Snapshot.RunId != row.PromptId)
                    {
                        // A new root prompt supersedes unresolved prior work,
                        // without inventing a completed outcome for that work.
                        tracker.TryAcceptState(new(1, _binding.Generation, before + 1, row.EventId, row.OccurredUtc,
                            _binding.RootSessionId, AgentActivity.Working, row.PromptId, RunActive: true));
                        complete = false;
                    }
                    else if (!tracker.Snapshot.RunActive) kind = AgentEventKind.RunStarted;
                    break;
                case "agent_message_chunk": case "agent_thought_chunk": case "tool_call": case "tool_call_update":
                    if (row.PromptId is not null && tracker.Snapshot.RunActive && row.PromptId == tracker.Snapshot.RunId)
                        kind = AgentEventKind.Working;
                    break;
                case "turn_completed":
                    if (tracker.Snapshot.RunActive && row.PromptId == tracker.Snapshot.RunId)
                        kind = row.StopReason switch { "end_turn" => AgentEventKind.RunFinished, "error" => AgentEventKind.RunFailed,
                            "cancelled" or "max_tokens" => AgentEventKind.Interrupted, _ => null };
                    else complete = false;
                    break;
                // Stop precedes messages in real streams. It is not a final
                // completion or an actual question/choice awaiting attention.
                case "stop": case "stop_failure": break;
            }
            if (kind is { } eventKind)
            {
                var value = new AgentActivityEvent(1, _binding.Generation, tracker.Snapshot.LastSequence + 1,
                    row.EventId, eventKind, row.OccurredUtc, eventKind == AgentEventKind.Ready ? null : row.PromptId,
                    AgentSessionId: _binding.RootSessionId);
                if (tracker.TryAccept(value, out var accepted))
                {
                    if (retainEvents)
                    {
                        if (pending.Count >= GrokActivityFeed.MaxPending) throw new IOException("Unpublished update events preserved at capacity.");
                        pending.Add(new(accepted!.Value, row.LiveEligible ? row.CapturedConsent : null));
                    }
                }
                else complete = false;
            }
            if (tracker.Snapshot.LastSequence > before)
                advancedLive = row.LiveEligible && newlyCapturedLiveIds?.Contains(row.EventId) == true ? row : null;
            recent.Add(new(row.EventId, hash)); if (recent.Count > 512) recent.RemoveAt(0);
        }
        var next = new GrokUpdateProjectionState(1, _binding, tracker.Snapshot, complete && tracker.HistoryComplete,
            recent.ToArray(), pending.ToArray());
        Save(next);
        if (next.Activity.LastSequence != previous.Activity.LastSequence)
        {
            _live = advancedLive is not null;
            _liveSequence = next.Activity.LastSequence; _liveTimestamp = _time.GetTimestamp();
            _initialAge = advancedLive is null ? TimeSpan.Zero : _time.GetUtcNow() - advancedLive.OccurredUtc;
        }
    }

    public bool IsCurrent(GrokUpdateProjectionState state)
    {
        var age = _initialAge + _time.GetElapsedTime(_liveTimestamp);
        return _live && state.Activity.LastSequence == _liveSequence && age >= TimeSpan.Zero && age <= SessionActivityStore.Freshness;
    }

    public void Publish(bool connected, SessionActivityContext context, NotificationOutbox? outbox = null,
        Action<GrokActivityPublication>? publish = null)
    {
        using var lease = Lease(); var state = ReadLocked();
        if (context.Generation != _binding.Generation || context.ProjectPath != _binding.HostProject || context.AgentId != AgentChoice.Grok)
            throw new InvalidOperationException("Update publication does not match its owner.");
        var value = new GrokActivityPublication(context, state.Activity, connected, state.Pending.Select(row => row.Event).ToArray(),
            connected && IsCurrent(state), state.HistoryComplete);
        if (publish is not null) publish(value);
        else SessionActivityStore.Publish(context, value.Activity, connected, value.Events, value.Synchronized, value.HistoryComplete);
        foreach (var item in state.Pending)
        {
            if (item.Event.Kind is not (AgentEventKind.RunFinished or AgentEventKind.RunFailed or AgentEventKind.Interrupted)
                || item.Consent is not { } consent || !consent.Allows(consent.DestinationReference ?? "", consent.Epoch)) continue;
            if (outbox is null) throw new IOException("Notification outbox unavailable; projection retained.");
            outbox.Queue(new AcceptedAgentActivityEvent(item.Event), _binding.HostProject, AgentChoice.Grok, consent);
        }
        if (state.Pending.Length > 0) Save(state with { Pending = [] });
    }

    private string Safe(string name) => FenceFiles.TryResolveUnlinked(_directory, name, out var path)
        ? path : throw new IOException("Update projection path is unavailable.");
    private FileStream Lease() { var path = Safe(StateFile + ".lock"); Directory.CreateDirectory(_directory); return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private GrokUpdateProjectionState ReadLocked()
    {
        var path = Safe(StateFile);
        if (!File.Exists(path)) return new(1, _binding, AgentActivitySnapshot.Unavailable, true, [], []);
        try
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Update projection is too large.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            static bool Unique(JsonElement item) => item.ValueKind == JsonValueKind.Object && !item.EnumerateObject().GroupBy(row => row.Name).Any(group => group.Count() > 1);
            if (!Unique(document.RootElement)
                || !document.RootElement.TryGetProperty("binding", out var binding) || !Unique(binding)
                || !document.RootElement.TryGetProperty("activity", out var activity) || !Unique(activity)
                || !document.RootElement.TryGetProperty("pending", out var pending) || pending.ValueKind != JsonValueKind.Array
                || pending.EnumerateArray().Any(row => !Unique(row)
                    || !row.TryGetProperty("event", out var activityEvent) || !Unique(activityEvent)
                    || row.TryGetProperty("consent", out var consent) && consent.ValueKind != JsonValueKind.Null && !Unique(consent)))
                throw new InvalidDataException("Ambiguous update projection.");
            var state = JsonSerializer.Deserialize<GrokUpdateProjectionState>(document.RootElement, JsonFile.Options);
            if (state is not { Schema: 1, Activity: not null, Recent: not null, Pending: not null } || state.Binding != _binding
                || !AgentActivityTracker.IsValidSnapshot(state.Activity, _binding.Generation)
                || state.Activity != AgentActivitySnapshot.Unavailable && (state.Activity.LastEvent?.AgentSessionId ?? state.Activity.Observation?.AgentSessionId) != _binding.RootSessionId
                || state.Recent.Length > 512 || state.Recent.Any(row => row is null || !AgentActivityTracker.ValidIdentifier(row.EventId)
                    || row.Hash is not { Length: 64 } || !row.Hash.All(Uri.IsHexDigit)) || state.Recent.Select(row => row.EventId).Distinct().Count() != state.Recent.Length
                || state.Pending.Length > GrokActivityFeed.MaxPending || state.Pending.Any(row => row is null || row.Event is null
                    || !AgentActivityTracker.IsValidEvent(row.Event, _binding.Generation) || row.Event.AgentSessionId != _binding.RootSessionId
                    || row.Event.Sequence > state.Activity.LastSequence
                    || row.Consent?.DestinationReference is { } reference && !Guid.TryParseExact(reference, "N", out _))
                || state.Pending.Select(row => row.Event.EventId).Distinct().Count() != state.Pending.Length)
                throw new InvalidDataException("Update projection is invalid or belongs to another launch.");
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Update projection was preserved after a read failure.", error); }
    }
    private void Save(GrokUpdateProjectionState state)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(state, JsonFile.Options).Length > 512 * 1024) throw new IOException("Update projection capacity exceeded; previous state retained.");
        ReturnRecovery.SaveAtomic(Safe(StateFile), state);
    }
}
