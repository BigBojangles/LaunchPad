using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestMaintenanceTests
{
    [EnvironmentFact("LAUNCHPAD_MAINTENANCE_PAUSED_DIAGNOSTIC")]
    [Trait("Category", "Integration")]
    public async Task PausedOwnedMaintenanceLaunchCapturesActualLaunchIdentityAndError()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_PAUSED_DIAGNOSTIC")!);
        Assert.StartsWith(Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest") + Path.DirectorySeparatorChar,
            root, StringComparison.OrdinalIgnoreCase);
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "maintenance-launch.json")));
        var executable = record.RootElement.GetProperty("qemuExe").GetString()!;
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        Assert.Equal(runtime.QemuExe, executable);
        var arguments = record.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()!
            .Replace("20000", "29990").Replace("20001", "29991").Replace("20002", "29992")).ToArray();
        Assert.DoesNotContain(arguments, value => value.Contains('&') || value.Contains('|') || value.Contains('"') || value.Contains('%'));
        var original = Path.Combine(Path.GetDirectoryName(root)!, "session.qcow2");
        var before = GuestBaselineTests.HashFile(original);
        using var originalRead = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
        var command = "@echo off\r\nwhoami > native-identity-private.txt\r\n\"" + executable + "\" "
            + string.Join(" ", arguments.Select(value => value.Contains(' ') ? "\"" + value + "\"" : value))
            + " -S 2> native-paused-error-private.txt\r\n";
        await File.WriteAllTextAsync(Path.Combine(root, "owned-paused-probe.cmd"), command, Encoding.ASCII);
        Assert.True(TestUserRunner.TryStart(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), root,
            new[] { "/d", "/c", "owned-paused-probe.cmd" }, out var started), "Owned standard-user diagnostic could not start: " + TestUserRunner.LastStartError);
        using var worker = started!;
        var asynchronousExit = worker.WaitForExitAsync();
        await Task.WhenAny(asynchronousExit, Task.Delay(200));
        var asynchronousExitStatus = asynchronousExit.Status.ToString();
        var asynchronousExitError = asynchronousExit.Exception?.GetBaseException().ToString();
        var nativeExitedBeforeCleanup = worker.WaitForExit(0);
        var exited = worker.WaitForExit(2500);
        if (!exited) { worker.Kill(entireProcessTree: true); Assert.True(worker.WaitForExit(5000)); }
        TestUserRunner.ReleaseMachine(worker.Id);
        var identity = await File.ReadAllTextAsync(Path.Combine(root, "native-identity-private.txt"));
        Assert.Contains(TestUserRunner.UserName, identity, StringComparison.OrdinalIgnoreCase);
        var error = await File.ReadAllTextAsync(Path.Combine(root, "native-paused-error-private.txt"));
        await File.WriteAllTextAsync(Path.Combine(root, "native-paused-diagnostic-private.json"), JsonSerializer.Serialize(new
        {
            identity, exitedBeforeCleanup = exited, error, before, asynchronousExitStatus, asynchronousExitError, nativeExitedBeforeCleanup,
            after = GuestBaselineTests.HashFile(original),
            limitations = "Diagnostic only: actual launch identity, protected owned original and paused CPUs; no guest instructions, maintenance apply or functional acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(before, GuestBaselineTests.HashFile(original));
    }

    [EnvironmentFact("LAUNCHPAD_MAINTENANCE_CANCEL", "1")]
    [Trait("Category", "Integration")]
    public async Task CancelAfterVerifiedPayloadPreservesOriginalAndUnverifiedChild()
    {
        var repository = GuestBaselineTests.RepositoryRoot();
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var kitRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_KIT_ROOT")!);
        Assert.StartsWith(Path.Combine(repository, "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
            kitRoot, StringComparison.OrdinalIgnoreCase);
        var kit = MaintenanceKit.Read(kitRoot);
        var original = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_SOURCE")!);
        var reports = Path.Combine(repository, "tests", "LaunchPad.Tests", "TestResults", "guest");
        Assert.StartsWith(reports + Path.DirectorySeparatorChar, original, StringComparison.OrdinalIgnoreCase);
        var parent = Path.GetDirectoryName(original)!;
        var previous = Directory.GetDirectories(parent, "upgrade-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = GuestBaselineTests.HashFile(original);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var atVerifiedPayload = false;
        var progress = new InlineProgress(message =>
        {
            if (message != "Installing the update…") return;
            atVerifiedPayload = true;
            cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionUpgrade.CreateAsync(runtime, kit, original, reports, cancel.Token, progress));
        Assert.True(atVerifiedPayload, "Cancellation must follow actual guest byte-count/hash acknowledgements.");
        Assert.Equal(before, GuestBaselineTests.HashFile(original));
        var child = Assert.Single(Directory.GetDirectories(parent, "upgrade-*"), path => !previous.Contains(path));
        Assert.True(File.Exists(Path.Combine(child, "session.qcow2")));
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(child, "upgrade-result.json")));
        Assert.False(record.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(original, record.RootElement.GetProperty("original").GetString());
        Assert.Equal(before, record.RootElement.GetProperty("originalSha256").GetString(), ignoreCase: true);
        Assert.Equal(2, record.RootElement.GetProperty("transfers").GetArrayLength());
        Assert.All(record.RootElement.GetProperty("transfers").EnumerateArray(), transfer =>
            Assert.Equal("offline-virtio", transfer.GetProperty("transport").GetString()));
        Assert.DoesNotContain("MAINTENANCE-APPLY-OK", await File.ReadAllTextAsync(Path.Combine(child, "maintenance-private.log")));
        await File.WriteAllTextAsync(Path.Combine(child, "cancel-preservation-private.json"), JsonSerializer.Serialize(new
        {
            passed = true, original, before, after = GuestBaselineTests.HashFile(original), child,
            cancelledAfterVerifiedPayload = atVerifiedPayload,
            limitations = "Owned new-child maintenance only. Actual payload and baseline acknowledgements precede cancellation; no package apply, normal reopen, real auth/project or host return apply."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    [EnvironmentFact("LAUNCHPAD_MAINTENANCE_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task MaintenanceUpgradesOnlyAChildThenNormalGuestKeepsOriginalProjectData()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var kitRoot = Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_KIT_ROOT") ?? QemuLayout.Root;
        if (kitRoot != QemuLayout.Root) Assert.StartsWith(Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
            Path.GetFullPath(kitRoot), StringComparison.OrdinalIgnoreCase);
        var kit = MaintenanceKit.Read(kitRoot);
        var original = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_SOURCE")
            ?? throw new InvalidOperationException("Explicit owned diagnostic session required."));
        var reports = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest");
        Assert.StartsWith(reports + Path.DirectorySeparatorChar, original, StringComparison.OrdinalIgnoreCase);
        var before = GuestBaselineTests.HashFile(original);
        var securityPackages = Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_SECURITY_PACKAGES") == "1";
        var npmPatches = Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_NPM_PATCHES") == "1";
        var expectedNpm = Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_EXPECTED_NPM") ?? "11.20.0";
        UpgradedSession result;
        if (Environment.GetEnvironmentVariable("LAUNCHPAD_MAINTENANCE_REOPEN_CHILD") is { Length: > 0 } existing)
        {
            existing = Path.GetFullPath(existing);
            Assert.Equal(Path.GetDirectoryName(original), Path.GetDirectoryName(existing));
            Assert.StartsWith("upgrade-", Path.GetFileName(existing));
            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(existing, "upgrade-result.json")));
            Assert.True(record.RootElement.GetProperty("verified").GetBoolean());
            Assert.Equal(original, record.RootElement.GetProperty("original").GetString());
            Assert.Equal(before, record.RootElement.GetProperty("originalSha256").GetString(), ignoreCase: true);
            Assert.Equal(kit.Manifest.Version, record.RootElement.GetProperty("version").GetString());
            result = new(existing, Path.Combine(existing, "session.qcow2"), original, before, kit.Manifest.Version);
        }
        else result = await SessionUpgrade.CreateAsync(runtime, kit, original, reports, CancellationToken.None,
            trustedBackingFiles: new[]
            {
                Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "resend-candidate-20261006-v2", "template.qcow2"),
                Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "runtime-procps-20261005", "template.qcow2")
            });
        Assert.NotEqual(original, result.Disk);
        Assert.Equal(before, result.OriginalSha256, ignoreCase: true);
        Assert.Equal(before, GuestBaselineTests.HashFile(original));
        var host = Path.Combine(result.Directory, "resume-host-fixture");
        Directory.CreateDirectory(Path.Combine(host, "nested"));
        await File.WriteAllTextAsync(Path.Combine(host, "nested", "value.txt"), "host-value-must-not-import");
        InitialImport.Begin(result.Directory);
        var port = GuestBaselineTests.AvailablePorts();
        var args = QemuCommand.Build("whpx", result.Disk, 0, port, "fence", null, serialLog: Path.Combine(result.Directory, "serial.log"),
            memoryMb: 4096, cores: 2, firmwareDir: runtime.FirmwareDir, workingDirectory: result.Directory);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, result.Directory, args, out var started),
            "Launch identity error: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        StatusLink? status = null;
        ConsoleSizeLink? sizing = null;
        Task? capture = null;
        var output = new StringBuilder();
        var complete = false;
        var shutdown = false;
        try
        {
            using var console = await Connect(QemuCommand.TuiPort(port), machine, stop.Token);
            sizing = await ConsoleSizeLink.ConnectAsync(port, stop.Token);
            Assert.NotNull(sizing);
            sizing.Send(80, 30);
            capture = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await console.GetStream().ReadAsync(buffer, stop.Token);
                    if (count == 0) break;
                    lock (output) output.Append(Encoding.UTF8.GetString(buffer, 0, count));
                }
            });
            using (var fence = await Connect(QemuCommand.FencePort(port), machine, stop.Token))
            {
                var program = Encoding.UTF8.GetBytes("""
                    #!/bin/sh
                    printf 'UPGRADE-UID:'; id -u
                    printf 'UPGRADE-CONTENT:'; cat nested/value.txt
                    test -w nested/value.txt && printf 'UPGRADE-WRITABLE:OK\n'
                    ps --version
                    if [ -f /home/builder/.config/launchpad-owned-maintenance/history.txt ]; then
                        printf 'UPGRADE-OWNED-HISTORY:'; cat /home/builder/.config/launchpad-owned-maintenance/history.txt
                        /home/builder/.local/bin/launchpad-owned-maintenance-tool
                    fi
                    for package in perl-base perl-modules-5.36 libperl5.36 perl; do
                        printf 'UPGRADE-PACKAGE:%s:' "$package"
                        dpkg-query -W -f='${Version}\n' "$package"
                    done
                    printf 'UPGRADE-NPM:'; npm --version
                    printf 'UPGRADE-NPM-BRACE:'; node -p 'require("/usr/local/lib/node_modules/npm/node_modules/brace-expansion/package.json").version'
                    printf 'UPGRADE-NPM-UNDICI:'; node -p 'require("/usr/local/lib/node_modules/npm/node_modules/undici/package.json").version'
                    printf 'UPGRADE-READY\n'
                    read answer
                    """ + "\n");
                await InitialImport.WaitForMarkerAsync(Path.Combine(result.Directory, "serial.log"), "IMPORT-READY safe-merge", stop.Token);
                var programPath = Path.Combine(host, "lp-upgrade-probe");
                await File.WriteAllBytesAsync(programPath, program, stop.Token);
                var manifest = await FenceHost.SendProjectAsync(fence.GetStream(), host, null, Path.Combine(result.Directory, "sent.manifest"),
                    stop.Token, new AgentLaunch(AgentChoice.Custom, "lp-upgrade-probe", programPath),
                    restoreGuestState: true, restoreHostHome: false, sendProjectFiles: false);
                // Commit only after the same readiness barrier as production.
                Assert.Equal(SentManifest.Load(Path.Combine(result.Directory, "sent.manifest")).ContentHash("nested/value.txt"), manifest.ContentHash("nested/value.txt"));
            }
            status = new StatusLink(await Connect(QemuCommand.StatusPort(port), machine, stop.Token));
            await InitialImport.WaitForMarkerAsync(Path.Combine(result.Directory, "serial.log"), "DOOR-READY safe-import safe-merge", stop.Token);
            InitialImport.Commit(result.Directory, SentManifest.Load(Path.Combine(result.Directory, "sent.manifest")));
            await WaitOutput("UPGRADE-READY", output, stop.Token);
            Assert.Contains("UPGRADE-UID:1000", Output(output));
            Assert.Contains("UPGRADE-CONTENT:second-value", Output(output));
            Assert.Contains("UPGRADE-WRITABLE:OK", Output(output));
            Assert.Contains("procps-ng", Output(output));
            if (securityPackages)
            {
                foreach (var package in new[] { "perl-base", "perl-modules-5.36", "libperl5.36", "perl" })
                    Assert.Contains("UPGRADE-PACKAGE:" + package + ":5.36.0-7+deb12u4", Output(output));
                Assert.Contains("UPGRADE-NPM:" + expectedNpm, Output(output));
                Assert.Contains("UPGRADE-OWNED-HISTORY:owned-history-survives-security-maintenance", Output(output));
                Assert.Contains("UPGRADE-OWNED-TOOL:retained", Output(output));
            }
            if (npmPatches)
            {
                Assert.Contains("UPGRADE-NPM-BRACE:5.0.12", Output(output));
                Assert.Contains("UPGRADE-NPM-UNDICI:6.28.1", Output(output));
            }
            Assert.Equal("host-value-must-not-import", await File.ReadAllTextAsync(Path.Combine(host, "nested", "value.txt"), stop.Token));
            using var returned = await Connect(QemuCommand.FencePort(port), machine, stop.Token);
            var recovery = ReturnRecovery.Create(result.Directory);
            var receiving = Task.Run(() => ProjectPull.Receive(returned.GetStream(), recovery.Payload, TimeSpan.FromSeconds(30)));
            await console.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\n"), stop.Token);
            var receipt = await receiving.WaitAsync(stop.Token);
            recovery.SaveTransfer("owned-upgrade-probe", receipt);
            Assert.True(receipt.Complete, receipt.Error);
            Assert.Empty(receipt.Files); // Unchanged guest data matches the seeded confirmed hashes.
            await GuestBaselineTests.WaitForSerialMarker(Path.Combine(result.Directory, "serial.log"), "RETURN-COMPLETE", stop.Token);
            complete = true;
        }
        finally
        {
            status?.Dispose(); sizing?.Dispose();
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown) { ConsoleSizeLink.Quit(port); if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true); }
            stop.Cancel();
            if (capture is not null) try { await capture; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(result.Directory, "maintenance-reopen-private.json"), JsonSerializer.Serialize(new
            {
                complete, shutdown, securityPackages, npmPatches, expectedNpm, result, output = Output(output), finalOriginalHash = GuestBaselineTests.HashFile(original),
                limitations = "Owned D06 session only, trusted offline root maintenance on a new child, then normal unprivileged custom-program reopen using the production no-host-files sender. No real project, credentials, host apply, GUI activation or complete confinement/durability acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown);
        Assert.Equal(before, GuestBaselineTests.HashFile(original));
    }
}
