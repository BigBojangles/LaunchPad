using System.Text;
using System.Text.Json;

namespace LaunchPad.Services;

/// <summary>Redacted completion identifiers. This receipt alone does not establish a root run or its duration.</summary>
public sealed record CodexCompletionMetadata(string Generation, string ThreadId, string TurnId, DateTimeOffset CapturedUtc);

/// <summary>
/// Decodes Codex's notify payload without retaining prompts or assistant messages.
/// The caller must authenticate the producer and independently identify the current root thread/turn.
/// Parsing never installs configuration, changes activity or publishes notifications.
/// </summary>
public static class CodexCompletionDecoder
{
    public const int MaxInputBytes = 64 * 1024;

    public static CodexCompletionMetadata? Capture(string json, string generation, string project,
        string rootThreadId, string activeTurnId, DateTimeOffset capturedUtc)
    {
        if (json is null || !Guid.TryParseExact(generation, "N", out _)
            || !Identifier(rootThreadId) || !Identifier(activeTurnId)
            || capturedUtc == default || capturedUtc.Offset != TimeSpan.Zero
            || Encoding.UTF8.GetByteCount(json) > MaxInputBytes || !ProjectPath(project, out var expectedProject)) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || names.Count > 64) return null;
            // No child/source marker has yet been proved on the pinned notify schema.
            // Refuse marked payloads; absence is not a substitute for caller root attribution.
            if (names.Contains("agent_id") || names.Contains("parent_session_id")
                || names.Contains("parent_thread_id") || names.Contains("source")) return null;
            if (!Text(root, "type", out var type) || type != "agent-turn-complete"
                || !Text(root, "thread-id", out var thread) || thread != rootThreadId
                || !Text(root, "turn-id", out var turn) || turn != activeTurnId
                || !Text(root, "cwd", out var cwd) || !ProjectPath(cwd, out var actualProject)
                || !string.Equals(expectedProject, actualProject,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            return new(generation, thread, turn, capturedUtc);
        }
        catch (JsonException) { return null; }
    }

    private static bool Identifier(string value) => Guid.TryParseExact(value, "D", out _);
    private static bool Text(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return value.Length > 0 && !value.Any(char.IsControl);
    }
    private static bool ProjectPath(string path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrEmpty(path) || path.Length > 32768 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) return false;
        try { fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); return true; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
