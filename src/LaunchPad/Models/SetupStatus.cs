namespace LaunchPad.Models;

public enum SetupPhase
{
    Checking,
    Installing,
    Ready,
    Failed
}

public sealed record SetupStatus(SetupPhase Phase, string UserMessage);
