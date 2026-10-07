using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed record ClaudeActivityBinding([property: JsonRequired] string Generation,
    [property: JsonRequired] string RootSessionId, [property: JsonRequired] string Project);

/// <summary>Only advisory identifiers and fixed schema values; never a transcript or tool payload.</summary>
public sealed record ClaudeCallbackMetadata([property: JsonRequired] string EventName, [property: JsonRequired] string? PromptId,
    [property: JsonRequired] string? ToolUseId, [property: JsonRequired] string? NotificationType, [property: JsonRequired] string? Source);
public sealed record ClaudeActivityReceipt([property: JsonRequired] ClaudeActivityBinding Binding, [property: JsonRequired] string Id,
    [property: JsonRequired] long Sequence, [property: JsonRequired] DateTimeOffset CapturedUtc,
    [property: JsonRequired] ClaudeCallbackMetadata Metadata)
{
    [JsonRequired] public string? ProducerEpoch { get; init; }
}
public sealed record ClaudeActivityCheckpoint([property: JsonRequired] ClaudeActivityBinding Binding, [property: JsonRequired] long Cursor,
    [property: JsonRequired] string[] ReceiptIds, [property: JsonRequired] AgentActivitySnapshot Activity,
    [property: JsonRequired] string? CandidatePromptId, [property: JsonRequired] bool Closed);

/// <summary>
/// Claude's callback status projection. It never installs hooks, changes decisions,
/// publishes run outcomes or queues alerts. A production producer must separately
/// prove CLI compatibility, root attribution, delivery and final-turn semantics.
/// </summary>
public sealed class ClaudeActivityAdapter
{
    public const int MaxInputBytes = 64 * 1024;
    private const int MaxReceipts = 256;
    private readonly ClaudeActivityBinding _binding;
    private readonly AgentActivityTracker _tracker;
    private readonly TimeProvider _time;
    private readonly Queue<string> _receipts = new();
    private long _cursor, _receivedTimestamp;
    private TimeSpan _initialAge;
    private bool _live, _closed;
    private string? _candidatePrompt;

    public ClaudeActivityAdapter(ClaudeActivityBinding binding, ClaudeActivityCheckpoint? checkpoint = null,
        TimeProvider? time = null)
    {
        if (!Guid.TryParseExact(binding.Generation, "N", out _)
            || !AgentActivityTracker.ValidIdentifier(binding.RootSessionId)
            || !Path.IsPathFullyQualified(binding.Project) || binding.Project.Any(char.IsControl))
            throw new ArgumentException("Invalid Claude activity binding.");
        if (checkpoint is not null && (checkpoint.Binding != binding || checkpoint.Cursor < 0
            || checkpoint.ReceiptIds is null || checkpoint.ReceiptIds.Length > MaxReceipts
            || checkpoint.ReceiptIds.Any(id => !ReceiptId(id))
            || checkpoint.ReceiptIds.Distinct(StringComparer.Ordinal).Count() != checkpoint.ReceiptIds.Length
            || checkpoint.Activity is null || !AgentActivityTracker.IsValidSnapshot(checkpoint.Activity, binding.Generation)
            || checkpoint.Activity.LastSequence > checkpoint.Cursor
            || checkpoint.Activity.LastEvent is not null
            || checkpoint.Activity.RunId is { } run && !PromptId(run)
            || checkpoint.CandidatePromptId is { } candidate && !PromptId(candidate)
            || checkpoint.Activity != AgentActivitySnapshot.Unavailable
                && checkpoint.Activity.Observation?.AgentSessionId != binding.RootSessionId))
            throw new ArgumentException("Invalid Claude activity checkpoint.");
        _binding = binding; _time = time ?? TimeProvider.System;
        _tracker = new(binding.Generation, checkpoint?.Activity);
        if (checkpoint is not null)
        {
            _cursor = checkpoint.Cursor; _closed = checkpoint.Closed; _candidatePrompt = checkpoint.CandidatePromptId;
            foreach (var id in checkpoint.ReceiptIds) _receipts.Enqueue(id);
        }
        // Restored metadata is history, never a fresh observation from the CLI.
    }

    public AgentActivitySnapshot Snapshot => _tracker.Snapshot;
    public ClaudeActivityCheckpoint Checkpoint => new(_binding, _cursor, _receipts.ToArray(), Snapshot, _candidatePrompt, _closed);
    public AgentActivitySnapshot Current()
    {
        var age = _time.GetElapsedTime(_receivedTimestamp) + _initialAge;
        return _live && !_closed && age >= TimeSpan.Zero && age <= SessionActivityStore.Freshness
            ? Snapshot : AgentActivitySnapshot.Unavailable;
    }

    public static ClaudeActivityReceipt? Capture(string json, ClaudeActivityBinding binding, string id,
        long sequence, DateTimeOffset capturedUtc)
    {
        if (!ReceiptId(id) || sequence <= 0 || capturedUtc == default || capturedUtc.Offset != TimeSpan.Zero
            || Encoding.UTF8.GetByteCount(json) > MaxInputBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var keys = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (keys.Length > 64 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length
                || !Text(root, "session_id", out var session) || session != binding.RootSessionId
                || !Text(root, "cwd", out var cwd) || cwd != binding.Project
                // agent_type can name a main --agent session. agent_id means a child.
                || root.TryGetProperty("agent_id", out _)
                || root.TryGetProperty("parent_session_id", out _)
                || !Text(root, "hook_event_name", out var kind) || !KnownEvent(kind)
                || !OptionalText(root, "prompt_id", out var prompt) || prompt is not null && !PromptId(prompt)
                || !OptionalText(root, "tool_use_id", out var tool) || tool is not null && !AgentActivityTracker.ValidIdentifier(tool)
                || !OptionalText(root, "notification_type", out var notification)
                || !OptionalText(root, "source", out var source)) return null;
            var metadata = new ClaudeCallbackMetadata(kind!, prompt, tool, notification, source);
            return Valid(metadata) ? new(binding, id, sequence, capturedUtc, metadata) : null;
        }
        catch (JsonException) { return null; }
    }

