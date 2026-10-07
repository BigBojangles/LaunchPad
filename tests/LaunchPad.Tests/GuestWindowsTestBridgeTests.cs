using System.Diagnostics;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestWindowsTestBridgeTests
{
    [EnvironmentFact("LAUNCHPAD_WINDOWS_TEST_GUEST", "1")]
    [Trait("Category", "Integration")]
    public async Task ConfinedGuestRequestsActualRestrictedWindowsCommandAndReplaysFrozenResults()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows live fixture only.");
        var repository = RepositoryRoot();
        var candidate = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_WINDOWS_TEST_CANDIDATE")
            ?? throw new InvalidOperationException("Explicit new private bridge candidate required."));
        Assert.StartsWith(Path.Combine(repository, "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
            candidate, StringComparison.OrdinalIgnoreCase);
        var expected = Environment.GetEnvironmentVariable("LAUNCHPAD_WINDOWS_TEST_CANDIDATE_SHA256");
        Assert.Matches("^[a-f0-9]{64}$", expected ?? "");
        Assert.Equal(expected, HashFile(candidate));
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var selectedBefore = HashFile(runtime.KeptImage);
        var root = Path.Combine(repository, "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        RestrictedRuntimeAccess.ModifyDirectory(root);
        foreach (var backing in RestrictedRuntimeAccess.BackingChain(candidate, Path.GetDirectoryName(candidate)!, Path.GetDirectoryName(runtime.KeptImage)!))
            RestrictedRuntimeAccess.ReadFile(backing);
        var project = Path.Combine(root, "fixture");
        Directory.CreateDirectory(project);
        var hostRoot = Path.Combine(repository, "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-guest-20261007",
            "host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostRoot);
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(hostRoot).SetAccessControl(security);
        }
        var canary = Path.Combine(hostRoot, "host-only-canary.txt");
        await File.WriteAllTextAsync(canary, "owned host-only canary");
        var fixtureId = Guid.NewGuid().ToString("N");
        var requestId = Guid.NewGuid().ToString("N");
        var generation = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(project, "probe.ps1"), """
            $ErrorActionPreference = 'Stop'
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            if (-not $identity.Name.EndsWith('\BuildLaunchTest', [StringComparison]::OrdinalIgnoreCase)) { throw 'Wrong launch account' }
            if ((New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated test identity' }
            $denied = $false
            try { [IO.File]::ReadAllText('__CANARY__') | Out-Null } catch [UnauthorizedAccessException] { $denied = $true }
            if (-not $denied) { throw 'Owned host-only canary was readable' }
            [IO.File]::AppendAllText('run-count.txt', "one execution`n")
            [IO.File]::WriteAllText('artifact.txt', '__FIXTURE__')
            Write-Output ('WINDOWS-IDENTITY:' + $identity.Name)
            Write-Output 'WINDOWS-OWNED-CANARY:DENIED'
            exit 0
            """.Replace("__CANARY__", canary.Replace("'", "''")).Replace("__FIXTURE__", fixtureId) + "\n");
        var program = Path.Combine(project, "lp-windows-bridge-probe");
        await File.WriteAllTextAsync(program, "#!/bin/sh\nexec /usr/bin/aa-exec -p launchpad-agent -- /usr/bin/setpriv --no-new-privs -- /bin/sh /home/builder/in/project/bridge-probe.sh\n");
        await File.WriteAllTextAsync(Path.Combine(project, "bridge-probe.sh"), """
            #!/bin/sh
            set -eu
            trap 'printf "BRIDGE-PROBE-FAILED\n"' EXIT
            printf 'BRIDGE-UID:'; id -u
            printf 'BRIDGE-PROFILE:'; cat /proc/self/attr/current
            python3 - <<'PY'
            import os, stat
            path='/dev/virtio-ports/launchpad-windows-test'
            info=os.stat(path)
            assert stat.S_ISCHR(info.st_mode) and info.st_uid == 0 and info.st_mode & 0o077 == 0
            try:
                fd=os.open(path, os.O_RDWR)
            except PermissionError:
                print('BRIDGE-RAW-DEVICE:DENIED', flush=True)
            else:
                os.close(fd)
                raise RuntimeError('Raw host bridge accessible to coding user')
            PY
            launchpad-windows-test --project . --request-id __REQUEST__ --tool powershell --timeout 30 --artifact artifact.txt --artifact run-count.txt --output /home/builder/in/project/first-result -- -NoProfile -NonInteractive -ExecutionPolicy Bypass -File probe.ps1
            printf 'BRIDGE-FIRST-ARTIFACT:'; cat first-result/artifact.txt; printf '\n'
            cat first-result/logs/stdout.txt
            launchpad-windows-test --resume /home/builder/.local/state/launchpad/windows-tests/__REQUEST__.request --output /home/builder/in/project/replayed-result
            cmp first-result/artifact.txt replayed-result/artifact.txt
            cmp first-result/run-count.txt replayed-result/run-count.txt
            printf 'BRIDGE-REPLAY:IDENTICAL\nBRIDGE-PROBE-DONE\n'
            exec /usr/bin/cat
            """.Replace("__REQUEST__", requestId) + "\n");
        var disk = Path.Combine(root, "session.qcow2");
        await RunTool(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", candidate, disk);
        var port = AvailableBridgePorts();
        var serial = Path.Combine(root, "serial.log");
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: serial,
            memoryMb: 4096, cores: 2, firmwareDir: runtime.FirmwareDir, workingDirectory: runtime.QemuDirectory);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, runtime.QemuDirectory, args, out var started), "Owned bridge VM: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var output = new StringBuilder();
        var executions = 0;
        var responses = new List<WindowsTestResponse>();
        var bridge = new WindowsTestBridge(hostRoot, generation, new ControlledWindowsTestExecutor(hostRoot, () => "automatic", (ready, authorize) =>
        {
            Interlocked.Increment(ref executions);
            return new ManagedWindowsTestExecutor(ready, authorize);
        }));
        Task? capture = null;
        Task? channel = null;
        StatusLink? status = null;
        ConsoleSizeLink? sizing = null;
        var complete = false;
        var shutdown = false;
        string? failure = null;
        try
        {
            using var console = await GuestResendTests.Connect(QemuCommand.TuiPort(port), machine, stop.Token);
            sizing = await ConsoleSizeLink.ConnectAsync(port, stop.Token);
            sizing?.Send(100, 30);
            capture = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await console.GetStream().ReadAsync(buffer, stop.Token);
                    if (count == 0) break;
                    lock (output)
                    {
                        if (output.Length + count > 4 * 1024 * 1024) throw new IOException("Owned bridge output exceeded limit.");
                        output.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    }
                }
            });
            channel = Task.Run(async () =>
            {
                for (var index = 0; index < 2; index++)
                {
                    using var client = await GuestResendTests.Connect(QemuCommand.WindowsTestPort(port), machine, stop.Token);
                    WindowsQemuPeer.Require(client, machine.Id);
                    using var authentication = new WindowsTestChannel(generation);
                    var response = await authentication.ServeAsync(client.GetStream(), bridge, stop.Token);
                    lock (responses) responses.Add(response);
                    if (response.Outcome != "finished" || response.ExitCode != 0)
                        throw new IOException("Actual Windows test failed: " + JsonSerializer.Serialize(response));
                }
            });
            using (var fence = await GuestResendTests.Connect(QemuCommand.FencePort(port), machine, stop.Token))
                await FenceHost.SendProjectAsync(fence.GetStream(), project, null, Path.Combine(root, "sent.manifest"), stop.Token,
                    new AgentLaunch(AgentChoice.Custom, "lp-windows-bridge-probe", program), restoreGuestState: true, restoreHostHome: false);
            status = new StatusLink(await GuestResendTests.Connect(QemuCommand.StatusPort(port), machine, stop.Token));
            while (!Output(output).Contains("BRIDGE-PROBE-DONE", StringComparison.Ordinal))
            {
                if (channel.IsFaulted) await channel;
                if (capture.IsFaulted) await capture;
                if (Output(output).Contains("BRIDGE-PROBE-FAILED", StringComparison.Ordinal))
                    throw new IOException("Owned guest bridge probe failed: " + Output(output));
                await Task.Delay(100, stop.Token);
            }
            await channel.WaitAsync(stop.Token);
            Assert.Equal(1, executions);
            Assert.Equal(2, responses.Count);
            Assert.All(responses, response => { Assert.Equal("finished", response.Outcome); Assert.Equal(0, response.ExitCode); });
            Assert.Equal(JsonSerializer.Serialize(responses[0]), JsonSerializer.Serialize(responses[1]));
            Assert.Contains("BRIDGE-UID:1000", Output(output));
            Assert.Contains("BRIDGE-PROFILE:launchpad-agent", Output(output));
            Assert.Contains("BRIDGE-RAW-DEVICE:DENIED", Output(output));
            Assert.Contains("WINDOWS-IDENTITY:", Output(output));
            Assert.Contains("BuildLaunchTest", Output(output), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WINDOWS-OWNED-CANARY:DENIED", Output(output));
            Assert.Contains("BRIDGE-FIRST-ARTIFACT:" + fixtureId, Output(output));
            Assert.False(File.Exists(Path.Combine(project, "artifact.txt")));
            Assert.Equal("owned host-only canary", await File.ReadAllTextAsync(canary));
            var state = WindowsTestControls.ReadState(Path.Combine(hostRoot, generation, requestId));
            Assert.NotNull(state);
            Assert.Equal("finished", state.Stage);
            Assert.Equal("finished", state.Outcome);
            complete = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            stop.Cancel();
            status?.Dispose(); sizing?.Dispose();
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown) { ConsoleSizeLink.Quit(port); if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true); }
            foreach (var task in new[] { channel, capture })
                if (task is not null) try { await task.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or TimeoutException or SocketException) { }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "windows-bridge-output-private.txt"), Output(output));
            await File.WriteAllTextAsync(Path.Combine(root, "windows-bridge-live-private.json"), JsonSerializer.Serialize(new
            {
                complete, shutdown, failure, root, hostRoot, candidate, candidateBefore = expected, candidateAfter = HashFile(candidate),
                selectedBefore, selectedAfter = HashFile(runtime.KeptImage), executions, responses,
                limitations = "Owned disposable VM and command fixture. Real guest broker/authenticated QEMU/controlled restricted executor/result replay; not GUI, independent guardian lifetime, bundled agent behavior, installed runtime, preserved-session maintenance or complete host security coverage. No real project or host apply."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown, "Ordinary guest shutdown not verified.");
        Assert.Equal(expected, HashFile(candidate));
        Assert.Equal(selectedBefore, HashFile(runtime.KeptImage));
    }

    private static int AvailableBridgePorts()
    {
        for (var port = 26000; port < 29000; port += PortChoice.Width)
        {
            var listeners = new List<System.Net.Sockets.TcpListener>();
            try
            {
                for (var offset = 0; offset < PortChoice.Width; offset++)
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port + offset);
                    listeners.Add(listener); listener.Start();
                }
                return port;
            }
            catch (SocketException) { }
            finally { foreach (var listener in listeners) listener.Stop(); }
        }
        throw new IOException("No owned five-port bridge range available.");
    }
}
