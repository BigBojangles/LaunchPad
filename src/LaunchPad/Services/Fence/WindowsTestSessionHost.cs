using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LaunchPad.Services.Fence;

// Guardian-owned outbound connection. No host execution listener, credentials
// in the guest, MainWindow ownership, or writable session-directory ledger.
public static class WindowsTestSessionHost
{
    public static async Task RunAsync(string sessionDirectory, SessionOwnerIdentity identity, Process machine, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || identity.ProjectPath is null) return;
        string? root = null;
        string? latestError = null;
        try
        {
            using var owner = Process.GetCurrentProcess();
            if (!Guid.TryParseExact(identity.Generation, "N", out _) || identity.QmpPort < PortChoice.First || identity.QmpPort > PortChoice.Last
                || identity.OwnerPid != owner.Id || identity.OwnerStartTicks != owner.StartTime.ToUniversalTime().Ticks
                || identity.MachinePid != machine.Id || identity.MachineStartTicks != machine.StartTime.ToUniversalTime().Ticks
                || machine.HasExited || !machine.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Windows test session ownership changed.");
            var paths = new AppPaths();
            root = ManagedRoot(paths, identity.ProjectPath, sessionDirectory);
            PrepareRoot(root);
            using var lease = new FileStream(Path.Combine(root, identity.Generation + ".owner.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var permissionClock = new PermissionClock();
            var bridge = new WindowsTestBridge(root, identity.Generation, new ControlledWindowsTestExecutor(root,
                () => new SettingsStore(paths).WindowsTestPermissionFor(identity.ProjectPath), requestPermission: request =>
                    PermissionPolicies.ForRequest(paths, identity.ProjectPath, identity, request, permissionClock)));
            var everReady = false;
            var failures = 0;
            while (!token.IsCancellationRequested && !machine.HasExited)
            {
                try
                {
                    using var client = new TcpClient();
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(3));
                        await client.ConnectAsync("127.0.0.1", QemuCommand.WindowsTestPort(identity.QmpPort), connect.Token).ConfigureAwait(false);
                    }
                    WindowsQemuPeer.Require(client, machine.Id);
                    if (machine.HasExited || machine.StartTime.ToUniversalTime().Ticks != identity.MachineStartTicks)
                        throw new InvalidDataException("Windows test VM identity changed.");
                    using var auth = new WindowsTestChannel(identity.Generation);
                    Save("connected");
                    var result = await auth.ServeAsync(client.GetStream(), bridge, token, () => { everReady = true; Save("ready"); }).ConfigureAwait(false);
                    everReady = true;
                    failures = 0;
                    Save("result", result.RequestId, result.Outcome);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException
                    or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
                {
                    if (token.IsCancellationRequested) break;
                    Save("unavailable", error: error.Message);
                    // No endless retry against an old guest without the broker.
                    // Once a request has succeeded, later transport loss may
                    // reconnect and return its durable result without rerunning.
                    if (!everReady || ++failures >= 3) return;
                }
                await Task.Delay(failures == 0 ? 250 : 1000, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
            or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            Save("unavailable", error: error.Message);
        }
        finally { Save("stopped"); }

        void Save(string state, string? requestId = null, string? outcome = null, string? error = null)
        {
            if (root is null) return;
            if (error is not null) latestError = error;
            try { ReturnRecovery.SaveAtomic(Path.Combine(root, identity.Generation + ".status.json"),
                new { generation = identity.Generation, state, requestId, outcome, error = latestError, recordedUtc = DateTime.UtcNow }); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string ManagedRoot(AppPaths paths, string project, string sessionDirectory)
    {
        var root = WindowsTestControls.ProjectRoot(paths, project);
        var session = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
        if (root.Equals(session, StringComparison.OrdinalIgnoreCase) || root.StartsWith(session + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || session.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !FenceFiles.TryResolveUnlinked(root, "ledger-probe", out _))
            throw new InvalidDataException("Windows test host storage must be outside the writable VM session.");
        return root;
    }

    private static void PrepareRoot(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.Name.EndsWith("\\" + TestUserRunner.UserName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A separate host owner is required for Windows test results.");
        Directory.CreateDirectory(root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
    }
}
