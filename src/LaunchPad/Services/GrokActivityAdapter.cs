using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;

namespace LaunchPad.Services;

public sealed record GrokActivityCheckpoint(string Generation, string RootSessionId, string Project,
    AgentActivitySnapshot Activity, bool HistoryComplete, string[] BackgroundTaskIds, string? WakeupPromptId, string? WorkspaceRoot = null);

/// <summary>Projects identified passive Grok callbacks. This does not install hooks or authorize operations.</summary>
public sealed class GrokActivityAdapter
{
    public const int MaxInputBytes = 64 * 1024;
    private readonly string _session;
    private readonly string _project;
    private readonly string _generation;
    private readonly string _workspace;
    private readonly TimeProvider _time;
    private readonly AgentActivityTracker _tracker;
    private readonly bool _previousHistoryComplete;
    private readonly HashSet<string> _backgroundTasks = new(StringComparer.Ordinal);
    private string? _wakeupPrompt;
    private const string TaskWakeupPrefix = "task-completed-";

    public GrokActivityAdapter(string generation, string rootSessionId, string project,
        AgentActivitySnapshot? previous = null, TimeProvider? time = null, bool previousHistoryComplete = true,
        GrokActivityCheckpoint? checkpoint = null, string? workspaceRoot = null)
    {
        if (checkpoint is not null)
        {
            if (previous is not null || checkpoint.Generation != generation || checkpoint.RootSessionId != rootSessionId
                || checkpoint.Project != project || (checkpoint.WorkspaceRoot ?? project) != (workspaceRoot ?? project) || checkpoint.BackgroundTaskIds is null
                || checkpoint.BackgroundTaskIds.Length > 64
                || checkpoint.BackgroundTaskIds.Any(id => !ValidTaskId(id))
                || checkpoint.BackgroundTaskIds.Distinct(StringComparer.Ordinal).Count() != checkpoint.BackgroundTaskIds.Length
                || checkpoint.Activity is null || !AgentActivityTracker.IsValidSnapshot(checkpoint.Activity, generation)
                || !checkpoint.Activity.RunActive && (checkpoint.BackgroundTaskIds.Length > 0 || checkpoint.WakeupPromptId is not null)
                || checkpoint.WakeupPromptId is { } wakeup && (!wakeup.StartsWith(TaskWakeupPrefix, StringComparison.Ordinal)
                    || !checkpoint.BackgroundTaskIds.Contains(wakeup[TaskWakeupPrefix.Length..], StringComparer.Ordinal)))
                throw new ArgumentException("Invalid Grok activity checkpoint.");
            previous = checkpoint.Activity;
            previousHistoryComplete = checkpoint.HistoryComplete;
            _backgroundTasks.UnionWith(checkpoint.BackgroundTaskIds);
            _wakeupPrompt = checkpoint.WakeupPromptId;
        }
        if (!AgentActivityTracker.ValidIdentifier(rootSessionId)) throw new ArgumentException("Invalid Grok session identity.");
        if (string.IsNullOrWhiteSpace(project) || project.Length > 32768 || project.Any(char.IsControl))
            throw new ArgumentException("Invalid Grok project identity.");
        if (previous is not null && previous != AgentActivitySnapshot.Unavailable
            && (previous.Observation?.AgentSessionId ?? previous.LastEvent?.AgentSessionId) != rootSessionId)
            throw new ArgumentException("The saved activity belongs to another Grok session.");
        _generation = generation;
        _session = rootSessionId;
        _project = project;
        _workspace = workspaceRoot ?? project;
        if (string.IsNullOrWhiteSpace(_workspace) || _workspace.Length > 32768 || _workspace.Any(char.IsControl))
            throw new ArgumentException("Invalid Grok workspace identity.");
        _time = time ?? TimeProvider.System;
        _tracker = new(generation, previous);
        _previousHistoryComplete = previousHistoryComplete;
    }

