namespace LaunchPad.Services.Fence;

public readonly record struct AgentLaunch(string Id, string? Program, string? SourceFile)
{
    public static AgentLaunch Grok { get; } = new(AgentChoice.Grok, null, null);

    public bool IsGrok => string.Equals(Id, AgentChoice.Grok, StringComparison.Ordinal);
}

public sealed record AgentOption(string Id, string Label, bool Enabled, string? DisabledReason);

public static class AgentChoice
{
    public const string Grok = "grok";
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string Custom = "custom";
    public const string Marker = "AGENT-PICK";

    /// <summary>Single list for UI + guest id mapping. Codex/Claude enabled after 2026-10-05 builder image bake.</summary>
    public static IReadOnlyList<AgentOption> Options { get; } =
    [
        new(Grok, "Grok", true, null),
        new(Codex, "Codex CLI", true, null),
        new(Claude, "Claude Code", true, null),
        new(Custom, "Custom", true, null)
    ];

    public static bool Known(string? id) =>
        id is Grok or Codex or Claude or Custom;

    public static string Normalize(string? id) => Known(id) ? id! : Grok;

    public static AgentOption? Find(string? id)
    {
        id = Normalize(id);
        foreach (var option in Options)
        {
            if (string.Equals(option.Id, id, StringComparison.Ordinal))
                return option;
        }

        return null;
    }

    public static bool IsEnabled(string? id) => Find(id)?.Enabled == true;

    public static string Header(string? id) => "AGENT " + Normalize(id) + "\n";

    public static bool SafeProgram(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            return false;

        foreach (var character in name)
        {
            if (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-')
                continue;
            return false;
        }

        return name != "." && name != "..";
    }

    public static string? ProgramHeader(string? name)
    {
        if (!SafeProgram(name))
            return null;
        return "AGENT-CMD " + name + "\n";
    }
}