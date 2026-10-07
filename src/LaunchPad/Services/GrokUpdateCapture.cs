using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;
using Microsoft.Win32.SafeHandles;

namespace LaunchPad.Services;

public sealed record GrokUpdateMetadata(string EventId, DateTimeOffset OccurredUtc, string Kind,
    string? PromptId, string? StopReason, long? ElapsedMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DateTimeOffset CapturedUtc = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LiveEligible = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NotificationConsent? CapturedConsent = null);
public sealed record GrokUpdateFingerprint(string EventId, string Hash);
public sealed record GrokUpdateCaptureState([property: JsonRequired] int Schema,
    [property: JsonRequired] GrokActivityBinding Binding, [property: JsonRequired] string StreamPath,
    [property: JsonRequired] string? FileIdentity, [property: JsonRequired] long Cursor,
    [property: JsonRequired] bool DiscardingOversizedLine, [property: JsonRequired] string PrefixHash,
    [property: JsonRequired] string AnchorHash, [property: JsonRequired] bool HistoryComplete,
    [property: JsonRequired] GrokUpdateMetadata[] Pending, [property: JsonRequired] GrokUpdateFingerprint[] Recent);

/// <summary>
/// Read-only native Windows capture of one prebound Grok 1.0.46 stream. Cursor
/// and redacted pending metadata commit together. No status freshness, outcome
/// freshness from replay or agent-control writes are created here. Optional
/// host consent is retained only at the original fresh, verified capture.
/// Integrity witnesses cover the opened file ID, first 4096 committed bytes and
/// last 512 committed bytes, not arbitrary in-place edits in the middle. Event
/// deduplication covers only the latest 512 metadata identities.
/// </summary>
public sealed class GrokUpdateCapture
{
    public const string StateFile = "grok-update-capture.json";
    public const int Capacity = 128;
    public const int MaxLineBytes = 256 * 1024;
    public const int MaxReadBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly GrokActivityBinding _binding;
    private readonly string _stream;
    private readonly TimeProvider _time;
    private readonly Func<NotificationConsent?>? _consent;
    private readonly bool _allowLive;
    public const string AttemptFile = "grok-update-capture-attempt.json";
    public sealed record Attempt([property: JsonRequired] GrokActivityBinding Binding,
        [property: JsonRequired] string StreamPath, [property: JsonRequired] string FileIdentity,
        [property: JsonRequired] GrokUpdateMetadata[] Rows);
    private Attempt? _attempt;
    public IReadOnlyList<string> LiveCapturedIds { get; private set; } = [];
    private static readonly string EmptyHash = Convert.ToHexString(SHA256.HashData([]));

    public GrokUpdateCapture(string directory, GrokActivityBinding binding, string streamPath,
        TimeProvider? time = null, Func<NotificationConsent?>? consent = null, bool allowLive = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native stream identity requires Windows.");
        if (binding.CliVersion != "1.0.46" || !CanonicalUuid(binding.RootSessionId)
            || !Guid.TryParseExact(binding.Generation, "N", out var generation) || generation.ToString("N") != binding.Generation
            || string.IsNullOrWhiteSpace(binding.CallbackDirectory) || string.IsNullOrWhiteSpace(binding.HostProject))
            throw new ArgumentException("Unsupported or incomplete Grok launch binding.");
        _directory = Path.GetFullPath(directory); _binding = binding; _stream = Path.GetFullPath(streamPath);
        _time = time ?? TimeProvider.System; _consent = consent; _allowLive = allowLive;
        if (Path.GetFileName(_stream) != "updates.jsonl" || Path.GetFileName(Path.GetDirectoryName(_stream)) != binding.RootSessionId)
            throw new ArgumentException("The update stream must belong to the prebound root session.");
    }

    public static string NativeStreamPath(string grokHome, GrokActivityBinding binding)
    {
        var relative = "sessions/" + Uri.EscapeDataString(binding.CallbackDirectory) + "/" + binding.RootSessionId + "/updates.jsonl";
        return FenceFiles.TryResolveUnlinked(grokHome, relative, out var path)
            ? path : throw new IOException("Bound Grok update path is unavailable.");
    }

