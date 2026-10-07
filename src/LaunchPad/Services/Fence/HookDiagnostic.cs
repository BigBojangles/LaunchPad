using System.Text.Json;
using System.Text.RegularExpressions;

namespace LaunchPad.Services.Fence;

// Passive schema witnesses only. These records never enter the activity tracker
// or notification outbox, and cannot request any host operation.
public sealed record HookDiagnostic(int Version, string Nonce, long CapturedUnixMs,
    string Event, Dictionary<string, string> Fields, string? SessionHash,
    string? TurnHash, string NotificationType, bool? StopHookActive,
    int BackgroundTasksCount, int SessionCronsCount, bool CwdMatchesProject);

public static class HookDiagnostics
{
    public const string Prefix = "LP-DIAG ";
    public const string FileName = "hook-diagnostics.jsonl";
    public const int MaxLineBytes = 4096;
    public const long MaxJournalBytes = 2 * 1024 * 1024;
    public static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    {
        "SessionStart", "SessionEnd", "UserPromptSubmit", "PreToolUse", "PostToolUse",
        "PostToolUseFailure", "PermissionDenied", "Stop", "StopFailure", "Notification",
        "SubagentStart", "SubagentStop", "PreCompact", "PostCompact", "Unknown"
    };
    public static readonly HashSet<string> FieldNames = new(StringComparer.Ordinal)
    {
        "hookEventName", "hook_event_name", "sessionId", "session_id", "turnId", "turn_id",
        "promptId", "prompt_id", "notificationType", "notification_type", "type", "message",
        "title", "cwd", "workspaceRoot", "workspace_root", "toolName", "toolInput",
        "tool_name", "tool_input", "toolUseId", "requestId", "agentId", "subagentId",
        "parentSessionId", "stopHookActive", "stop_hook_active", "backgroundTasks",
        "background_tasks", "sessionCrons", "session_crons", "reason", "stopReason", "status"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "str", "int", "float", "bool", "list", "dict", "NoneType" };
    private static readonly HashSet<string> NotificationTypes = new(StringComparer.Ordinal)
        { "permission_prompt", "idle_prompt", "elicitation_dialog", "auth_success", "info",
          "warning", "task_complete", "other", "absent" };
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static HookDiagnostic? Decode(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)
            || System.Text.Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return null;
        try
        {
            var value = JsonSerializer.Deserialize<HookDiagnostic>(line.AsSpan(Prefix.Length), Options);
            if (value is null || value.Version != 1 || value.Nonce is null
                || !Regex.IsMatch(value.Nonce, "\\A[0-9a-f]{32}\\z")
                || value.CapturedUnixMs < 0 || value.CapturedUnixMs > 4102444800000
                || value.Event is null || !Events.Contains(value.Event)
                || value.Fields is null || value.Fields.Count > FieldNames.Count
                || value.Fields.Any(item => !FieldNames.Contains(item.Key) || !Types.Contains(item.Value))
                || !Hash(value.SessionHash) || !Hash(value.TurnHash)
                || value.NotificationType is null || !NotificationTypes.Contains(value.NotificationType)
                || value.BackgroundTasksCount is < -1 or > 1000000
                || value.SessionCronsCount is < -1 or > 1000000) return null;
            return value;
        }
        catch (JsonException) { return null; }
    }

    private static bool Hash(string? value) => value is null || Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z");

    public static void Append(SessionActivityContext context, IReadOnlyList<HookDiagnostic> records)
    {
        if (records.Count == 0 || context.AgentId != "grok"
            || !FenceFiles.TryResolveUnlinked(context.Directory, FileName, out var path)) return;
        try
        {
            using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            foreach (var record in records)
            {
                // Serialize the typed projection, never the received raw JSON.
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    { receivedUtc = DateTimeOffset.UtcNow, generation = context.Generation, provider = "grok", diagnostic = record });
                if (output.Length + bytes.Length + 1 > MaxJournalBytes) break;
                output.Write(bytes);
                output.WriteByte((byte)'\n');
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
