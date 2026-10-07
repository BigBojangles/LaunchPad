using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Redacted host receipts, retained unchanged through feed/publication retries.</summary>
public sealed class GrokActivityInbox(string directory, GrokActivityBinding binding)
{
    public const string FileName = "grok-activity-inbox.json";
    public const int Capacity = 256;
    private const int MaxBytes = 2 * 1024 * 1024;
    public sealed record Ledger([property: JsonRequired] int Schema, [property: JsonRequired] GrokActivityBinding Binding,
        [property: JsonRequired] long Sequence, [property: JsonRequired] GrokActivityReceipt[] Receipts);

    public GrokActivityReceipt? Capture(string raw, DateTimeOffset captured, Func<NotificationConsent?> consent, bool allowRunLifecycle = true)
    {
        if (captured == default || !TryRedact(binding, raw, out var callback)) return null;
        NotificationConsent? originalConsent;
        try { originalConsent = consent(); } catch { originalConsent = null; }
        using var lease = Lease();
        var state = ReadLocked();
        if (state.Receipts.Length >= Capacity || state.Sequence == long.MaxValue)
            throw new IOException("The activity inbox is full. Earlier receipts were preserved.");
        var receipt = new GrokActivityReceipt(Guid.NewGuid().ToString("N"), state.Sequence + 1, captured, callback, originalConsent, allowRunLifecycle);
        Save(state with { Sequence = receipt.Sequence, Receipts = state.Receipts.Append(receipt).ToArray() });
        return receipt;
    }

    public Ledger Read() { using var lease = Lease(); return ReadLocked(); }
    public bool Acknowledge(string id)
    {
        using var lease = Lease(); var state = ReadLocked();
        if (state.Receipts.Length == 0 || state.Receipts[0].Id != id) return false;
        Save(state with { Receipts = state.Receipts[1..] }); return true;
    }