    public GrokUpdateCaptureState Read() { using var lease = Lease(); return ReadLocked(); }

    public GrokUpdateCaptureState Poll()
    {
        LiveCapturedIds = [];
        using var lease = Lease(); var previous = ReadLocked();
        if (!File.Exists(_stream))
        {
            if (previous.FileIdentity is not null) throw new IOException("The bound update stream disappeared; capture was preserved.");
            return previous;
        }
        if (!FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(_stream)!, "updates.jsonl", out var safe) || safe != _stream)
            throw new IOException("The bound update stream is linked or unavailable.");
        // Exclude deletion/rename for this read. Verify the actual opened target,
        // not just path metadata observed before opening it.
        using var input = new FileStream(_stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var identity = Identity(input.SafeFileHandle, _stream);
        if (previous.FileIdentity is not null && previous.FileIdentity != identity || input.Length < previous.Cursor
            || HashRange(input, 0, (int)Math.Min(previous.Cursor, 4096)) != previous.PrefixHash
            || HashRange(input, previous.Cursor - Math.Min(previous.Cursor, 512), (int)Math.Min(previous.Cursor, 512)) != previous.AnchorHash)
            throw new InvalidDataException("The bound update stream changed or was truncated; capture was preserved.");
        var attempts = ReadAttempt(identity).Rows.Where(row => !previous.Recent.Any(item => item.EventId == row.EventId)).ToDictionary(row => row.EventId);
        var pending = previous.Pending.ToList(); var recent = previous.Recent.ToList();
        var liveIds = new List<string>();
        var cursor = previous.Cursor; var discarding = previous.DiscardingOversizedLine; var complete = previous.HistoryComplete;
        input.Position = cursor; var budget = MaxReadBytes;
        while (budget > 0 && pending.Count < Capacity)
        {
            var start = input.Position; using var line = new MemoryStream(); var ended = false;
            while (budget > 0)
            {
                var next = input.ReadByte(); if (next < 0) break; budget--;
                if (next == '\n') { ended = true; break; }
                if (!discarding)
                {
                    if (line.Length == MaxLineBytes) { discarding = true; complete = false; line.SetLength(0); }
                    else line.WriteByte((byte)next);
                }
            }
            if (!ended)
            {
                // Retain no partial transcript in host state. Short partial
                // lines are reread; huge lines advance in bounded discard mode.
                cursor = discarding ? input.Position : start; break;
            }
            cursor = input.Position;
            if (!discarding && TryProject(_binding, line.ToArray(), out var metadata))
            {
                var hash = Fingerprint(metadata!);
                var known = recent.FirstOrDefault(row => row.EventId == metadata!.EventId);
                if (known is not null && known.Hash != hash)
                    throw new InvalidDataException("Conflicting update identity; capture was preserved.");
                if (known is null)
                {
                    if (attempts.TryGetValue(metadata!.EventId, out var first))
                    {
                        if (Fingerprint(first) != hash) throw new InvalidDataException("Captured update changed before commit; original attempt retained.");
                        metadata = first; // no settings reread or new live witness on retry
                    }
                    else
                    {
                        var captured = _time.GetUtcNow();
                        var live = _allowLive && captured >= metadata.OccurredUtc && captured - metadata.OccurredUtc <= SessionActivityStore.Freshness;
                        NotificationConsent? originalConsent = null;
                        if (live) try { originalConsent = _consent?.Invoke(); } catch { }
                        metadata = metadata with { CapturedUtc = captured, LiveEligible = live, CapturedConsent = originalConsent };
                        attempts.Add(metadata.EventId, metadata);
                        _attempt = new(_binding, _stream, identity, attempts.Values.ToArray());
                        if (live) liveIds.Add(metadata.EventId);
                    }
                    pending.Add(metadata!); recent.Add(new(metadata!.EventId, hash));
                    if (recent.Count > 512) recent.RemoveAt(0);
                }
            }
            else if (!discarding && !KnownIgnoredLine(_binding, line.ToArray())) complete = false;
            discarding = false;
        }
        if (input.Length < cursor) throw new InvalidDataException("The update stream was truncated during capture.");
        var nextState = new GrokUpdateCaptureState(1, _binding, _stream, identity, cursor, discarding,
            HashRange(input, 0, (int)Math.Min(cursor, 4096)),
            HashRange(input, cursor - Math.Min(cursor, 512), (int)Math.Min(cursor, 512)), complete, pending.ToArray(), recent.ToArray());
        if (attempts.Count > 0)
        {
            _attempt = new(_binding, _stream, identity, attempts.Values.ToArray());
            // Commit the original host receipt before cursor movement. A locked
            // main ledger can be retried/reopened without a new opt-in epoch.
            ReturnRecovery.SaveAtomic(Safe(AttemptFile), _attempt);
        }
        ReturnRecovery.SaveAtomic(Safe(StateFile), nextState);
        LiveCapturedIds = liveIds.ToArray();
        return nextState;
    }