    public AgentActivitySnapshot Snapshot => _tracker.Snapshot;
    public bool HistoryComplete => _previousHistoryComplete && _tracker.HistoryComplete;
    public GrokActivityCheckpoint Checkpoint => new(_generation, _session, _project, Snapshot, HistoryComplete,
        _backgroundTasks.Order(StringComparer.Ordinal).ToArray(), _wakeupPrompt, _workspace);

    public bool TryAccept(string json, out AcceptedAgentActivityEvent? accepted)
    {
        accepted = null;
        if (Encoding.UTF8.GetByteCount(json) > MaxInputBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!keys.Add(property.Name) || keys.Count > 64) return false;
            if (!Field(value, "sessionId", "session_id", out var session) || session != _session
                || !Field(value, "cwd", null, out var cwd) || cwd != _project
                || !Field(value, "workspaceRoot", "workspace_root", out var workspace) || workspace is not null && workspace != _workspace
                || HasIdentity(value, "agentId", "agent_id") || HasIdentity(value, "subagentId", "subagent_id")
                || HasIdentity(value, "parentSessionId", "parent_session_id") || HasIdentity(value, "subagentType", "subagent_type")) return false;
            if (!EventName(value, out var name) || !Field(value, "promptId", "prompt_id", out var prompt)) return false;
            var generatedWakeup = prompt?.StartsWith(TaskWakeupPrefix, StringComparison.Ordinal) == true;
            if (generatedWakeup && (!Snapshot.RunActive || !_backgroundTasks.Contains(prompt![TaskWakeupPrefix.Length..])))
                return false;
            if (generatedWakeup && name != "userpromptsubmit" && prompt != _wakeupPrompt) return false;
            var run = generatedWakeup ? Snapshot.RunId : prompt;
            if (name == "stop" && Snapshot.RunActive && run == Snapshot.RunId)
                RememberBackgroundTasks(value);
            // A new identified user turn is authoritative even when the prior
            // Stop was ambiguous (background work, continuation or missing
            // fields). Resync without inventing a finish for the prior run.
            if (name == "userpromptsubmit" && !generatedWakeup && AgentActivityTracker.ValidIdentifier(prompt)
                && Snapshot.RunActive && Snapshot.RunId != prompt)
            {
                var resynced = _tracker.TryAcceptState(new(1, _generation, Snapshot.LastSequence + 1,
                    EventId(prompt, "resync"), _time.GetUtcNow(), _session,
                    AgentActivity.Working, prompt, RunActive: true));
                if (resynced) ClearBackgroundTasks();
                return resynced;
            }
            var kind = name switch
            {
                "sessionstart" when prompt is null => AgentEventKind.Ready,
                "userpromptsubmit" when generatedWakeup => AgentEventKind.Working,
                "userpromptsubmit" when AgentActivityTracker.ValidIdentifier(prompt) => AgentEventKind.RunStarted,
                "stop" when AgentActivityTracker.ValidIdentifier(prompt) && IsUnambiguousStop(value) => AgentEventKind.RunFinished,
                "stopfailure" when AgentActivityTracker.ValidIdentifier(prompt) => AgentEventKind.RunFailed,
                // Tool/notification callbacks without a proved current prompt or
                // question ID cannot refresh activity or manufacture an outcome.
                _ => (AgentEventKind?)null
            };
            if (kind is null) return false;
            var id = EventId(prompt, kind.ToString()!);
            var next = new AgentActivityEvent(1, _generation, Snapshot.LastSequence + 1, id,
                kind.Value, _time.GetUtcNow(), run, AgentSessionId: _session);
            var succeeded = _tracker.TryAccept(next, out accepted);
            if (succeeded)
            {
                if (generatedWakeup && name == "userpromptsubmit") _wakeupPrompt = prompt;
                if (!Snapshot.RunActive) ClearBackgroundTasks();
            }
            return succeeded;
        }
        catch (JsonException) { return false; }
    }

    private void ClearBackgroundTasks() { _backgroundTasks.Clear(); _wakeupPrompt = null; }

    private static bool ValidTaskId(string? id) => Guid.TryParseExact(id, "D", out var parsed) && parsed.ToString("D") == id;

    private void RememberBackgroundTasks(JsonElement value)
    {
        // Only a current root Stop may establish task provenance. The callback
        // remains ineligible for completion while any background entry exists.
        if (value.TryGetProperty("reason", out var reason) && (reason.ValueKind != JsonValueKind.String || reason.GetString() != "end_turn")) return;
        if (!MatchesBoth(value, "stopHookActive", "stop_hook_active", field => field.ValueKind == JsonValueKind.False)
            || !MatchesBoth(value, "sessionCrons", "session_crons", EmptyArray)) return;
        var present = value.TryGetProperty("backgroundTasks", out var tasks);
        var aliasPresent = value.TryGetProperty("background_tasks", out var alias);
        if (!present && !aliasPresent || present && aliasPresent && tasks.GetRawText() != alias.GetRawText()) return;
        if (!present) tasks = alias;
        if (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() is 0 or > 64) return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object || task.EnumerateObject().Select(item => item.Name).Distinct().Count() != task.EnumerateObject().Count()
                || !Field(task, "id", null, out var id) || !ValidTaskId(id)
                || !Field(task, "type", null, out var type) || type != "shell"
                || !Field(task, "status", "state", out var status) || status != "running"
                || !ids.Add(id!)) return;
        }
        if (_backgroundTasks.Union(ids).Count() <= 64) _backgroundTasks.UnionWith(ids);
    }

    private string EventId(string? prompt, string kind) => "grok:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(_session + "\n" + (prompt ?? "") + "\n" + kind))).ToLowerInvariant();

    private static bool IsUnambiguousStop(JsonElement value)
        => (!value.TryGetProperty("reason", out var reason) || reason.ValueKind == JsonValueKind.String && reason.GetString() == "end_turn")
            && MatchesBoth(value, "stopHookActive", "stop_hook_active", field => field.ValueKind == JsonValueKind.False)
            && MatchesBoth(value, "backgroundTasks", "background_tasks", EmptyArray)
            && MatchesBoth(value, "sessionCrons", "session_crons", EmptyArray);

    private static bool EmptyArray(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;

    private static bool MatchesBoth(JsonElement value, string name, string alias, Func<JsonElement, bool> matches)
    {
        var present = value.TryGetProperty(name, out var field);
        var otherPresent = value.TryGetProperty(alias, out var other);
        return (present || otherPresent) && (!present || matches(field)) && (!otherPresent || matches(other));
    }

    private static bool Field(JsonElement value, string name, string? alias, out string? result)
    {
        result = null;
        var present = value.TryGetProperty(name, out var field);
        var otherPresent = alias is not null && value.TryGetProperty(alias, out _);
        if (present && field.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
        if (present) result = field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (otherPresent)
        {
            var other = value.GetProperty(alias!);
            if (other.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            var text = other.ValueKind == JsonValueKind.String ? other.GetString() : null;
            if (present && text != result) return false;
            result = text;
        }
        return true;
    }

    private static bool HasIdentity(JsonElement value, string name, string alias)
        => value.TryGetProperty(name, out var first) && first.ValueKind != JsonValueKind.Null
            || value.TryGetProperty(alias, out var second) && second.ValueKind != JsonValueKind.Null;

    private static bool EventName(JsonElement value, out string? result)
    {
        result = null;
        foreach (var key in new[] { "hookEventName", "hook_event_name" })
        {
            if (!value.TryGetProperty(key, out var field)) continue;
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 and <= 64 } text
                || text.Any(c => !char.IsAsciiLetter(c) && c != '_')) return false;
            var normalized = text.Replace("_", "").ToLowerInvariant();
            if (result is not null && normalized != result) return false;
            result = normalized;
        }
        return result is not null;
    }
}
