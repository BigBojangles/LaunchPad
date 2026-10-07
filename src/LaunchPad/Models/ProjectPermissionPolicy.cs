namespace LaunchPad.Models;

public sealed record ApprovedDestination(string Host, int Port);
public sealed record ProjectPermissionPolicy(int Version, string Preset, ApprovedDestination[] Destinations)
{
    public static ProjectPermissionPolicy Standard => new(1, "standard", []);
}
public sealed record AppliedPermissionPolicy(int Version, string ProjectPath, string Generation, int OwnerPid, long OwnerStartTicks,
    int MachinePid, long MachineStartTicks, ProjectPermissionPolicy Policy, string NetworkMode, DateTimeOffset AppliedUtc, DateTimeOffset? EndedUtc,
    string? TemporaryPermissionsDisabledReason = null);
public sealed record TemporaryTestPermission(int Version, string Id, string ProjectPath, string Generation, int OwnerPid, long OwnerStartTicks,
    string Tool, string? Program, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc, long IssuedUptimeMs, long ExpiresUptimeMs);
public sealed record SessionPermissionExceptions(int Version, string Generation, TemporaryTestPermission[] Grants, DateTimeOffset? EndedUtc);
