using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Explicit launch binding, never inferred from the first callback.</summary>
public sealed record GrokActivityBinding(string Generation, string RootSessionId, string CallbackDirectory,
    string HostProject, string CliVersion, string? WorkspaceRoot = null);
// The host producer captures this immutable consent decision when it creates
// the receipt, and retains that same receipt on retry. The feed never rereads
// Settings or acquires a later consent epoch.
public sealed record GrokActivityReceipt(string Id, long Sequence, DateTimeOffset CapturedUtc, string Callback,
    NotificationConsent? CapturedConsent = null, [property: JsonRequired] bool AllowRunLifecycle = true);
public sealed record GrokPendingActivity(AgentActivityEvent Event, NotificationConsent? Consent);
public sealed record GrokActivityFeedState(int Schema, GrokActivityBinding Binding, long Cursor, string[] ReceiptIds, string[] ProjectionIds,
    DateTimeOffset CapturedUtc, bool LiveReceipt, GrokActivityCheckpoint Checkpoint, GrokPendingActivity[] Pending);

/// <summary>
/// One durable callback cursor, checkpoint and unpublished event list. Advisory
/// metadata only; this store never installs hooks or controls an agent.
/// </summary>
public sealed class GrokActivityFeed
{
    public const string StateFile = "grok-activity-feed.json";
    public const int MaxPending = 256;
    private const int MaxStateBytes = 512 * 1024;
    private readonly string _directory;
    private readonly GrokActivityBinding _binding;
    private readonly TimeProvider _time;
    private readonly object _health = new();
    private bool _hasLiveObservation;
    private long _observedSequence;
    private long _receivedTimestamp;
    private TimeSpan _initialAge;

    public GrokActivityFeed(string directory, GrokActivityBinding binding, TimeProvider? time = null)
    {
        if (!Guid.TryParseExact(binding.Generation, "N", out _)
            || !AgentActivityTracker.ValidIdentifier(binding.RootSessionId)
            || binding.CliVersion != "1.0.46" || !Path.IsPathFullyQualified(binding.HostProject)
            || string.IsNullOrWhiteSpace(binding.CallbackDirectory) || binding.CallbackDirectory.Length > 32768
            || binding.CallbackDirectory.Any(char.IsControl)
            || binding.WorkspaceRoot is { } workspace && (string.IsNullOrWhiteSpace(workspace) || workspace.Length > 32768 || workspace.Any(char.IsControl)))
            throw new ArgumentException("Invalid or unsupported Grok launch binding.");
        _directory = Path.GetFullPath(directory); _binding = binding;
        _time = time ?? TimeProvider.System;
    }

    public GrokActivityFeedState Read()
    {
        using var lease = Acquire(); return ReadLocked();
    }

    public AgentActivitySnapshot Current()
    {
        var state = Read();
        return IsCurrent(state) ? state.Checkpoint.Activity : AgentActivitySnapshot.Unavailable;
    }

    internal bool IsCurrent(GrokActivityFeedState state)
    {
        lock (_health)
        {
            var age = _time.GetElapsedTime(_receivedTimestamp) + _initialAge;
            return _hasLiveObservation && state.LiveReceipt && state.Checkpoint.Activity.LastSequence == _observedSequence
                && age >= TimeSpan.Zero && age <= SessionActivityStore.Freshness;
        }
    }