    private string Safe(string name) => FenceFiles.TryResolveUnlinked(directory, name, out var path)
        ? path : throw new IOException("Activity inbox path is unavailable.");
    private FileStream Lease()
    {
        var path = Safe(FileName + ".lock"); Directory.CreateDirectory(directory);
        // Grok can dispatch SessionStart and the first prompt concurrently.
        // Resolve only a short sharing conflict, never an unbounded hook wait.
        for (var attempt = 0; ; attempt++)
        {
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (attempt < 8 && (error.HResult & 0xffff) is 32 or 33) { Thread.Sleep(20); }
        }
    }
    private Ledger ReadLocked()
    {
        var path = Safe(FileName);
        if (!File.Exists(path)) return new(1, binding, 0, []);
        try
        {
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Activity inbox is too large.");
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 32 });
            if (!Unique(doc.RootElement)) throw new InvalidDataException("Activity inbox is ambiguous.");
            var state = JsonSerializer.Deserialize<Ledger>(doc.RootElement, JsonFile.Options);
            if (state is not { Schema: 1, Receipts: not null } || state.Binding != binding || state.Sequence < 0
                || state.Receipts.Length > Capacity
                || state.Receipts.Any(row => row is null || row.Sequence <= 0 || row.Sequence > state.Sequence
                    || row.CapturedUtc == default || !Guid.TryParseExact(row.Id, "N", out var id) || id.ToString("N") != row.Id
                    || !TryRedact(binding, row.Callback, out var clean) || clean != row.Callback)
                || state.Receipts.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != state.Receipts.Length
                || !state.Receipts.Select(row => row.Sequence).SequenceEqual(state.Receipts.Select(row => row.Sequence).Order())
                || state.Receipts.Select(row => row.Sequence).Distinct().Count() != state.Receipts.Length
                || state.Receipts.Length > 0 && state.Receipts[^1].Sequence != state.Sequence)
                throw new InvalidDataException("Activity inbox is incomplete or belongs to another launch.");
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Activity inbox could not be read. Its file was preserved.", error); }
    }
    private void Save(Ledger state)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(state, JsonFile.Options).Length > MaxBytes)
            throw new IOException("Activity inbox is full. Earlier receipts were preserved.");
        ReturnRecovery.SaveAtomic(Safe(FileName), state);
    }

    // Validate identity before projection. Dropping unknown payloads must never
    // turn malformed or conflicting completion metadata into a valid Stop.
    public static bool TryRedact(GrokActivityBinding binding, string raw, out string callback)
    {
        callback = "";
        if (raw is null || Encoding.UTF8.GetByteCount(raw) > GrokActivityAdapter.MaxInputBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (!Unique(root) || root.EnumerateObject().Count() > 64) return false;
            var output = new Dictionary<string, object?>();
            bool Text(string first, string? alias, string? expected = null, bool required = false, bool identifier = false)
            {
                string? common = null; var found = false;
                foreach (var key in new[] { first, alias }.Where(key => key is not null))
                {
                    if (!root.TryGetProperty(key!, out var value)) continue;
                    if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
                    var text = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                    if (found && common != text || expected is not null && text != expected
                        || identifier && text is not null && text.Length > 0 && !AgentActivityTracker.ValidIdentifier(text)) return false;
                    found = true; common = text; output[key!] = identifier && text == "" ? null : text;
                }
                return !required || found && common is not null;
            }
            if (!Text("sessionId", "session_id", binding.RootSessionId, true)
                || !Text("cwd", null, binding.CallbackDirectory, true)
                || !Text("workspaceRoot", "workspace_root", binding.WorkspaceRoot ?? binding.CallbackDirectory)
                || !Text("promptId", "prompt_id", identifier: true)) return false;
            foreach (var key in new[] { "agentId", "agent_id", "subagentId", "subagent_id", "parentSessionId", "parent_session_id", "subagentType", "subagent_type" })
                if (root.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null) return false;
            string? eventName = null;
            foreach (var key in new[] { "hookEventName", "hook_event_name" })
            {
                if (!root.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 64 } name
                    || name.Any(c => !char.IsAsciiLetter(c) && c != '_')) return false;
                var normalized = name.Replace("_", "").ToLowerInvariant();
                if (eventName is not null && normalized != eventName) return false;
                eventName = normalized; output[key] = name;
            }
            if (eventName is not ("sessionstart" or "userpromptsubmit" or "stop" or "stopfailure")) return false;
            if (eventName == "userpromptsubmit" && !new[] { "promptId", "prompt_id" }.Any(key => output.TryGetValue(key, out var value) && value is string)) return false;
            if (root.TryGetProperty("reason", out var reason))
                output["reason"] = reason.ValueKind == JsonValueKind.String && reason.GetString() is "end_turn" or "session_end" ? reason.GetString() : null;
            foreach (var key in new[] { "stopHookActive", "stop_hook_active" })
                if (root.TryGetProperty(key, out var value))
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    output[key] = value.GetBoolean();
                }
            foreach (var key in new[] { "backgroundTasks", "background_tasks", "sessionCrons", "session_crons" })
            {
                if (!root.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 64) return false;
                if (key.StartsWith("session", StringComparison.Ordinal))
                { output[key] = value.GetArrayLength() == 0 ? Array.Empty<object>() : new object?[] { null }; continue; }
                var entries = new List<object?>();
                foreach (var task in value.EnumerateArray())
                {
                    if (!Unique(task)) { entries.Add(null); continue; }
                    var safe = new Dictionary<string, string>();
                    var invalid = false;
                    if (task.TryGetProperty("status", out var status) && task.TryGetProperty("state", out var state)
                        && status.GetRawText() != state.GetRawText()) invalid = true;
                    foreach (var field in task.EnumerateObject().Where(field => field.Name is "id" or "type" or "status" or "state"))
                    {
                        if (field.Value.ValueKind != JsonValueKind.String) { invalid = true; continue; }
                        var text = field.Value.GetString()!;
                        if (field.Name == "id" && Guid.TryParseExact(text, "D", out var id) && id.ToString("D") == text
                            || field.Name == "type" && text == "shell"
                            || field.Name is "status" or "state" && text is "running" or "pending" or "completed" or "done" or "failed" or "cancelled" or "stopped" or "finished" or "active")
                            safe[field.Name] = text;
                        else invalid = true;
                    }
                    entries.Add(invalid ? null : safe);
                }
                output[key] = entries;
                // Equality is checked BEFORE redaction; different hidden task
                // payloads must not become two equal, apparently safe arrays.
                var alias = key == "backgroundTasks" ? "background_tasks" : key == "sessionCrons" ? "session_crons" : null;
                if (alias is not null && root.TryGetProperty(alias, out var other) && value.GetRawText() != other.GetRawText()) return false;
            }
            callback = JsonSerializer.Serialize(output); return true;
        }
        catch (JsonException) { return false; }
    }
    private static bool Unique(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
}
