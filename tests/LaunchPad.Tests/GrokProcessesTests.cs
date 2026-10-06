using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class GrokProcessesTests
{
    [Fact]
    public void ReadsThisProcessWorkingDirectory()
    {
        var cwd = GrokProcesses.TryGetWorkingDirectory(Environment.ProcessId);

        Assert.NotNull(cwd);
        Assert.Equal(
            GrokProcesses.NormalizePath(Environment.CurrentDirectory),
            GrokProcesses.NormalizePath(cwd));
    }

    [Theory]
    [InlineData("grok", true)]
    [InlineData("grok.exe", true)]
    [InlineData("Grok", true)]
    [InlineData("WindowsTerminal", false)]
    [InlineData("", false)]
    public void AcceptsProcessNamesThatStartWithGrok(string name, bool expected)
    {
        Assert.Equal(expected, GrokProcesses.IsGrokProcessName(name));
    }
}
