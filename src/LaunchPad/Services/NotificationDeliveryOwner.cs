using System.Diagnostics;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public static class NotificationDeliveryOwner
{
    public const string Argument = "--notification-owner";
    public static bool IsRequest(string[] args) => args.Length == 2 && args[0] == Argument && Path.IsPathFullyQualified(args[1]);

    public static void StartIfEnabled(AppPaths paths)
    {
        var settings = new SettingsStore(paths);
        if (!settings.Current.NotificationsEnabled || !Guid.TryParseExact(settings.Current.NotificationDestination, "N", out _)) return;
        var root = Path.Combine(paths.AppDataDir, "notifications");
        Directory.CreateDirectory(root);
        using var startLease = AcquireStartLease(root);
        settings = new SettingsStore(paths);
        if (!settings.Current.NotificationsEnabled || !Guid.TryParseExact(settings.Current.NotificationDestination, "N", out _)) return;
        if (!FenceFiles.TryResolveUnlinked(root, "delivery.owner", out var owner)) throw new IOException("Notification delivery path is unavailable.");
        try { using var probe = new FileStream(owner, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; } // A host delivery owner holds this lease.
        var start = new ProcessStartInfo(paths.ExePath) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(paths.AppDataDir);
        using var process = Process.Start(start) ?? throw new IOException("Notification delivery could not start.");
    }

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsRequest(args)) return 1;
        var paths = new AppPaths(appDataDir: args[1]);
        var service = new NotificationService(paths);
        var root = Path.Combine(paths.AppDataDir, "notifications");
        try
        {
            Directory.CreateDirectory(root);
            if (!FenceFiles.TryResolveUnlinked(root, "delivery.owner", out var owner)) return 1;
            using var lease = new FileStream(owner, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var transport = NotificationTransport.Create(service.Destinations);
            RunLoop().GetAwaiter().GetResult();
            return 0;
            async Task RunLoop()
            {
                while (true)
                {
                    try
                    {
                        await TickAsync(paths, service.Outbox, transport).ConfigureAwait(false);
                        var current = new SettingsStore(paths);
                        if (!current.Current.NotificationsEnabled && !service.Outbox.Read().Any(item => item.State == NotificationState.Pending)
                            && TryStop(paths, lease)) return;
                    }
                    catch (Exception)
                    {
                        // Fail closed. Never let an optional alert failure affect VM/agent lifetime.
                        WriteWarning(root);
                        try { if (TryStop(paths, lease)) return; } catch { }
                    }
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }
        catch (IOException) { return 0; } // Concurrent owner already has the lease; it reloads the queue.
        catch { WriteWarning(root); return 1; }
    }

    public static async Task<int> TickAsync(AppPaths paths, NotificationOutbox outbox, INotificationTransport transport, CancellationToken cancellation = default)
    {
        outbox.RecoverAbandonedSends();
        var processed = 0;
        while (processed < 32)
        {
            var before = outbox.Read().Count(item => item.State == NotificationState.Pending);
            if (before == 0) break;
            var sent = await outbox.DispatchOneAsync(transport,
                project => new SettingsStore(paths).NotificationConsentFor(project),
                project => new SettingsStore(paths).DisplayNameFor(project), cancellation).ConfigureAwait(false);
            if (!sent && outbox.Read().Count(item => item.State == NotificationState.Pending) >= before) break;
            processed++;
        }
        return processed;
    }

    internal static bool TryStop(AppPaths paths, FileStream ownedLease)
    {
        // Start and final release share a gate. Re-enable either keeps this owner
        // alive or sees the released owner lease and starts a replacement.
        using var startLease = AcquireStartLease(Path.Combine(paths.AppDataDir, "notifications"));
        if (new SettingsStore(paths).Current.NotificationsEnabled) return false;
        ownedLease.Dispose();
        return true;
    }

    private static FileStream AcquireStartLease(string root)
    {
        if (!FenceFiles.TryResolveUnlinked(root, "delivery-start.guard", out var path)) throw new IOException("Notification delivery path is unavailable.");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(20); }
        }
    }

    private static void WriteWarning(string root)
    {
        try
        {
            if (FenceFiles.TryResolveUnlinked(root, "delivery-warning.json", out var path))
                ReturnRecovery.SaveAtomic(path, new { observedUtc = DateTimeOffset.UtcNow,
                    message = "Notification delivery needs attention. Saved history was preserved; no unconfirmed message was retried." });
        }
        catch { }
    }
    public static void ReportWarning(AppPaths paths) => WriteWarning(Path.Combine(paths.AppDataDir, "notifications"));
}
