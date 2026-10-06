using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

public sealed class GuestCustomBoundaryTests
{
    [EnvironmentFact("LAUNCHPAD_CUSTOM_HOST_BOUNDARY", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualCustomProgramAndChildKeepTheOwnedHostBoundary()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var expected = HashFile(runtime.KeptImage);
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var project = Path.Combine(root, "fixture");
        Directory.CreateDirectory(project);
        var fixtureId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(project, "original.txt"), fixtureId);
        var source = Path.Combine(RepositoryRoot(), "scripts", "owned-custom-boundary.py");
        var sourceBytes = await File.ReadAllBytesAsync(source);
        await File.WriteAllBytesAsync(Path.Combine(project, "owned-custom-boundary.py"), sourceBytes);
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var hostAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(adapter => adapter.GetIPProperties())
            .Where(properties => properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
            .SelectMany(properties => properties.UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        Assert.NotNull(hostAddress); // Missing owned-route coverage is not a passing skip.
        using var gatewayControl = new GuestAgentToolsTests.OwnedHostListener();
        using var hostAddressControl = new GuestAgentToolsTests.OwnedHostListener(hostAddress);
        using var ipv6GatewayControl = new GuestAgentToolsTests.OwnedHostListener(IPAddress.IPv6Loopback);
        Assert.True(gatewayControl.LiveWitness && hostAddressControl.LiveWitness);
        var disk = Path.Combine(root, "session.qcow2");
        await Run(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", runtime.KeptImage, disk);
        var port = AvailablePorts();
        var targets = new[]
        {
            new { name = "host-gateway-owned-listener", address = "10.0.2.2", port = gatewayControl.Port },
            new { name = "host-gateway-qmp", address = "10.0.2.2", port },
            new { name = "host-interface-owned-listener", address = hostAddress!.ToString(), port = hostAddressControl.Port },
            new { name = "host-interface-qmp", address = hostAddress.ToString(), port },
            new { name = "host-ipv6-gateway-owned-listener", address = "fec0::2", port = ipv6GatewayControl.Port }
        };
        await File.WriteAllTextAsync(Path.Combine(project, "owned-targets.json"), JsonSerializer.Serialize(new { fixtureId, network = targets }));
        var program = Path.Combine(project, "lp-custom-boundary");
        await File.WriteAllTextAsync(program, "#!/bin/sh\nexec python3 owned-custom-boundary.py\n");
        var serial = Path.Combine(root, "serial.log");
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: serial,
            memoryMb: 4096, cores: 2, firmwareDir: runtime.FirmwareDir, workingDirectory: root);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, root, args, out var started),
            "Owned custom-boundary VM error: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        JsonElement? nativeOwner = null;
        var output = new StringBuilder();
        Task? capture = null;
        StatusLink? status = null;
        ConsoleSizeLink? sizing = null;
        ProjectReturnReceipt? receipt = null;
        ReturnRecovery? recovery = null;
        var complete = false;
        var shutdown = false;
        var qmpControl = false;
        try
        {
            nativeOwner = await ObserveOwner(machine.Id, stop.Token);
            Assert.Equal(0, nativeOwner.Value.GetProperty("ReturnValue").GetInt32());
            Assert.Equal(TestUserRunner.UserName, nativeOwner.Value.GetProperty("User").GetString(), ignoreCase: true);
            using (var qmp = await Connect(port, machine, stop.Token))
            {
                using var reader = new StreamReader(qmp.GetStream(), leaveOpen: true);
                var greeting = await reader.ReadLineAsync(stop.Token);
                qmpControl = greeting?.Contains("\"QMP\"", StringComparison.Ordinal) == true;
                Assert.True(qmpControl, "Actual owned QMP listener greeting required.");
            }
            using var console = await Connect(QemuCommand.TuiPort(port), machine, stop.Token);
            sizing = await ConsoleSizeLink.ConnectAsync(port, stop.Token);
            Assert.NotNull(sizing);
            sizing.Send(80, 30);
            capture = Task.Run(async () =>
            {
                var bytes = new byte[4096];
                while (true)
                {
                    var count = await console.GetStream().ReadAsync(bytes, stop.Token);
                    if (count == 0) break;
                    lock (output) output.Append(Encoding.UTF8.GetString(bytes, 0, count));
                }
            });
            using (var fence = await Connect(QemuCommand.FencePort(port), machine, stop.Token))
            {
                var sent = await FenceHost.SendProjectAsync(fence.GetStream(), project, null, null, stop.Token,
                    new AgentLaunch(AgentChoice.Custom, "lp-custom-boundary", program), restoreGuestState: true, restoreHostHome: false);
                sent.Save(Path.Combine(root, "sent.manifest"));
            }
            status = new StatusLink(await Connect(QemuCommand.StatusPort(port), machine, stop.Token));
            await WaitForSerialMarker(serial, "DOOR-READY", stop.Token);
            await WaitOutput("CUSTOM-BOUNDARY-DONE", output, stop.Token);
            using var returned = await Connect(QemuCommand.FencePort(port), machine, stop.Token);
            recovery = ReturnRecovery.Create(root);
            var receive = Task.Run(() => ProjectPull.Receive(returned.GetStream(), recovery.Payload, TimeSpan.FromSeconds(30)));
            await console.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\n"), stop.Token);
            receipt = await receive.WaitAsync(stop.Token);
            recovery.SaveTransfer(project, receipt);
            Assert.True(receipt.Complete, receipt.Error);
            Assert.True(receipt.HasContentIdentities);
            Assert.Equal(fixtureId, await File.ReadAllTextAsync(Path.Combine(recovery.Payload, "custom-owned-write.txt")));
            Assert.Equal(fixtureId, await File.ReadAllTextAsync(Path.Combine(project, "original.txt")));
            Assert.False(File.Exists(Path.Combine(project, "custom-owned-write.txt")));
            complete = true;
        }
        finally
        {
            status?.Dispose();
            sizing?.Dispose();
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown) { ConsoleSizeLink.Quit(port); if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true); }
            stop.Cancel();
            if (capture is not null) try { await capture; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "custom-boundary-private.json"), JsonSerializer.Serialize(new
            {
                complete, shutdown, root, fixtureId, template = runtime.KeptImage,
                templateHash = expected, finalTemplateHash = HashFile(runtime.KeptImage), sourceHash,
                finalSourceHash = HashFile(source), assemblySha256 = HashFile(typeof(TestUserRunner).Assembly.Location),
                qmpControl, targets, arguments = args, nativeOwner,
                hostListeners = new { gatewayControl.LiveWitness, gatewayConnections = gatewayControl.Connections,
                    interfaceLiveWitness = hostAddressControl.LiveWitness, interfaceConnections = hostAddressControl.Connections },
                receipt, recovery = recovery?.DirectoryPath, output = Output(output),
                ipv6Listener = new { ipv6GatewayControl.LiveWitness, ipv6GatewayControl.Connections },
                limitations = "Actual custom Python program/child on fresh selected-image overlay, owned host TCP controls, guest IPv4/IPv6 development loopback and one normal example.com DNS/HTTPS HEAD positive control, production return. No test firewall/guest policy modification, port scan, host apply, credentials, real projects, live interface mutation, complete UDP/IPv6-interface/BFE-fault coverage or hypervisor-escape claim. An unreachable IPv6 gateway alone does not establish filtering. Native launch-account rights are a separate check."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown);
        Assert.Equal(expected, HashFile(runtime.KeptImage));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recovery!.Payload, "custom-boundary.json")));
        Assert.True(report.RootElement.GetProperty("complete").GetBoolean());
        Assert.Empty(report.RootElement.GetProperty("failures").EnumerateArray());
        Assert.Equal(0, gatewayControl.Connections);
        Assert.Equal(0, hostAddressControl.Connections);
        Assert.Equal(0, ipv6GatewayControl.Connections);
        Assert.All(report.RootElement.GetProperty("guestLoopback").EnumerateArray(), value => Assert.True(value.GetProperty("allowed").GetBoolean()));
        Assert.True(report.RootElement.GetProperty("external").GetProperty("dns").GetBoolean());
        Assert.True(report.RootElement.GetProperty("external").GetProperty("https").GetBoolean());
        Assert.Equal(sourceHash, HashFile(source));
    }

    private static async Task<JsonElement> ObserveOwner(int pid, CancellationToken stop)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$p=Get-CimInstance Win32_Process -Filter 'ProcessId=" + pid + "'; if(!$p){throw 'Owned QEMU missing'}; Invoke-CimMethod -InputObject $p -MethodName GetOwner | Select-Object ReturnValue,Domain,User | ConvertTo-Json -Compress" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(stop);
        var errors = process.StandardError.ReadToEndAsync(stop);
        await process.WaitForExitAsync(stop);
        Assert.True(process.ExitCode == 0, await errors);
        using var observed = JsonDocument.Parse(await output);
        return observed.RootElement.Clone();
    }
}
