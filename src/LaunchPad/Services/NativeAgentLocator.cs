using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Finds installed Windows programs; never downloads or substitutes an agent.</summary>
public sealed class NativeAgentLocator(GrokLocator grok, AppPaths paths)
{
    public string? Find(AgentLaunch agent)
    {
        if (agent.Id == AgentChoice.Custom)
            return !string.IsNullOrWhiteSpace(agent.SourceFile) && Path.IsPathFullyQualified(agent.SourceFile)
                && Supported(agent.SourceFile) && File.Exists(agent.SourceFile) ? Path.GetFullPath(agent.SourceFile) : null;
        if (agent.Id == AgentChoice.Grok && grok.FindGrokExecutable() is { } installed) return installed;
        var names = agent.Id switch
        {
            AgentChoice.Grok => new[] { "grok.exe", "grok.cmd" },
            AgentChoice.Codex => new[] { "codex.exe", "codex.cmd" },
            AgentChoice.Claude => new[] { "claude.exe", "claude.cmd" },
            _ => Array.Empty<string>()
        };
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(new[] { Path.Combine(paths.UserProfile, ".local", "bin"), Path.Combine(paths.UserProfile, "AppData", "Roaming", "npm") });
        foreach (var directory in directories)
        {
            if (!Path.IsPathFullyQualified(directory.Trim('"'))) continue;
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
            }
        }
        // Standalone Codex maintains a current package pointer outside PATH.
        // Prefer the user's existing PATH/bin/npm choice; never select an old
        // release or a daemon/sandbox helper by scanning package directories.
        if (agent.Id == AgentChoice.Codex && Path.IsPathFullyQualified(paths.UserProfile))
        {
            var candidate = Path.Combine(paths.UserProfile, ".codex", "packages", "standalone", "current", "bin", "codex.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat";
}
