using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed record UpgradedSession(string Directory, string Disk, string Original, string OriginalSha256, string RuntimeVersion);

public static class SessionUpgrade
{
    public static async Task<UpgradedSession> CreateAsync(PublicRuntime runtime, MaintenanceKit kit, string originalDisk,
        string managedSessionsRoot, CancellationToken token, IProgress<string>? progress = null,
        IReadOnlyCollection<string>? trustedBackingFiles = null)
    {
        var original = Path.GetFullPath(originalDisk);
        var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedSessionsRoot));
        if (!original.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(original) != "session.qcow2" || !FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(original)!, "session.qcow2", out _))
            throw new InvalidDataException("The saved VM is outside its managed session location or contains a link.");
        // Retain a read handle that forbids writes/renames to the original while
        // QEMU opens it only as this new child's backing image.
        using var originalRead = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        var before = Convert.ToHexString(await SHA256.HashDataAsync(originalRead, token).ConfigureAwait(false));
        var relative = Path.GetRelativePath(allowed, original);
        var family = relative.Contains(Path.DirectorySeparatorChar)
            ? Path.Combine(allowed, relative.Split(Path.DirectorySeparatorChar)[0]) : allowed;
        var backingFiles = RestrictedRuntimeAccess.BackingChain(original, family, Path.GetDirectoryName(runtime.KeptImage)!, trustedBackingFiles);
        var directory = Path.Combine(Path.GetDirectoryName(original)!, "upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var disk = Path.Combine(directory, "session.qcow2");
        var nonce = Guid.NewGuid().ToString("N");
        var output = new StringBuilder();
        var verified = false;
        Process? machine = null;
        CancellationTokenSource? stopCapture = null;
        Task? capture = null;
        TcpClient? console = null;
        TcpClient? payloadClient = null;
        var qmp = 0;
        var elapsed = Stopwatch.StartNew();
        var transfers = new List<object>();
        try
        {
            progress?.Report("Preparing a preserved child of the saved VM…");
            RestrictedRuntimeAccess.ModifyDirectory(directory);
            foreach (var backingFile in backingFiles) RestrictedRuntimeAccess.ReadFile(backingFile);
            ReturnRecovery.SaveAtomic(Path.Combine(directory, "backing-access.json"), new { original, family, backingFiles,
                scope = "Read/execute ACLs only on the validated saved-disk dependency set; no content write or rebase." });
            foreach (var file in new[] { kit.Kernel, kit.Initrd, kit.Payload })
                RestrictedRuntimeAccess.ReadFile(file);
            // Only the new empty child gets this address. An absolute backing
            // address prevents Windows QEMU accumulating relative upgrade paths.
            await RunAsync(runtime.ImgExe, ["create", "-f", "qcow2", "-F", "qcow2", "-b", original, disk], token).ConfigureAwait(false);
            var ports = AvailablePorts();
            qmp = ports;
            var launch = new[] { directory, runtime.QemuDirectory, Path.GetDirectoryName(kit.Payload)! }
                .Select(workingDirectory => new { workingDirectory,
                    args = QemuCommand.BuildMaintenance(disk, kit.Kernel, kit.Initrd, ports, ports + 1, runtime.FirmwareDir, workingDirectory, ports + 2) })
                .OrderBy(candidate => TestUserRunner.CommandLineLength(runtime.QemuExe, candidate.args) <= 1024 ? 0 : 1)
                .ThenBy(candidate => candidate.workingDirectory == directory ? 0 : 1)
                .ThenBy(candidate => TestUserRunner.CommandLineLength(runtime.QemuExe, candidate.args)).First();
            var commandLength = TestUserRunner.CommandLineLength(runtime.QemuExe, launch.args);
            ReturnRecovery.SaveAtomic(Path.Combine(directory, "maintenance-launch.json"), new { runtime.QemuExe,
                launch.workingDirectory, launch.args, commandLength });
            if (commandLength > 1024) throw new IOException("The maintenance launch paths exceed Windows' command length limit. The original saved VM was preserved.");
            if (!TestUserRunner.TryStart(runtime.QemuExe, launch.workingDirectory, launch.args, out machine) || machine is null)
                throw new IOException("The maintenance VM did not start as its launch account: " + TestUserRunner.LastStartError);
            console = await ConnectWhileRunning(ports + 1);
            payloadClient = await ConnectWhileRunning(ports + 2);
            stopCapture = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stream = console.GetStream();
            capture = Task.Run(async () =>
            {
                var bytes = new byte[4096];
                while (true)
                {
                    var count = await stream.ReadAsync(bytes, stopCapture.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    lock (output) output.Append(Encoding.UTF8.GetString(bytes, 0, count));
                }
            }, CancellationToken.None);
            await WaitAsync(text => text.Contains("can't access tty", StringComparison.Ordinal) || text.Contains("\n# ", StringComparison.Ordinal), output, machine, token).ConfigureAwait(false);
            await SendAsync("mountpoint -q /proc || mount -t proc proc /proc\nmountpoint -q /sys || mount -t sysfs sysfs /sys\nmount -t tmpfs tmpfs /run\nstty -echo -F /dev/ttyS0\nmkdir -p /run/launchpad-maintenance\ncd /run/launchpad-maintenance\nmodprobe virtio_console\nmaintenance_port=\nfor node in /sys/class/virtio-ports/*; do [ \"$(cat \"$node/name\" 2>/dev/null)\" = launchpad-maintenance ] && maintenance_port=/dev/${node##*/}; done\n[ -n \"$maintenance_port\" ] && [ -c \"$maintenance_port\" ] && exec 3<\"$maintenance_port\" && printf '\\nLP-MAINT-READY-" + nonce + "\\n'\n");
            await Marker("LP-MAINT-READY-" + nonce);
            progress?.Report("Updating only the saved VM's new child…");
            // The serial console carries small control commands only. Raw
            // payload bytes use an offline virtio port, never a shell heredoc.
            using (var payload = new FileStream(kit.Payload, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(payload, token).ConfigureAwait(false));
                if (!hash.Equals(kit.Manifest.Payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The offline maintenance payload changed after verification.");
                payload.Position = 0;
                await Transfer(payload, "upgrade.tar.gz", hash);
            }
            var baseline = SentManifest.Load(Path.Combine(Path.GetDirectoryName(original)!, "sent.manifest"));
            using (var baselineStream = new MemoryStream(baseline.GuestBaseline(), writable: false))
                await Transfer(baselineStream, "baseline.json", Convert.ToHexString(SHA256.HashData(baselineStream.ToArray())));
            progress?.Report("Installing the update…");
            await SendAsync("printf '%s  upgrade.tar.gz\\n' '" + kit.Manifest.Payload.Sha256 + "' | sha256sum -c - && tar -xzf upgrade.tar.gz && sh apply.sh && printf '\\nLP-MAINT-APPLIED-" + nonce + "\\n'\n");
            await Marker("LP-MAINT-APPLIED-" + nonce);
            lock (output)
                if (!output.ToString().Contains(kit.Manifest.GuestScriptSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The installed guest script did not report its expected identity.");
            await SendAsync("sync\nmount -o remount,ro / && printf '\\nLP-MAINT-SYNCED-" + nonce + "\\n'\n/sbin/poweroff -f\n");
            await Marker("LP-MAINT-SYNCED-" + nonce);
            var wait = Stopwatch.StartNew();
            while (!machine.WaitForExit(0) && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100, token).ConfigureAwait(false);
            if (!machine.WaitForExit(0)) throw new IOException("The maintenance guest did not shut down. Its child was preserved without activation.");
            baseline.Save(Path.Combine(directory, "sent.manifest"));
            if (ProjectSessionStore.HasUnconfirmedImport(Path.GetDirectoryName(original)!))
                ReturnRecovery.SaveAtomic(Path.Combine(directory, ProjectSessionStore.UnconfirmedImport),
                    new { original, message = "An earlier host import was not confirmed. Saved guest files remain available for review; automatic copy-back stays protected." });
            originalRead.Position = 0;
            if (Convert.ToHexString(await SHA256.HashDataAsync(originalRead, token).ConfigureAwait(false)) != before)
                throw new IOException("The original saved VM changed unexpectedly. The upgrade was not activated.");
            verified = true;
            return new(directory, disk, original, before, kit.Manifest.Version);

            Task Marker(string marker) => WaitAsync(text => text.Split('\n').Any(line => line.TrimEnd('\r') == marker), output, machine, token);
            async Task<TcpClient> ConnectWhileRunning(int port)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(90));
                var connecting = FenceHost.ConnectAsync(port, limit.Token);
                while (!connecting.IsCompleted)
                {
                    if (machine.WaitForExit(0))
                    {
                        limit.Cancel();
                        try { using var lateClient = await connecting.ConfigureAwait(false); }
                        catch (Exception error) when (error is OperationCanceledException or SocketException or IOException) { }
                        token.ThrowIfCancellationRequested();
                        throw new IOException("The maintenance VM exited before opening its offline channel. Its child was preserved.");
                    }
                    await Task.Delay(100, limit.Token).ConfigureAwait(false);
                }
                return await connecting.ConfigureAwait(false);
            }
            async Task SendAsync(string text)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(90));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text), limit.Token).ConfigureAwait(false);
            }
            async Task Transfer(Stream source, string filename, string hash)
            {
                var bytes = source.Length;
                var sequence = transfers.Count + 1;
                var marker = "LP-MAINT-TRANSFER-" + nonce + "-" + sequence;
                var started = elapsed.Elapsed.TotalSeconds;
                await SendAsync("dd of=" + filename + " bs=65536 count=" + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " iflag=fullblock,count_bytes status=none <&3 && [ \"$(wc -c < " + filename + ")\" -eq " + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " ] && printf '%s  " + filename + "\\n' '" + hash + "' | sha256sum -c - && printf '\\n" + marker + "\\n'\n");
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(90));
                await source.CopyToAsync(payloadClient.GetStream(), 65536, limit.Token).ConfigureAwait(false);
                var sent = elapsed.Elapsed.TotalSeconds;
                await Marker(marker);
                transfers.Add(new { filename, bytes, sha256 = hash.ToLowerInvariant(), transport = "offline-virtio",
                    startedSeconds = started, sentSeconds = sent, verifiedSeconds = elapsed.Elapsed.TotalSeconds });
            }
        }
        finally
        {
            if (machine is not null)
            {
                if (!machine.WaitForExit(0))
                {
                    // A failed maintenance child is recovery data. No source
                    // disk or template is deleted, replaced or rebased.
                    if (qmp > 0) ConsoleSizeLink.Quit(qmp);
                    if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true);
                }
                TestUserRunner.ReleaseMachine(machine.Id);
                machine.Dispose();
            }
            stopCapture?.Cancel();
            console?.Dispose();
            payloadClient?.Dispose();
            if (capture is not null)
                try { await capture.ConfigureAwait(false); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            stopCapture?.Dispose();
            string log; lock (output) log = output.ToString();
            await File.WriteAllTextAsync(Path.Combine(directory, "maintenance-private.log"), log, CancellationToken.None).ConfigureAwait(false);
            ReturnRecovery.SaveAtomic(Path.Combine(directory, "upgrade-result.json"), new { verified, original, originalSha256 = before,
                kit.Manifest.Version, recordedUtc = DateTime.UtcNow, elapsedSeconds = elapsed.Elapsed.TotalSeconds, transfers });
        }
    }

    private static int AvailablePorts()
    {
        for (var port = PortChoice.First; port <= PortChoice.Last; port += 4)
        {
            var listeners = new List<TcpListener>();
            try { for (var index = 0; index < 3; index++) { var listener = new TcpListener(IPAddress.Loopback, port + index); listeners.Add(listener); listener.Start(); } return port; }
            catch (SocketException) { }
            finally { foreach (var listener in listeners) listener.Stop(); }
        }
        throw new IOException("No maintenance ports are available.");
    }

    private static async Task WaitAsync(Func<string, bool> ready, StringBuilder output, Process machine, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            string text; lock (output) text = output.ToString();
            if (ready(text)) return;
            if (machine.WaitForExit(0)) throw new IOException("The maintenance VM exited before confirming the upgrade.");
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new IOException("The maintenance step timed out. The original disk and its new child were preserved.");
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The maintenance preparation tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new IOException("Maintenance preparation failed: " + await error.ConfigureAwait(false) + await output.ConfigureAwait(false));
    }
}