    public bool Ingest(ClaudeActivityReceipt receipt, bool live = false)
    {
        if (_closed || receipt.Binding != _binding || !ReceiptId(receipt.Id) || receipt.Sequence <= _cursor || _receipts.Contains(receipt.Id)
            || receipt.CapturedUtc == default || receipt.CapturedUtc.Offset != TimeSpan.Zero
            || receipt.Metadata is null || !Valid(receipt.Metadata)) return false;
        var value = receipt.Metadata;
        var prompt = value.PromptId;
        AgentStateObservation? observed = null;
        var previous = Snapshot;
        // A newer provisional prompt supersedes the old prompt for attribution,
        // even before it has produced enough evidence to show Working.
        var currentPrompt = _candidatePrompt ?? previous.RunId;
        switch (value.EventName)
        {
            case "SessionStart" when value.Source == "startup" && _cursor == 0 && prompt is null:
                observed = State(AgentActivity.Idle);
                break;
            case "UserPromptSubmit":
                // Submission may be blocked or come from a scheduler/child. Keep
                // only a candidate; it cannot start work, close a run or refresh health.
                _candidatePrompt = prompt;
                _live = false;
                break;
            case "PreToolUse":
            case "PostToolUse":
            case "PostToolUseFailure":
                // A parallel tool's result cannot prove that the unidentified
                // waiting question was answered. Invalidate rather than clear it.
                if (prompt != currentPrompt) break;
                if (previous.State == AgentActivity.NeedsAttention && prompt == previous.RunId)
                    _live = false;
                else
                    observed = State(AgentActivity.Working, prompt, active: true);
                break;
            case "Notification" when value.NotificationType is "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog":
                if (previous.RunActive && prompt == previous.RunId && prompt == currentPrompt)
                    // This identifies the waiting observation, not a stable distinct
                    // question across CLI notifications. It is STATE ONLY: no alert.
                    observed = State(AgentActivity.NeedsAttention, prompt, "claude-wait:" + receipt.Id, active: true);
                break;
            case "SessionEnd":
                _closed = true; _live = false;
                break;
            case "Stop":
            case "StopFailure":
                // Stop can continue; failure does not identify the whole root run's
                // final outcome. Do not renew stale green or fabricate completion.
                if (prompt == previous.RunId) _live = false;
                break;
        }
        if (observed is not null)
        {
            if (_tracker.TryAcceptState(observed))
            {
                _receivedTimestamp = _time.GetTimestamp();
                _initialAge = _time.GetUtcNow() - receipt.CapturedUtc;
                // Clock-skewed/future data must stay non-live even after elapsed
                // time catches up; only a new actual observation can restore it.
                _live = live && _initialAge >= TimeSpan.Zero && _initialAge <= SessionActivityStore.Freshness;
            }
            else _live = false;
        }
        _cursor = receipt.Sequence;
        _receipts.Enqueue(receipt.Id);
        if (_receipts.Count > MaxReceipts) _receipts.Dequeue();
        return true;

        AgentStateObservation State(AgentActivity state, string? run = null, string? question = null, bool active = false)
            => new(1, _binding.Generation, receipt.Sequence, "claude-observation:" + receipt.Id,
                receipt.CapturedUtc, _binding.RootSessionId, state, run, question, active);
    }

    private static bool ReceiptId(string? id) => Guid.TryParseExact(id, "N", out var parsed) && id == parsed.ToString("N");
    private static bool PromptId(string? id) => Guid.TryParseExact(id, "D", out var parsed) && id == parsed.ToString("D");
    private static bool KnownEvent(string? kind) => kind is "SessionStart" or "UserPromptSubmit" or "PreToolUse"
        or "PostToolUse" or "PostToolUseFailure" or "PermissionRequest" or "Notification" or "Stop" or "StopFailure" or "SessionEnd";
    private static bool Valid(ClaudeCallbackMetadata value) => KnownEvent(value.EventName)
        && (value.PromptId is null || PromptId(value.PromptId))
        && (value.ToolUseId is null || AgentActivityTracker.ValidIdentifier(value.ToolUseId))
        && (value.NotificationType is null || value.NotificationType is "permission_prompt" or "idle_prompt" or "auth_success"
            or "elicitation_dialog" or "elicitation_url_dialog")
        && (value.Source is null || value.Source is "startup" or "resume" or "clear" or "compact" or "fork")
        && (value.EventName is "SessionStart" or "SessionEnd" || value.PromptId is not null)
        && (value.EventName is not ("PreToolUse" or "PostToolUse" or "PostToolUseFailure") || value.ToolUseId is not null)
        && (value.EventName != "Notification" || value.NotificationType is not null);
    private static bool Text(JsonElement root, string key, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString(); return value is { Length: > 0 };
    }
    private static bool OptionalText(JsonElement root, string key, out string? value)
    {
        value = null;
        return !root.TryGetProperty(key, out _) || Text(root, key, out value);
    }
}