    public bool Acknowledge(IReadOnlyList<string> eventIds)
    {
        using var lease = Lease(); var state = ReadLocked();
        if (eventIds.Count > state.Pending.Length || !state.Pending.Take(eventIds.Count).Select(row => row.EventId).SequenceEqual(eventIds)) return false;
        ReturnRecovery.SaveAtomic(Safe(StateFile), state with { Pending = state.Pending.Skip(eventIds.Count).ToArray() }); return true;
    }

    public static bool TryProject(GrokActivityBinding binding, byte[] utf8, out GrokUpdateMetadata? metadata)
    {
        metadata = null;
        if (binding.CliVersion != "1.0.46" || !CanonicalUuid(binding.RootSessionId) || utf8.Length > MaxLineBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!Unique(root) || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String
                || method.GetString() is not ("session/update" or "_x.ai/session/update")
                || !root.TryGetProperty("params", out var parameters) || !Unique(parameters)
                || !Text(parameters, "sessionId", out var session) || session != binding.RootSessionId
                || !parameters.TryGetProperty("update", out var update) || !Unique(update)
                || !parameters.TryGetProperty("_meta", out var provenance) || !Unique(provenance)
                || !Text(provenance, "eventId", out var eventId) || !AgentActivityTracker.ValidIdentifier(eventId)
                || !provenance.TryGetProperty("agentTimestampMs", out var stamp) || !stamp.TryGetInt64(out var milliseconds)
                || !Text(update, "sessionUpdate", out var kind)) return false;
            foreach (var value in new[] { root, parameters, update, provenance })
                foreach (var key in new[] { "agentId", "agent_id", "subagentId", "subagent_id", "parentSessionId", "parent_session_id", "subagentType", "subagent_type" })
                    if (value.TryGetProperty(key, out var child) && child.ValueKind != JsonValueKind.Null) return false;
            string? prompt = null;
            if (update.TryGetProperty("prompt_id", out _) && !Text(update, "prompt_id", out prompt)) return false;
            if (provenance.TryGetProperty("promptId", out _))
            {
                if (!Text(provenance, "promptId", out var alias) || prompt is not null && prompt != alias) return false;
                prompt = alias;
            }
            if (prompt is not null && !CanonicalUuid(prompt) && !(prompt.StartsWith("task-completed-", StringComparison.Ordinal) && CanonicalUuid(prompt[15..]))) return false;
            if (kind == "hook_execution")
            {
                if (method.GetString() != "_x.ai/session/update" || !Text(update, "event_name", out var hook)
                    || hook is not ("session_start" or "user_prompt_submit" or "stop" or "stop_failure")) return false;
                kind = hook;
                if (hook == "user_prompt_submit" && prompt is null) return false;
                if (hook == "session_start" && prompt is not null) return false;
            }
            else if (kind is not ("turn_completed" or "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update")) return false;
            string? reason = null; long? elapsed = null;
            if (kind == "turn_completed")
            {
                if (method.GetString() != "_x.ai/session/update" || prompt is null || !Text(update, "stop_reason", out reason)
                    || reason is not ("end_turn" or "cancelled" or "error" or "max_tokens")
                    || !update.TryGetProperty("elapsed_ms", out var duration) || !duration.TryGetInt64(out var number) || number < 0) return false;
                elapsed = number;
            }
            metadata = new(eventId!, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), kind!, prompt, reason, elapsed); return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentOutOfRangeException or InvalidOperationException) { return false; }
    }

    private static bool KnownIgnoredLine(GrokActivityBinding binding, byte[] bytes)
    {
        // Unknown/malformed telemetry marks incomplete history. Legitimate user
        // content is ignored, without retaining or interpreting its body.
        try { using var doc = JsonDocument.Parse(bytes); return Unique(doc.RootElement)
            && Text(doc.RootElement, "method", out var method) && method == "session/update"
            && doc.RootElement.TryGetProperty("params", out var p) && Unique(p)
            && Text(p, "sessionId", out var rootSession) && rootSession == binding.RootSessionId
            && p.TryGetProperty("_meta", out var meta) && Unique(meta)
            && Text(meta, "eventId", out var eventId) && AgentActivityTracker.ValidIdentifier(eventId)
            && meta.TryGetProperty("agentTimestampMs", out var stamp) && stamp.TryGetInt64(out var milliseconds)
            && milliseconds >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            && p.TryGetProperty("update", out var u) && Unique(u)
            && Text(u, "sessionUpdate", out var kind) && kind == "user_message_chunk"; }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }
    private static bool Unique(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() <= 64
        && value.EnumerateObject().Select(row => row.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();
    private static bool Text(JsonElement value, string name, out string? text)
    { text = null; if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false; text = item.GetString(); return text is { Length: > 0 and <= 256 }; }
    private static bool CanonicalUuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value;
    private string Safe(string name) => FenceFiles.TryResolveUnlinked(_directory, name, out var path)
        ? path : throw new IOException("Update capture state path is unavailable.");
    private FileStream Lease() { var path = Safe(StateFile + ".lock"); Directory.CreateDirectory(_directory); return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private GrokUpdateCaptureState ReadLocked()
    {
        var path = Safe(StateFile);
        if (!File.Exists(path)) return new(1, _binding, _stream, null, 0, false, EmptyHash, EmptyHash, true, [], []);
        try
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Update capture state is too large.");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!Unique(doc.RootElement)
                || !doc.RootElement.TryGetProperty("binding", out var bindingJson) || !Unique(bindingJson)
                || !doc.RootElement.TryGetProperty("pending", out var pendingJson) || pendingJson.ValueKind != JsonValueKind.Array || pendingJson.EnumerateArray().Any(row => !Unique(row))
                || !doc.RootElement.TryGetProperty("recent", out var recentJson) || recentJson.ValueKind != JsonValueKind.Array || recentJson.EnumerateArray().Any(row => !Unique(row)))
                throw new InvalidDataException("Update capture state is ambiguous.");
            var state = JsonSerializer.Deserialize<GrokUpdateCaptureState>(doc.RootElement, JsonFile.Options);
            if (state is not { Schema: 1, Pending: not null, Recent: not null } || state.Binding != _binding || state.StreamPath != _stream
                || state.Cursor < 0 || state.FileIdentity is null && state.Cursor != 0 || state.Pending.Length > Capacity || state.Recent.Length > 512
                || !ValidHash(state.PrefixHash) || !ValidHash(state.AnchorHash)
                || state.FileIdentity is null && (state.DiscardingOversizedLine || state.Pending.Length > 0 || state.Recent.Length > 0)
                || state.Pending.Any(row => !ValidMetadata(row))
                || state.Recent.Any(row => row is null || !AgentActivityTracker.ValidIdentifier(row.EventId) || !ValidHash(row.Hash))
                || state.Pending.Select(row => row.EventId).Distinct().Count() != state.Pending.Length
                || state.Recent.Select(row => row.EventId).Distinct().Count() != state.Recent.Length
                || state.Pending.Any(row => !state.Recent.Any(item => item.EventId == row.EventId
                    && item.Hash == Fingerprint(row))))
                throw new InvalidDataException("Update capture state is incomplete or belongs to another launch.");
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Update capture state was preserved after a read failure.", error); }
    }
    private static bool ValidKind(string value) => value is "session_start" or "user_prompt_submit" or "stop" or "stop_failure" or "turn_completed" or "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update";
    internal static bool ValidMetadata(GrokUpdateMetadata? row)
    {
        if (row is null || !AgentActivityTracker.ValidIdentifier(row.EventId) || row.OccurredUtc == default || !ValidKind(row.Kind)
            || row.PromptId is not null && !CanonicalUuid(row.PromptId) && !(row.PromptId.StartsWith("task-completed-", StringComparison.Ordinal) && CanonicalUuid(row.PromptId[15..]))
            || row.LiveEligible && (row.CapturedUtc == default || row.CapturedUtc < row.OccurredUtc || row.CapturedUtc - row.OccurredUtc > SessionActivityStore.Freshness)
            || row.CapturedConsent is not null && (!row.LiveEligible || row.CapturedConsent.DestinationReference is { } reference && !Guid.TryParseExact(reference, "N", out _))) return false;
        if (row.Kind == "session_start" && row.PromptId is not null || row.Kind == "user_prompt_submit" && row.PromptId is null) return false;
        return row.Kind == "turn_completed"
            ? row.PromptId is not null && row.StopReason is "end_turn" or "cancelled" or "error" or "max_tokens" && row.ElapsedMs is >= 0
            : row.StopReason is null && row.ElapsedMs is null;
    }
    // Source dedupe excludes host observation/consent so a retry never acquires
    // a new consent epoch and legacy metadata-only ledgers remain readable.
    internal static string Fingerprint(GrokUpdateMetadata row) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(row with { CapturedUtc = default, LiveEligible = false, CapturedConsent = null })));
    private Attempt ReadAttempt(string identity)
    {
        if (_attempt is null && File.Exists(Safe(AttemptFile)))
        {
            if (new FileInfo(Safe(AttemptFile)).Length > 512 * 1024) throw new InvalidDataException("Capture attempt is too large.");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Safe(AttemptFile)));
                if (!Unique(document.RootElement)
                    || !document.RootElement.TryGetProperty("binding", out var binding) || !Unique(binding)
                    || !document.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array
                    || rows.EnumerateArray().Any(row => !Unique(row))) throw new InvalidDataException("Ambiguous capture attempt.");
                _attempt = JsonSerializer.Deserialize<Attempt>(document.RootElement, JsonFile.Options)
                    ?? throw new InvalidDataException("Missing capture attempt.");
            }
            catch (JsonException error) { throw new InvalidDataException("Original capture attempt preserved.", error); }
        }
        _attempt ??= new(_binding, _stream, identity, []);
        if (_attempt.Binding != _binding || _attempt.StreamPath != _stream || _attempt.FileIdentity != identity
            || _attempt.Rows is null || _attempt.Rows.Length > Capacity || _attempt.Rows.Any(row => !ValidMetadata(row) || row.CapturedUtc == default)
            || _attempt.Rows.Select(row => row.EventId).Distinct().Count() != _attempt.Rows.Length)
            throw new InvalidDataException("Capture attempt belongs to another source or is invalid; preserved.");
        return _attempt;
    }
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string HashRange(FileStream input, long offset, int length)
    { input.Position = offset; var bytes = new byte[length]; input.ReadExactly(bytes); return Convert.ToHexString(SHA256.HashData(bytes)); }

    [StructLayout(LayoutKind.Sequential)] private struct NativeFileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out NativeFileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    private static string Identity(SafeFileHandle file, string expected)
    {
        var path = new StringBuilder(32768); var count = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (count == 0 || count >= path.Capacity || !GetFileInformationByHandle(file, out var info)) throw new IOException("Update stream identity is unavailable.");
        var actual = path.ToString(); if (actual.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) actual = "\\\\" + actual[8..];
        else if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("The opened update stream does not match its binding.");
        return $"{info.Volume:X8}-{info.IndexHigh:X8}-{info.IndexLow:X8}-{info.Creation.dwHighDateTime:X8}-{info.Creation.dwLowDateTime:X8}";
    }
}
