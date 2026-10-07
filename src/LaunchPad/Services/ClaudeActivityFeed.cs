using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Redacted callback queue and checkpoint. Publication precedes acknowledgment.</summary>
public sealed class ClaudeActivityFeed(string directory, ClaudeActivityBinding binding, TimeProvider? time = null, string? ownerEpoch = null)
{
    public const string FileName = "claude-activity-feed.json";
    public const int Capacity = 256;
    private const int MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonFile.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private ClaudeActivityAdapter? _adapter;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // Native producer/consumer supply their authenticated console-lifetime nonce.
    // Otherwise each feed instance owns a new epoch; reopening cannot promote old data.
    private readonly string _ownerEpoch = ValidEpoch(ownerEpoch) ? ownerEpoch! : ownerEpoch is null
        ? Guid.NewGuid().ToString("N") : throw new ArgumentException("Invalid Claude producer lifetime.");
    public sealed record Ledger([property: JsonRequired] int Schema, [property: JsonRequired] ClaudeActivityBinding Binding,
        [property: JsonRequired] long Sequence, [property: JsonRequired] ClaudeActivityCheckpoint Checkpoint,
        [property: JsonRequired] ClaudeActivityReceipt[] Pending);

    public ClaudeActivityReceipt? Capture(string raw, DateTimeOffset capturedUtc)
    {
        using var lease = Lease();
        var state = ReadLocked();
        if (state.Checkpoint.Closed || state.Pending.Any(row => row.Metadata.EventName == "SessionEnd")) return null;
        if (state.Sequence == long.MaxValue)
            throw new IOException("Claude status queue has exhausted its sequence. Earlier observations were preserved.");
        var receipt = ClaudeActivityAdapter.Capture(raw, binding, Guid.NewGuid().ToString("N"), state.Sequence + 1, capturedUtc);
        if (receipt is null) return null;
        receipt = receipt with { ProducerEpoch = _ownerEpoch };
        if (state.Pending.Length >= Capacity)
            throw new IOException("Claude status queue is full. Pending observations were preserved.");
        Save(state with { Sequence = receipt.Sequence, Pending = state.Pending.Append(receipt).ToArray() });
        return receipt;
    }

    public Ledger Read() { using var lease = Lease(); return ReadLocked(); }

    public void Publish(bool connected, Action<AgentActivitySnapshot> publisher, bool live = false)
    {
        using var lease = Lease();
        var state = ReadLocked();
        if (_adapter is null || _adapter.Checkpoint.Cursor != state.Checkpoint.Cursor)
            _adapter = new(binding, state.Checkpoint, _time);
        foreach (var receipt in state.Pending)
        {
            if (receipt.Sequence <= _adapter.Checkpoint.Cursor) continue;
            if (!_adapter.Ingest(receipt, live && receipt.ProducerEpoch == _ownerEpoch))
                throw new InvalidDataException("Claude observation could not be projected.");
        }
        if (_adapter.Checkpoint.Cursor != state.Checkpoint.Cursor)
        {
            state = state with { Checkpoint = _adapter.Checkpoint };
            Save(state); // Retain original receipts until the public sink succeeds.
        }
        publisher(connected ? _adapter.Current() : AgentActivitySnapshot.Unavailable);
        if (state.Pending.Length > 0) Save(state with { Pending = [] });
    }

    private Ledger ReadLocked()
    {
        var path = Safe(FileName);
        if (!File.Exists(path)) return new(1, binding, 0, new ClaudeActivityAdapter(binding).Checkpoint, []);
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length > MaxBytes) throw new InvalidDataException("Claude status queue is too large.");
            using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 20 });
            if (!Unique(document.RootElement)) throw new InvalidDataException("Claude status queue has duplicate fields.");
            var state = JsonSerializer.Deserialize<Ledger>(document.RootElement, Options);
            if (state is not { Schema: 1, Checkpoint: not null, Pending: not null } || state.Binding != binding
                || state.Sequence < 0 || state.Checkpoint.Cursor > state.Sequence || state.Pending.Length > Capacity
                || state.Pending.Any(row => row is null || row.Sequence > state.Sequence
                    || !ValidEpoch(row.ProducerEpoch)
                    || !new ClaudeActivityAdapter(binding).Ingest(row))
                || state.Pending.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != state.Pending.Length
                || state.Pending.Select(row => row.Sequence).Distinct().Count() != state.Pending.Length
                || !state.Pending.Select(row => row.Sequence).SequenceEqual(state.Pending.Select(row => row.Sequence).Order())
                || state.Pending.Length == 0 && state.Sequence != state.Checkpoint.Cursor
                || state.Pending.Length > 0 && (state.Pending[^1].Sequence != state.Sequence
                    || state.Pending[0].Sequence - 1 > state.Checkpoint.Cursor
                    || state.Pending.Where((row, index) => row.Sequence != state.Pending[0].Sequence + index).Any())
                || state.Checkpoint.Closed && state.Pending.Any(row => row.Sequence > state.Checkpoint.Cursor))
                throw new InvalidDataException("Claude status queue belongs to another launch or has invalid observations.");
            _ = new ClaudeActivityAdapter(binding, state.Checkpoint);
            return state;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("Claude status queue could not be read. Its original file was preserved.", error); }
    }
    private void Save(Ledger state)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(state, JsonFile.Options).Length > MaxBytes)
            throw new IOException("Claude status queue is full. Earlier observations were preserved.");
        ReturnRecovery.SaveAtomic(Safe(FileName), state);
    }
    private FileStream Lease()
    {
        var path = Safe(FileName + ".lock");
        Directory.CreateDirectory(directory);
        for (var attempt = 0; ; attempt++)
        {
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (attempt < 8 && (error.HResult & 0xffff) is 32 or 33) { Thread.Sleep(20); }
        }
    }
    private string Safe(string name) => FenceFiles.TryResolveUnlinked(directory, name, out var path)
        ? path : throw new IOException("Claude status path is unavailable.");
    private static bool ValidEpoch(string? value) => Guid.TryParseExact(value, "N", out var parsed) && value == parsed.ToString("N");
    private static bool Unique(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == value.EnumerateObject().Count() && value.EnumerateObject().All(field => Unique(field.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(Unique),
        _ => true
    };
}
