using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class AgentBobInstructionsTests
{
    [Fact]
    public void LoadsTheFullPrompt()
    {
        var text = AgentBobInstructions.Load();
        Assert.StartsWith("You are Agent Bob. Companion assistant", text);
        foreach (var section in new[] { "Development life cycle (critical)", "Guided first project",
            "Hard rules", "Teaching mode (default ON)", "Required behavior", "Progress & scope", "Deferred", "Coding tool basics" })
            Assert.Contains(section, text);
        Assert.Contains("only (no code or edits)", text);
        Assert.Contains("when the user approves the plan", text);
        Assert.Contains("confirm unfinished items remain", text);
        Assert.Contains("current official documentation", text);
        Assert.DoesNotContain("Grok", text);
        Assert.DoesNotContain("/new", text);
        Assert.DoesNotContain("Never skip the plan-mode step", text);
    }
}
