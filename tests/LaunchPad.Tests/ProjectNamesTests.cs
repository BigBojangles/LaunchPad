using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class ProjectNamesTests
{
    [Theory]
    [InlineData("My Game", "My Game")]
    [InlineData("  Hello   World  ", "Hello World")]
    [InlineData("Bad:Name?", "Bad Name")]
    public void SanitizesValidNames(string input, string expected)
    {
        Assert.True(ProjectNames.TrySanitize(input, out var name, out var error));
        Assert.Equal(expected, name);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("CON")]
    [InlineData("...")]
    public void RejectsInvalidNames(string input)
    {
        Assert.False(ProjectNames.TrySanitize(input, out var name, out var error));
        Assert.Equal("", name);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
