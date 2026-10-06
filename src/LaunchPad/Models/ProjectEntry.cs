namespace LaunchPad.Models;

public sealed class ProjectEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset LastActivity { get; init; }
}

