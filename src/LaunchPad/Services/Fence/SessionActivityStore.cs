using System.Text.Json;
using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

public sealed record SessionActivityContext(string Directory, string Generation, string? ProjectPath = null, string? AgentId = null);
public sealed record SessionActivityObservation(string Generation, bool Connected, DateTimeOffset ObservedUtc,
    AgentActivitySnapshot Activity, bool Synchronized = false, bool HistoryComplete = false, string? Source = null);

/// <summary>Shared host-side observation for desktop/owner handoff, not a permission boundary.</summary>
public static class SessionActivityStore
{
    public const string StateFile = "activity-state.json";
    public const string EventsFile = "activity-events.jsonl";
    private const int MaxStateBytes = 16 * 1024;
    private const long MaxJournalBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(4);

    public static SessionActivityObservation? Read(string directory, string generation)
    {
        if (!Guid.TryParseExact(generation, "N", out _) || !FenceFiles.TryResolveUnlinked(directory, StateFile, out var path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxStateBytes) return null;
            var value = JsonSerializer.Deserialize<SessionActivityObservation>(stream, AgentActivityTracker.JsonOptions);
            return value is not null && value.Generation == generation && value.ObservedUtc != default
                && value.Activity is not null && AgentActivityTracker.IsValidSnapshot(value.Activity, generation) ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static AgentActivitySnapshot Current(string directory, string generation, DateTimeOffset now)
    {
        var value = Read(directory, generation);
        return value is { Connected: true, Synchronized: true } && now >= value.ObservedUtc && now - value.ObservedUtc <= Freshness
            ? value.Activity : AgentActivitySnapshot.Unavailable;
    }

    public static void Publish(SessionActivityContext context, AgentActivitySnapshot activity, bool connected,
        IReadOnlyList<AgentActivityEvent>? events = null, bool synchronized = true, bool historyComplete = true, string? source = null)
    {
        if (!Guid.TryParseExact(context.Generation, "N", out _) || !AgentActivityTracker.IsValidSnapshot(activity, context.Generation))
            throw new ArgumentException("Invalid activity observation.");
        if (!FenceFiles.TryResolveUnlinked(context.Directory, StateFile, out var path)
            || !FenceFiles.TryResolveUnlinked(context.Directory, EventsFile, out var journal)
            || !FenceFiles.TryResolveUnlinked(context.Directory, EventsFile + ".previous", out var previous))
            throw new IOException("Activity observation path is unavailable.");
        if (events is { Count: > 0 })
        {
            if (events.Any(value => !AgentActivityTracker.IsValidEvent(value, context.Generation)))
                throw new ArgumentException("Invalid activity event.");
            if (File.Exists(journal) && new FileInfo(journal).Length >= MaxJournalBytes) File.Move(journal, previous, overwrite: true);
            using var output = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read);
            foreach (var value in events)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AgentActivityTracker.JsonOptions);
                output.Write(bytes);
                output.WriteByte((byte)'\n');
            }
            output.Flush(flushToDisk: true);
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new SessionActivityObservation(context.Generation, connected, DateTimeOffset.UtcNow,
                    activity, synchronized, historyComplete, source), AgentActivityTracker.JsonOptions);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
