namespace LaunchPad.Models;

public enum SessionKind { VirtualMachine, HostWindow }
public enum SessionLifecycle { Starting, Running, Busy, NeedsAnswer, Stopping, Stopped, Failed, Unknown }

/// <summary>UI-independent identity and lifecycle information for one session.</summary>
public sealed record SessionRecord(
    string Id,
    string ProjectPath,
    string AgentId,
    SessionKind Kind,
    int? ProcessId,
    int? TerminalProcessId,
    nint? WindowHandle,
    SessionLifecycle State,
    string? Error = null,
    SessionWindowIdentity? Window = null);

/// <summary>Process creation times prevent a reused PID or window from being selected.</summary>
public sealed record SessionWindowIdentity(int ClientPid, long ClientStartTicks, long ConsoleHandle,
    int ConsolePid, long ConsoleStartTicks, long WindowHandle, int WindowPid, long WindowStartTicks);
