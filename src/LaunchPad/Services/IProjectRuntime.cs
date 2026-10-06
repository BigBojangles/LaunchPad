using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Host-specific launching behind a UI-independent application boundary.</summary>
public interface IProjectRuntime
{
    bool HasVirtualMachine { get; }
    string? HostAgentExecutable { get; }
    FenceStartAvailability FenceStartAvailability { get; }
    IFencedProjectSession CreateFencedSession();
    bool IsFencedOpen(string project);
    bool IsHostOpen(string project);
    Task OpenFencedAsync(string project, CancellationToken cancellationToken, IProgress<string>? progress);
    Task<bool> EnsureHostAgentAsync();
    bool TryLaunchHostAgent(string project, LaunchPlacement? placement, out string error);
    Task<string?> SendProjectAsync(string project, CancellationToken cancellationToken);
    SessionRecord? DescribeFenced(string project);
    SessionRecord? DescribeHost(string project);
    void UpdateSessionTitles(string project, string vmTitle, string hostTitle) { }
    string? FocusSession(SessionRecord session) => "Window focus is unavailable for this runtime.";
}

public sealed record FenceStartAvailability(string? Reason, bool Blocked);

/// <summary>The dialog's session operations; the platform owns their implementation.</summary>
public interface IFencedProjectSession
{
    IReadOnlyList<string> Warnings { get; }
    string? LatestAside(string project);
    ScanReport? LatestScan(string project);
    ProjectRecovery ReadRecovery(string project);
    Task CopyBackToProjectAsync(string project, string? recoveryDirectory = null);
    Task ResumeSavedAsync(string project, LaunchPlacement? placement, IProgress<string>? progress, CancellationToken cancellationToken);
    Task StartAsync(string project, LaunchPlacement? placement, IProgress<string>? progress,
        CancellationToken cancellationToken, Action<IReadOnlyList<string>>? warningsChanged = null,
        bool leaveRunning = false);
}
