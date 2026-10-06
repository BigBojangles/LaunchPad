using Xunit;

namespace LaunchPad.Tests;

// Optional live checks must report unmet prerequisites as skipped, never passed.
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variable, string? expectedValue = null)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || (expectedValue is not null && value != expectedValue))
            Skip = $"Unverified: set {variable}" + (expectedValue is null ? "." : $"={expectedValue} to run this live check.");
    }
}

public sealed class EnvironmentTheoryAttribute : TheoryAttribute
{
    public EnvironmentTheoryAttribute(string variable, string? expectedValue = null)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || (expectedValue is not null && value != expectedValue))
            Skip = $"Unverified: set {variable}" + (expectedValue is null ? "." : $"={expectedValue} to run this live check.");
    }
}
