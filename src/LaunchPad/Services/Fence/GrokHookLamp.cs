using System.Security.Cryptography;
using System.Text.Json;
using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

/// <summary>Last reported root-TUI state, never an accepted run outcome or permission.</summary>
internal sealed class GrokHookLamp(string generation, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string? _nonce;
    private string? _root;
    private long _lastCaptured;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _sequence;
    private string? _run;
    private bool _ended;

    public AgentActivitySnapshot Snapshot { get; private set; } = AgentActivitySnapshot.Unavailable;

    public bool Observe(HookDiagnostic value)
    {
        var now = _time.GetUtcNow();
        var captured = DateTimeOffset.FromUnixTimeMilliseconds(value.CapturedUnixMs);
        if (value.Version != 2 || value.ChildSession != false || _ended
            || !value.CwdMatchesProject || value.SessionHash is null
            || captured > now.AddSeconds(5) || captured < now.AddSeconds(-30)
            || value.Fields.Any(field => field.Key is "parentSessionId" or "subagentId" && field.Value != "NoneType")) return false;
        if (_root is null)
        {
            if (value.Event != "SessionStart") return false;
            _root = value.SessionHash;
            _nonce = value.Nonce;
        }
        if (value.SessionHash != _root || value.Nonce != _nonce || value.CapturedUnixMs < _lastCaptured
            || value.Event == "SessionStart" && _sequence > 0) return false;
        if (value.Event == "UserPromptSubmit" && value.PromptHash is null) return false;
        var sessionNotice = value.Event == "Notification" && value.PromptHash is null
            && value.NotificationType is "idle_prompt" or "permission_prompt" or "elicitation_dialog";
        if (value.Event is not ("SessionStart" or "SessionEnd" or "UserPromptSubmit") && !sessionNotice
            && (_run is null || value.PromptHash != _run)) return false;
        var id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
        if (_seen.Contains(id)) return false;
        AgentActivity? state = value.Event switch
        {
            "SessionStart" => AgentActivity.Idle,
            "UserPromptSubmit" => AgentActivity.Working,
            "PostToolUse" or "PostToolUseFailure" => AgentActivity.Working,
            "Notification" when value.NotificationType is "permission_prompt" or "elicitation_dialog" => AgentActivity.NeedsAttention,
            "Notification" when value.NotificationType == "idle_prompt" => AgentActivity.Idle,
            "SessionEnd" => AgentActivity.Idle,
            // Another Stop hook may continue the turn after this passive hook.
            // Wait for actual idle_prompt/SessionEnd instead of false red.
            _ => null
        };
        _lastCaptured = value.CapturedUnixMs;
        _seen.Add(id); _order.Enqueue(id);
        if (_order.Count > 256) _seen.Remove(_order.Dequeue());
        if (state is null) return false;
        // A parallel tool completing does not resolve a pending user question.
        if (state == AgentActivity.Working && value.Event != "UserPromptSubmit"
            && Snapshot.State == AgentActivity.NeedsAttention) return false;
        if (value.Event == "UserPromptSubmit") _run = value.PromptHash;
        if (state != AgentActivity.Idle && _run is null) return false;
        if (state == AgentActivity.Idle) _run = null;
        if (value.Event == "SessionEnd") _ended = true;
        var observation = new AgentStateObservation(1, generation, ++_sequence, id, now, _root,
            state.Value, _run, state == AgentActivity.NeedsAttention ? "hook-" + id : null,
            RunActive: state != AgentActivity.Idle);
        Snapshot = new(observation.State, observation.RunId, observation.QuestionId,
            observation.RunActive, observation.Sequence, null, observation);
        return true;
    }
}