    /// <returns>True only after the receipt/checkpoint/event transaction is stored.</returns>
    public bool Ingest(GrokActivityReceipt receipt, bool replay = false)
    {
        if (!Guid.TryParseExact(receipt.Id, "N", out var id) || receipt.Id != id.ToString("N") || receipt.Sequence <= 0 || receipt.CapturedUtc == default
            || receipt.Callback is null || Encoding.UTF8.GetByteCount(receipt.Callback) > GrokActivityAdapter.MaxInputBytes
            || !MatchesBinding(receipt.Callback)) return false;
        using var lease = Acquire();
        var previous = ReadLocked();
        if (receipt.Sequence <= previous.Cursor || previous.ReceiptIds.Contains(receipt.Id, StringComparer.Ordinal)) return false;
        var adapter = new GrokActivityAdapter(_binding.Generation, _binding.RootSessionId, _binding.CallbackDirectory,
            time: new ReceiptTime(receipt.CapturedUtc), checkpoint: previous.Checkpoint, workspaceRoot: _binding.WorkspaceRoot);
        // Even an ambiguous Stop can update background provenance while returning
        // false. Persist that checkpoint together with the receipt cursor.
        AcceptedAgentActivityEvent? accepted = null;
        var gated = !receipt.AllowRunLifecycle && (!IsSessionStart(receipt.Callback) || previous.Cursor != 0);
        if (!gated) adapter.TryAccept(receipt.Callback, out accepted);
        var checkpoint = adapter.Checkpoint;
        if (gated) checkpoint = checkpoint with { HistoryComplete = false };
        var projectionId = accepted?.Value.EventId ?? (checkpoint.Activity.LastSequence > previous.Checkpoint.Activity.LastSequence
            ? checkpoint.Activity.Observation?.ObservationId : null);
        if (projectionId is not null && (previous.ProjectionIds.Contains(projectionId, StringComparer.Ordinal)
            || previous.Pending.Any(item => item.Event.EventId == projectionId)))
        { checkpoint = previous.Checkpoint; accepted = null; projectionId = null; }
        if (receipt.Sequence != previous.Cursor + 1) checkpoint = checkpoint with { HistoryComplete = false };
        var pending = previous.Pending;
        if (accepted is not null)
        {
            if (pending.Length >= MaxPending) throw new IOException("The Grok activity feed is full; unpublished events were preserved.");
            var consent = !replay && IsFresh(receipt.CapturedUtc, live: true) ? receipt.CapturedConsent : null;
            pending = pending.Append(new GrokPendingActivity(accepted.Value, consent)).ToArray();
        }
        var advanced = checkpoint.Activity.LastSequence > previous.Checkpoint.Activity.LastSequence;
        var next = new GrokActivityFeedState(1, _binding, receipt.Sequence,
            previous.ReceiptIds.Append(receipt.Id).TakeLast(256).ToArray(), projectionId is null ? previous.ProjectionIds : previous.ProjectionIds.Append(projectionId).TakeLast(512).ToArray(),
            advanced ? receipt.CapturedUtc : previous.CapturedUtc,
            gated ? false : advanced ? !replay && IsFresh(receipt.CapturedUtc, live: true) : previous.LiveReceipt,
            checkpoint, pending);
        Save(next);
        if (gated) lock (_health) _hasLiveObservation = false;
        if (advanced)
            lock (_health)
            {
                _hasLiveObservation = next.LiveReceipt && IsFresh(receipt.CapturedUtc, live: true);
                _observedSequence = checkpoint.Activity.LastSequence;
                _receivedTimestamp = _time.GetTimestamp();
                _initialAge = _time.GetUtcNow() - receipt.CapturedUtc;
            }
        return true;
    }

    /// <summary>Acknowledge only a published prefix; concurrent arrivals remain pending.</summary>
    public bool Acknowledge(IReadOnlyList<string> eventIds)
    {
        if (eventIds.Count == 0) return true;
        using var lease = Acquire(); var state = ReadLocked();
        if (eventIds.Count > state.Pending.Length || !state.Pending.Take(eventIds.Count).Select(item => item.Event.EventId).SequenceEqual(eventIds)) return false;
        Save(state with { Pending = state.Pending.Skip(eventIds.Count).ToArray() });
        return true;
    }

    /// <summary>Publish under the same exclusive owner; acknowledge only after every local sink succeeds.</summary>
    public void PublishPending(Action<GrokActivityFeedState> publish)
    {
        using var lease = Acquire(); var state = ReadLocked();
        publish(state);
        if (state.Pending.Length > 0) Save(state with { Pending = [] });
    }

    private bool IsFresh(DateTimeOffset captured, bool live)
    {
        var now = _time.GetUtcNow();
        return live && captured != default && now >= captured && now - captured <= SessionActivityStore.Freshness;
    }

