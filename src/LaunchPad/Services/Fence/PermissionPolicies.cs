using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

// Presets describe requested policy; the owner records what this launch actually
// applied. A saved Strict choice is never evidence of a live network allowlist.
public static class PermissionPolicies
{
    public static ProjectPermissionPolicy Validate(ProjectPermissionPolicy? policy)
    {
        if (policy is null || policy.Version != 1 || policy.Preset is not ("strict" or "standard" or "troubleshoot")
            || policy.Destinations is null || policy.Destinations.Length > 64)
            throw new InvalidDataException("The saved project permission policy is invalid. Nothing was approved.");
        var destinations = new List<ApprovedDestination>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in policy.Destinations)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Host) || item.Host.Length > 253 || item.Port is < 1 or > 65535
                || item.Host.Any(character => character > 127 || char.IsWhiteSpace(character))
                || Uri.CheckHostName(item.Host) != UriHostNameType.Dns || item.Host.Contains('*') || item.Host.EndsWith('.'))
                throw new InvalidDataException("Use an exact destination hostname and port; wildcards and URLs are not allowed.");
            var host = item.Host.ToLowerInvariant();
            if (!seen.Add(host + ":" + item.Port.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidDataException("An approved destination is listed twice.");
            destinations.Add(new(host, item.Port));
        }
        return policy with { Destinations = destinations.ToArray() };
    }

    public static void RequireLaunchSupport(ProjectPermissionPolicy policy)
    {
        policy = Validate(policy);
        if (policy.Preset == "strict")
            throw new InvalidOperationException("Strict is saved, but destination-filtered VM networking is not available in this build. No VM was started. Choose Standard to use the current fenced setup.");
    }

    public static SessionOwnerIdentity? LiveOwner(string project)
    {
        try
        {
            var owner = SessionGuardian.TryReadLiveOwner(ProjectSessionStore.Current(Path.Combine(QemuLayout.Root, "sessions", QemuLayout.ProjectKey(project))));
            return owner is not null && SameProject(owner.ProjectPath, project) ? owner : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }
    }

    public static bool Alive(SessionOwnerIdentity owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.OwnerPid);
            using var machine = Process.GetProcessById(owner.MachinePid);
            return !process.HasExited && !machine.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.OwnerStartTicks
                && machine.StartTime.ToUniversalTime().Ticks == owner.MachineStartTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string FilePath(AppPaths paths, string project, string generation, string suffix)
    {
        if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid permission session generation.");
        return WindowsTestControls.Safe(WindowsTestControls.ProjectRoot(paths, project), generation + suffix);
    }

    public static void Begin(AppPaths paths, SessionOwnerIdentity owner, ProjectPermissionPolicy policy, DateTimeOffset? now = null)
    {
        if (owner.ProjectPath is null) return;
        RequireLaunchSupport(policy);
        var root = WindowsTestControls.ProjectRoot(paths, owner.ProjectPath);
        PrepareRoot(root);
        using var lease = MutationLease(paths, owner.ProjectPath);
        var path = FilePath(paths, owner.ProjectPath, owner.Generation, "-permissions.json");
        if (File.Exists(path)) throw new InvalidDataException("Permission generation already exists; it was preserved.");
        ReturnRecovery.SaveAtomic(path, new AppliedPermissionPolicy(1, Path.GetFullPath(owner.ProjectPath), owner.Generation,
            owner.OwnerPid, owner.OwnerStartTicks, owner.MachinePid, owner.MachineStartTicks, Validate(policy), "standard",
            now ?? DateTimeOffset.UtcNow, null));
    }

    public static AppliedPermissionPolicy? Applied(AppPaths paths, string project, SessionOwnerIdentity owner)
    {
        var root = WindowsTestControls.ProjectRoot(paths, project);
        FilePath(paths, project, owner.Generation, "-permissions.json");
        var record = WindowsTestControls.Read<AppliedPermissionPolicy>(root, owner.Generation + "-permissions.json", 65536);
        if (record is null) return null;
        if (record.Version != 1 || !SameProject(record.ProjectPath, project) || record.Generation != owner.Generation
            || record.OwnerPid != owner.OwnerPid || record.OwnerStartTicks != owner.OwnerStartTicks
            || record.MachinePid != owner.MachinePid || record.MachineStartTicks != owner.MachineStartTicks
            || record.NetworkMode != "standard" || record.Policy is null || Validate(record.Policy).Preset == "strict"
            || record.TemporaryPermissionsDisabledReason is { Length: > 512 })
            throw new InvalidDataException("Applied session permissions do not match this VM owner.");
        return record;
    }

    public static SessionPermissionExceptions? Exceptions(AppPaths paths, string project, SessionOwnerIdentity owner)
    {
        var root = WindowsTestControls.ProjectRoot(paths, project);
        FilePath(paths, project, owner.Generation, "-exceptions.json");
        var record = WindowsTestControls.Read<SessionPermissionExceptions>(root, owner.Generation + "-exceptions.json", 65536);
        if (record is not null && (record.Version != 1 || record.Generation != owner.Generation || record.Grants is null || record.Grants.Length > 16))
            throw new InvalidDataException("The saved temporary permissions are invalid.");
        if (record is not null) foreach (var grant in record.Grants) ValidateGrant(grant, project, owner);
        return record;
    }

    public static TemporaryTestPermission Grant(AppPaths paths, SettingsStore settings, string project, SessionOwnerIdentity owner,
        string tool, string? program, DateTimeOffset? now = null, Func<bool>? isLive = null, long? uptimeMs = null)
    {
        project = Path.GetFullPath(project);
        using var lease = MutationLease(paths, project);
        if (!(isLive?.Invoke() ?? Alive(owner)) || !SameProject(owner.ProjectPath, project)) throw new InvalidOperationException("Open this project's VM before granting a temporary permission.");
        var applied = Applied(paths, project, owner);
        if (settings.PermissionPolicyFor(project).Preset != "troubleshoot" || applied?.Policy.Preset != "troubleshoot" || applied.EndedUtc is not null
            || applied.TemporaryPermissionsDisabledReason is not null)
            throw new InvalidOperationException("Save Troubleshoot and reopen this VM before adding a temporary permission.");
        var stamp = now ?? DateTimeOffset.UtcNow;
        var uptime = uptimeMs ?? Environment.TickCount64;
        if (stamp > DateTimeOffset.MaxValue.AddMinutes(-30) || uptime < 0 || uptime > long.MaxValue - 1800000)
            throw new InvalidDataException("Permission clock is outside its supported range.");
        var grant = new TemporaryTestPermission(1, Guid.NewGuid().ToString("N"), project, owner.Generation, owner.OwnerPid, owner.OwnerStartTicks,
            tool, program, stamp, stamp.AddMinutes(30), uptime, uptime + 1800000);
        ValidateGrant(grant, project, owner);
        var previous = Exceptions(paths, project, owner);
        if (previous?.EndedUtc is not null) throw new InvalidOperationException("The permission session has ended.");
        var grants = (previous?.Grants ?? []).Where(item => item.ExpiresUtc > stamp && item.ExpiresUptimeMs > uptime
            && !(item.Tool == tool && string.Equals(item.Program, program, StringComparison.OrdinalIgnoreCase))).Append(grant).ToArray();
        if (grants.Length > 16) throw new InvalidOperationException("Remove a temporary permission before adding another.");
        ReturnRecovery.SaveAtomic(FilePath(paths, project, owner.Generation, "-exceptions.json"), new SessionPermissionExceptions(1, owner.Generation, grants, null));
        return grant;
    }

    public static void Revoke(AppPaths paths, string project, SessionOwnerIdentity owner)
    {
        using var lease = MutationLease(paths, project);
        if (Applied(paths, project, owner) is not { EndedUtc: null }) throw new InvalidOperationException("The permission session has ended.");
        ReturnRecovery.SaveAtomic(FilePath(paths, project, owner.Generation, "-exceptions.json"), new SessionPermissionExceptions(1, owner.Generation, [], null));
    }

    public static string TestPermission(ProjectPermissionPolicy policy, string legacyPermission, string project, SessionOwnerIdentity owner,
        WindowsTestRequest request, AppliedPermissionPolicy? applied, SessionPermissionExceptions? exceptions, DateTimeOffset now, bool ownerAlive, long? uptimeMs = null, bool clockValid = true)
    {
        policy = Validate(policy);
        if (legacyPermission is not ("automatic" or "confirm")) throw new InvalidDataException("Invalid Windows test permission.");
        if (request.Generation != owner.Generation || !SameProject(owner.ProjectPath, project) || !ownerAlive)
            throw new InvalidDataException("Windows test session owner changed. Nothing was approved.");
        if (policy.Preset == "strict") return "confirm";
        if (clockValid && policy.Preset == "troubleshoot" && applied?.Policy.Preset == "troubleshoot" && applied.EndedUtc is null
            && applied.TemporaryPermissionsDisabledReason is null && exceptions is { EndedUtc: null } && exceptions.Generation == owner.Generation)
            foreach (var grant in exceptions.Grants)
            {
                ValidateGrant(grant, project, owner);
                var uptime = uptimeMs ?? Environment.TickCount64;
                if (grant.IssuedUtc <= now && now < grant.ExpiresUtc && grant.IssuedUptimeMs <= uptime && uptime < grant.ExpiresUptimeMs && grant.Tool == request.Tool
                    && string.Equals(grant.Program, request.Program, StringComparison.OrdinalIgnoreCase)) return "automatic";
            }
        return legacyPermission;
    }

    public static string ForRequest(AppPaths paths, string project, SessionOwnerIdentity owner, WindowsTestRequest request, PermissionClock clock)
    {
        var settings = new SettingsStore(paths);
        var stamp = clock.Read();
        // Never mark a warning persisted before its write succeeds. Failure
        // refuses this request; subsequent reads retry rather than reviving it.
        if (!stamp.Valid) DisableTemporary(paths, owner, "The host clock moved backward. Temporary permissions are disabled until this VM is reopened.");
        return TestPermission(settings.PermissionPolicyFor(project), settings.WindowsTestPermissionFor(project), project, owner, request,
            Applied(paths, project, owner), Exceptions(paths, project, owner), stamp.Utc, Alive(owner), stamp.UptimeMs, stamp.Valid);
    }

    private static void ValidateGrant(TemporaryTestPermission? grant, string project, SessionOwnerIdentity owner)
    {
        if (grant is null || grant.Version != 1 || !Guid.TryParseExact(grant.Id, "N", out _) || !SameProject(grant.ProjectPath, project)
            || grant.Generation != owner.Generation || grant.OwnerPid != owner.OwnerPid || grant.OwnerStartTicks != owner.OwnerStartTicks
            || grant.IssuedUtc > DateTimeOffset.MaxValue.AddMinutes(-30) || grant.ExpiresUtc <= grant.IssuedUtc || grant.ExpiresUtc > grant.IssuedUtc.AddMinutes(30)
            || grant.IssuedUptimeMs < 0 || grant.ExpiresUptimeMs <= grant.IssuedUptimeMs || grant.ExpiresUptimeMs - grant.IssuedUptimeMs > 1800000
            || grant.Tool is not ("project" or "dotnet" or "node" or "python" or "powershell")
            || grant.Tool == "project" && (!WindowsTestProtocol.SafePath(grant.Program) || !grant.Program!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            || grant.Tool != "project" && grant.Program is not null)
            throw new InvalidDataException("Temporary permission scope or expiry is invalid.");
    }

    public static void End(AppPaths paths, SessionOwnerIdentity owner, DateTimeOffset? now = null)
    {
        if (owner.ProjectPath is null) return;
        using var lease = MutationLease(paths, owner.ProjectPath);
        var applied = Applied(paths, owner.ProjectPath, owner);
        if (applied is null) return;
        var stamp = now ?? DateTimeOffset.UtcNow;
        ReturnRecovery.SaveAtomic(FilePath(paths, owner.ProjectPath, owner.Generation, "-permissions.json"), applied with { EndedUtc = stamp });
        var grants = Exceptions(paths, owner.ProjectPath, owner);
        if (grants is not null) ReturnRecovery.SaveAtomic(FilePath(paths, owner.ProjectPath, owner.Generation, "-exceptions.json"), grants with { EndedUtc = stamp });
    }

    public static void DisableTemporary(AppPaths paths, SessionOwnerIdentity owner, string reason)
    {
        if (owner.ProjectPath is null || reason.Length > 512) throw new InvalidDataException("Invalid temporary-permission diagnostic.");
        using var lease = MutationLease(paths, owner.ProjectPath);
        var applied = Applied(paths, owner.ProjectPath, owner);
        if (applied is not null && applied.TemporaryPermissionsDisabledReason is null)
            ReturnRecovery.SaveAtomic(FilePath(paths, owner.ProjectPath, owner.Generation, "-permissions.json"), applied with { TemporaryPermissionsDisabledReason = reason });
    }

    private static FileStream MutationLease(AppPaths paths, string project)
    {
        var file = WindowsTestControls.Safe(WindowsTestControls.ProjectRoot(paths, project), "permissions.lock");
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    private static bool SameProject(string? left, string right)
    {
        try { return left is not null && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("Invalid saved permission project path.", error); }
    }

    private static void PrepareRoot(string root)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateDirectory(root); return; }
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.Name.EndsWith("\\" + TestUserRunner.UserName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A separate host owner is required for permissions.");
        Directory.CreateDirectory(root);
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
    }
}
