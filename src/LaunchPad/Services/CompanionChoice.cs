namespace LaunchPad.Services;

// Chat choice is independent of the coding-agent and project launch settings.
public sealed record CompanionChoice(string Name, string? ChatUrl, string? InstructionsUrl)
{
    public static IReadOnlyList<CompanionChoice> All { get; } = Array.AsReadOnly(new[]
    {
        new CompanionChoice("ChatGPT", "https://chatgpt.com/", "https://learn.chatgpt.com/docs/personalize"),
        new CompanionChoice("Claude", "https://claude.ai/", "https://support.claude.com/en/articles/10185728-understanding-claude-s-personalization-features"),
        new CompanionChoice("Grok", ExternalLinks.GrokChat, "https://x.ai/grok"),
        new CompanionChoice("Gemini", "https://gemini.google.com/", "https://support.google.com/gemini/answer/15236321?hl=en"),
        new CompanionChoice("Other chat", null, null)
    });
}