    private sealed class ReceiptTime(DateTimeOffset captured) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => captured; }

    private bool MatchesBinding(string callback)
    {
        try
        {
            using var parsed = JsonDocument.Parse(callback, new JsonDocumentOptions { MaxDepth = 16 });
            var value = parsed.RootElement;
            if (value.ValueKind != JsonValueKind.Object
                || value.EnumerateObject().Select(item => item.Name).Distinct().Count() != value.EnumerateObject().Count()) return false;
            bool Field(string first, string? alias, string expected, bool required)
            {
                var found = false;
                foreach (var name in new[] { first, alias }.Where(name => name is not null))
                    if (value.TryGetProperty(name!, out var item))
                    { if (item.ValueKind != JsonValueKind.String || item.GetString() != expected) return false; found = true; }
                return found || !required;
            }
            return Field("sessionId", "session_id", _binding.RootSessionId, true)
                && Field("cwd", null, _binding.CallbackDirectory, true)
                && Field("workspaceRoot", "workspace_root", _binding.WorkspaceRoot ?? _binding.CallbackDirectory, false)
                && !new[] { "agentId", "agent_id", "subagentId", "subagent_id", "parentSessionId", "parent_session_id", "subagentType", "subagent_type" }
                    .Any(name => value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null);
        }
        catch (JsonException) { return false; }
    }
    private static bool IsSessionStart(string callback)
    {
        using var document = JsonDocument.Parse(callback);
        var value = document.RootElement;
        return value.TryGetProperty("hookEventName", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString()?.Replace("_", "").Equals("SessionStart", StringComparison.OrdinalIgnoreCase) == true
            || value.TryGetProperty("hook_event_name", out var alias) && alias.ValueKind == JsonValueKind.String
            && alias.GetString()?.Replace("_", "").Equals("SessionStart", StringComparison.OrdinalIgnoreCase) == true;
    }

    private FileStream Acquire()
    {
        if (!FenceFiles.TryResolveUnlinked(_directory, StateFile + ".lock", out var path))
            throw new IOException("The Grok activity directory contains a link.");
        Directory.CreateDirectory(_directory);
        // No waiting indefinitely inside a CLI hook. Another owner can retry;
        // never read/overwrite its partially processed checkpoint.
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string StatePath()
    {
        if (!FenceFiles.TryResolveUnlinked(_directory, StateFile, out var path)) throw new IOException("The Grok activity state contains a link.");
        return path;
    }

    private GrokActivityFeedState ReadLocked()
    {
        var path = StatePath();
        if (!File.Exists(path))
        {
            var adapter = new GrokActivityAdapter(_binding.Generation, _binding.RootSessionId, _binding.CallbackDirectory, time: _time, workspaceRoot: _binding.WorkspaceRoot);
            return new(1, _binding, 0, [], [], default, false, adapter.Checkpoint, []);
        }
        try
        {
            if (new FileInfo(path).Length > MaxStateBytes) throw new InvalidDataException("Grok activity state is too large.");
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 32 });
            var required = new[] { "schema", "binding", "cursor", "receiptIds", "projectionIds", "capturedUtc", "liveReceipt", "checkpoint", "pending" };
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || required.Any(name => !document.RootElement.TryGetProperty(name, out _))
                || document.RootElement.EnumerateObject().Select(item => item.Name).Distinct().Count() != document.RootElement.EnumerateObject().Count())
                throw new InvalidDataException("Grok activity state is incomplete.");
            var state = JsonSerializer.Deserialize<GrokActivityFeedState>(document.RootElement, JsonFile.Options);
            if (state is not { Schema: 1, Checkpoint: not null, Pending: not null, ReceiptIds: not null, ProjectionIds: not null }
                || state.Binding != _binding || state.Cursor < 0 || state.ReceiptIds.Length > 256
                || (state.Cursor == 0) != (state.ReceiptIds.Length == 0) || state.Cursor > 0 && state.CapturedUtc == default && state.LiveReceipt
                || state.ReceiptIds.Any(id => !Guid.TryParseExact(id, "N", out _))
                || state.ReceiptIds.Distinct(StringComparer.Ordinal).Count() != state.ReceiptIds.Length
                || state.ProjectionIds.Length > 512 || state.ProjectionIds.Any(id => !AgentActivityTracker.ValidIdentifier(id))
                || state.ProjectionIds.Distinct(StringComparer.Ordinal).Count() != state.ProjectionIds.Length
                || state.Pending.Length > MaxPending
                || state.Pending.Any(item => item is null || item.Event is null
                    || !AgentActivityTracker.IsValidEvent(item.Event, _binding.Generation)
                    || item.Event.AgentSessionId != _binding.RootSessionId
                    || item.Event.Sequence > state.Checkpoint.Activity.LastSequence)
                || state.Pending.Select(item => item.Event.EventId).Distinct(StringComparer.Ordinal).Count() != state.Pending.Length)
                throw new InvalidDataException("Grok activity state is invalid or belongs to another launch.");
            _ = new GrokActivityAdapter(_binding.Generation, _binding.RootSessionId, _binding.CallbackDirectory, time: _time, checkpoint: state.Checkpoint, workspaceRoot: _binding.WorkspaceRoot);
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Grok activity state could not be read. Its file was preserved.", error); }
    }

    private void Save(GrokActivityFeedState state)
    {
        // Cursor, background bindings, resync observation and unpublished events
        // move together. A failed write leaves the previous state retryable.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonFile.Options);
        if (bytes.Length > MaxStateBytes) throw new IOException("Grok activity state is full; earlier state was preserved.");
        ReturnRecovery.SaveAtomic(StatePath(), state);
    }
}
