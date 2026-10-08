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
        if (!ShouldStart(paths, serviceOutbox: new NotificationService(paths).Outbox)) return;
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
        if (!Configured(new SettingsStore(paths))) return 0;
        var service = new NotificationService(paths);
        var root = Path.Combine(paths.AppDataDir, "notifications");
        try
        {
            Directory.CreateDirectory(root);
            if (!FenceFiles.TryResolveUnlinked(root, "delivery.owner", out var owner)) return 1;
            using var lease = new FileStream(owner, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!Configured(new SettingsStore(paths))) return 0;
            using var transport = NotificationTransport.Create(service.Destinations);
            RunLoopAsync(paths, service.Outbox, transport, lease).GetAwaiter().GetResult();
            return 0;
        }
        catch (IOException) { return 0; } // Concurrent owner already has the lease; it reloads the queue.
        catch { WriteWarning(root); return 1; }
    }

    private static bool Configured(SettingsStore settings) => settings.Current.NotificationsEnabled
        && Guid.TryParseExact(settings.Current.NotificationDestination, "N", out _);

    internal static bool ShouldStart(AppPaths paths, NotificationOutbox serviceOutbox, Func<bool>? hasTerminals = null)
        => Configured(new SettingsStore(paths)) && ((hasTerminals ?? (() => NotificationTerminalPresence.HasOpenTerminal(paths)))()
            || serviceOutbox.Read().Any(item => item.State == NotificationState.Pending));

    internal static async Task RunLoopAsync(AppPaths paths, NotificationOutbox outbox, INotificationTransport transport,
        FileStream lease, Func<bool>? hasTerminals = null, TimeSpan? batchLimit = null, CancellationToken cancellation = default)
    {
        hasTerminals ??= () => NotificationTerminalPresence.HasOpenTerminal(paths);
        var limit = batchLimit ?? TimeSpan.FromSeconds(30);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(batchLimit));
        Stopwatch? finalDrain = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (hasTerminals()) finalDrain = null;
                else finalDrain ??= Stopwatch.StartNew();
                var pendingBeforeBatch = outbox.Read().Where(item => item.State == NotificationState.Pending)
                    .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var remaining = finalDrain is null ? limit : limit - finalDrain.Elapsed;
                if (Configured(new SettingsStore(paths)) && remaining > TimeSpan.Zero)
                {
                    // Even after the last console exits, finish one bounded
                    // batch of already accepted alerts, then release the owner.
                    using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    batch.CancelAfter(remaining);
                    var optOut = WatchOptOutAsync();
                    try { await TickAsync(paths, outbox, transport, batch.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    finally
                    {
                        batch.Cancel();
                        try { await optOut.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    }
                    async Task WatchOptOutAsync()
                    {
                        try
                        {
                            while (!batch.IsCancellationRequested)
                            {
                                if (!Configured(new SettingsStore(paths))) { batch.Cancel(); return; }
                                await Task.Delay(250, batch.Token).ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch { batch.Cancel(); } // Unreadable consent cannot permit more sends.
                    }
                }
                if (TryStop(paths, lease, hasTerminals, pendingBeforeBatch,
                    finalLimitExpired: finalDrain is not null && finalDrain.Elapsed >= limit)) return;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                WriteWarning(Path.Combine(paths.AppDataDir, "notifications"));
                // A pager failure cannot keep an idle process alive indefinitely.
                try { if (TryStop(paths, lease, hasTerminals)) return; }
                catch { return; }
            }
            await Task.Delay(1000, cancellation).ConfigureAwait(false);
        }
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

    internal static bool TryStop(AppPaths paths, FileStream ownedLease, Func<bool>? hasTerminals = null,
        IReadOnlySet<string>? pendingBeforeBatch = null, bool finalLimitExpired = false)
    {
        // Start and final release share a gate. Re-enable either keeps this owner
        // alive or sees the released owner lease and starts a replacement.
        using var startLease = AcquireStartLease(Path.Combine(paths.AppDataDir, "notifications"));
        if (Configured(new SettingsStore(paths)))
        {
            if ((hasTerminals ?? (() => NotificationTerminalPresence.HasOpenTerminal(paths)))()) return false;
            // A producer queues before taking this same start gate. If its
            // startup found our live lease, notice its newly queued item before
            // releasing ownership. Old timeout leftovers do not keep us alive.
            if (!finalLimitExpired && pendingBeforeBatch is not null && new NotificationService(paths).Outbox.Read()
                .Any(item => item.State == NotificationState.Pending && !pendingBeforeBatch.Contains(item.Id))) return false;
        }
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
