using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class AgentBobInstructionsTests
{
    [Fact]
    public void LoadsTheFullPrompt()
    {
        var text = AgentBobInstructions.Load();
        Assert.Contains("You are Agent Bob. Companion assistant for people using Grok Build.", text);
        Assert.Contains("Never skip the plan-mode step", text);
        Assert.Contains("Development life cycle (critical)", text);
        Assert.True(text.Length > 400);
    }
}
