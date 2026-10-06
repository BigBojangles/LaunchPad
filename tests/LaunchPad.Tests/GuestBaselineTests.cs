using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public class GuestBaselineTests
{
    // Read-only agent version probes in a disposable overlay. This does not prove
    // interactive sign-in, confinement parity, return apply, or installed packaging.
    [EnvironmentFact("LAUNCHPAD_GUEST_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task DisposableGuestRunsCustomProgramAsBuilderAndFindsBundledAgents()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var template = SecurityTemplate(runtime);
        var requestedReport = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(requestedReport))
            requestedReport = null;
        else
        {
            requestedReport = Path.GetFullPath(requestedReport);
            var reportsRoot = Path.GetFullPath(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults")) + Path.DirectorySeparatorChar;
            Assert.True(requestedReport.StartsWith(reportsRoot, StringComparison.OrdinalIgnoreCase), "Private guest reports must remain under TestResults.");
            Assert.False(File.Exists(requestedReport), "Refusing to overwrite existing guest evidence.");
        }
        var templateSha256 = HashFile(template);
        var baseImageSha256 = HashFile(Path.Combine(Path.GetDirectoryName(runtime.KeptImage)!, PublicRuntime.BaseName));
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var disk = Path.Combine(root, "session.qcow2");
        await RunTool(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", template, disk);
        var repairPath = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_REPAIR");
        var linpeasPath = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_LINPEAS");
        var nmapPath = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_NMAP");
        var securityProbe = !string.IsNullOrWhiteSpace(linpeasPath);
        var nmapProbe = !string.IsNullOrWhiteSpace(nmapPath);
        var restartProbe = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_RESTART") == "1";
        var diagnosticPath = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_DIAGNOSTIC");
        var diagnosticProbe = !string.IsNullOrWhiteSpace(diagnosticPath);
        string? diagnosticSha256 = null;
        Assert.True(new[] { securityProbe, nmapProbe, restartProbe, diagnosticProbe }.Count(value => value) <= 1, "Different probes require separate disposable overlays.");
        if (securityProbe)
            Assert.Equal("7454e8f3fc817fdb7de0818ac7b80a05071ebf4194f7a6e13689985033593870", HashFile(linpeasPath!));
        if (nmapProbe)
            Assert.Equal("a5d507f29437bef3bedd4771ff9aaa8fc1c2a109ddba1f5b1cf12027456929be", HashFile(nmapPath!));
        string? overlayStartupRepairSha256 = null;
        if (!string.IsNullOrWhiteSpace(repairPath))
        {
            overlayStartupRepairSha256 = HashFile(repairPath);
            await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash",
                ToWslPath(Path.Combine(RepositoryRoot(), "scripts", "inject-disposable-startup.sh")),
                ToWslPath(disk), ToWslPath(repairPath), ToWslPath(template));
            Assert.Equal(templateSha256, HashFile(template));
        }
        var port = AvailablePorts();
        // Exercise the same path shortening as production, with full input paths.
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null,
            serialLog: Path.Combine(root, "serial.log"),
            memoryMb: GuestMemory.DefaultMegabytes, cores: 2, firmwareDir: runtime.FirmwareDir, workingDirectory: runtime.QemuDirectory);
        await File.WriteAllTextAsync(Path.Combine(root, "launch.json"), JsonSerializer.Serialize(new { runtime.QemuExe, runtime.QemuDirectory, args }, new JsonSerializerOptions { WriteIndented = true }));
        var started = Stopwatch.StartNew();
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, runtime.QemuDirectory, args, out var process),
            "QEMU launch failed: win32 " + TestUserRunner.LastStartError);
        Assert.NotNull(process);
        using var machine = process!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(nmapProbe ? 20 : securityProbe ? 9 : 2));
        var output = "";
        var captured = new StringBuilder();
        var stages = new Dictionary<string, double>();
        var restartReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TcpListener? hostControl = null;
        int? hostControlPort = null;
        var hostControlAliveAfter = false;
        var hostControlUnexpectedConnection = false;
        var guestShutdownObserved = false;
        string? cleanupError = null;
        try
        {
            if (nmapProbe)
            {
                hostControl = new TcpListener(System.Net.IPAddress.Loopback, 0);
                hostControl.Start();
                hostControlPort = ((System.Net.IPEndPoint)hostControl.LocalEndpoint).Port;
                using var controlClient = new TcpClient();
                await controlClient.ConnectAsync(System.Net.IPAddress.Loopback, hostControlPort.Value, deadline.Token);
                using var accepted = await hostControl.AcceptTcpClientAsync(deadline.Token);
                Assert.True(controlClient.Connected && accepted.Connected, "The owned host control must actually accept a local connection.");
            }
            using var tui = await Connect(QemuCommand.TuiPort(port), machine, deadline.Token);
            using var sizing = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            sizing?.Send(80, 30);
            var capture = Capture(tui.GetStream(), captured, deadline.Token, restartReady);
            byte[]? restartProgram = null;
            using (var fence = await Connect(QemuCommand.FencePort(port), machine, deadline.Token))
            {
                stages["qemuStartToHostChannelSeconds"] = started.Elapsed.TotalSeconds;
                var program = Encoding.UTF8.GetBytes("""
                    #!/bin/sh
                    printf '\nLP-PROBE-BEGIN\n'
                    printf 'IDENTITY:'; id
                    printf 'PROFILE:'; cat /proc/self/attr/current 2>/dev/null || true
                    printf 'PROJECT:'; cat baseline-input.txt
                    printf 'PROJECT-WRITE:'; printf 'probe\n' > baseline-output.txt && echo OK
                    for tool in grok codex claude; do
                        printf 'TOOL:%s:' "$tool"
                        if command -v "$tool" >/dev/null 2>&1; then
                            timeout 20 "$tool" --version
                            printf 'TOOL-EXIT:%s:%s\n' "$tool" "$?"
                        else
                            printf 'MISSING\n'
                        fi
                    done
                    if [ -f restart-input.txt ]; then
                        printf 'RESTART-FIXTURE:'; cat restart-input.txt
                    fi
                    printf 'LP-PROBE-END\n'
                    """ + "\n");
                if (diagnosticProbe)
                {
                    diagnosticPath = Path.GetFullPath(diagnosticPath!);
                    Assert.Equal(Path.Combine(RepositoryRoot(), "scripts", "diagnose-claude-startup.sh"), diagnosticPath);
                    var diagnosticBytes = await File.ReadAllBytesAsync(diagnosticPath, deadline.Token);
                    diagnosticSha256 = Convert.ToHexString(SHA256.HashData(diagnosticBytes)).ToLowerInvariant();
                    var diagnostic = Encoding.UTF8.GetString(diagnosticBytes);
                    var probe = Encoding.UTF8.GetString(program);
                    program = Encoding.UTF8.GetBytes(probe.Replace("printf 'LP-PROBE-END\\n'", diagnostic + "\nprintf 'LP-PROBE-END\\n'", StringComparison.Ordinal));
                }
                if (securityProbe)
                    program = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "scripts", "security-guest-unprivileged.sh"), deadline.Token);
                if (nmapProbe)
                    program = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "scripts", "security-nmap-guest.sh"), deadline.Token);
                if (restartProbe)
                {
                    restartProgram = program;
                    program = Encoding.UTF8.GetBytes("#!/bin/sh\nprintf '\\nLP-RESTART-READY\\n'\nexec /usr/bin/cat\n");
                }
                var stream = fence.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("AGENT custom\nAGENT-CMD lp-baseline\nPROGRAM " + program.Length + " lp-baseline\n"), deadline.Token);
                await stream.WriteAsync(program, deadline.Token);
                var fixture = Encoding.UTF8.GetBytes("fixture-arrived\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(fixture.Length, "baseline-input.txt")), deadline.Token);
                await stream.WriteAsync(fixture, deadline.Token);
                if (securityProbe)
                {
                    var linpeas = await File.ReadAllBytesAsync(linpeasPath!, deadline.Token);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(linpeas.Length, "linpeas.sh")), deadline.Token);
                    await stream.WriteAsync(linpeas, deadline.Token);
                }
                if (nmapProbe)
                {
                    foreach (var payload in new[] {
                        (Path: nmapPath!, Name: "nmap-7.991.tar.bz2"),
                        (Path: Path.Combine(RepositoryRoot(), "scripts", "security-nmap-probe.py"), Name: "security-nmap-probe.py"),
                        (Path: Path.Combine(RepositoryRoot(), "scripts", "security-connectivity.json"), Name: "expected-connectivity.json") })
                    {
                        var body = await File.ReadAllBytesAsync(payload.Path, deadline.Token);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(body.Length, payload.Name)), deadline.Token);
                        await stream.WriteAsync(body, deadline.Token);
                    }
                    var hostFixture = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { address = "127.0.0.1", port = hostControlPort }));
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(hostFixture.Length, "host-control.json")), deadline.Token);
                    await stream.WriteAsync(hostFixture, deadline.Token);
                }
                stages["qemuStartToTransferSentSeconds"] = started.Elapsed.TotalSeconds;
            }
            using var status = await Connect(QemuCommand.StatusPort(port), machine, deadline.Token);
            if (restartProbe)
            {
                await restartReady.Task.WaitAsync(deadline.Token);
                // Hold the transfer connection before PAUSE: an absent host peer
                // makes the guest's read_pause_files see EOF immediately. This
                // isolates startup-repair validation from the host ordering bug.
                using var restartFence = await Connect(QemuCommand.FencePort(port), machine, deadline.Token);
                await status.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PAUSE\n"), deadline.Token);
                await WaitForSerialMarker(Path.Combine(root, "serial.log"), "DOOR-OPEN", deadline.Token);
                {
                    var stream = restartFence.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("PROGRAM " + restartProgram!.Length + " lp-baseline\n"), deadline.Token);
                    await stream.WriteAsync(restartProgram, deadline.Token);
                    var fixture = Encoding.UTF8.GetBytes("restart-fixture-arrived\n");
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(fixture.Length, "restart-input.txt")), deadline.Token);
                    await stream.WriteAsync(fixture, deadline.Token);
                }
                restartFence.Dispose();
                await WaitForSerialMarker(Path.Combine(root, "serial.log"), "DOOR-CLOSED", deadline.Token);
                stages["qemuStartToRestartClosedSeconds"] = started.Elapsed.TotalSeconds;
            }
            output = await capture;
            if (hostControl is not null)
            {
                hostControlAliveAfter = hostControl.Server.IsBound;
                hostControlUnexpectedConnection = hostControl.Pending();
            }
            stages["qemuStartToProbeFinishedSeconds"] = started.Elapsed.TotalSeconds;
        }
        finally
        {
            output = captured.ToString();
            hostControl?.Stop();
            // Stop only the process created by this check; retain overlay and logs.
            try
            {
                guestShutdownObserved = MachineShutdown.WaitForGuestExit(machine, port);
                if (!guestShutdownObserved && !machine.HasExited) ConsoleSizeLink.Quit(port);
                if (!machine.WaitForExit(5000))
                {
                    machine.Kill(entireProcessTree: true);
                    if (!machine.WaitForExit(5000)) throw new IOException("Owned audit VM did not stop.");
                }
            }
            catch (Exception ex) { cleanupError = ex.Message; }
            TestUserRunner.ReleaseMachine(machine.Id);
            var report = new
            {
                capturedUtc = DateTime.UtcNow,
                hostIdentity = System.Security.Principal.WindowsIdentity.GetCurrent().Name,
                launchIdentity = TestUserRunner.UserName,
                template = template,
                templateSha256,
                baseImageSha256,
                overlayStartupRepairSha256,
                probeKind = diagnosticProbe ? "custom-claude-startup-diagnostic" : nmapProbe ? "custom-unprivileged-nmap" : securityProbe ? "custom-unprivileged-linpeas" : "custom-agent-versions",
                diagnosticSha256,
                memoryMegabytes = GuestMemory.DefaultMegabytes, cores = 2,
                nmapSourceSha256 = nmapProbe ? HashFile(nmapPath!) : null,
                hostControlPort,
                hostControlAliveAfter,
                hostControlUnexpectedConnection,
                restartProbe,
                linpeasSha256 = securityProbe ? HashFile(linpeasPath!) : null,
                disposableOverlay = disk,
                stages,
                output,
                guestShutdownObserved,
                cleanupError,
                limitations = diagnosticProbe
                    ? "Bounded Claude startup diagnosis under the custom-program builder context; no sign-in, host return apply, confinement or persistence acceptance. Host-channel availability is not guest handoff readiness."
                    : nmapProbe
                    ? "Nmap under the custom-program builder context and three owned TCP controls only. This does not prove bundled-agent or child-process confinement or all host boundaries. Audit source/build are separate from shipping template."
                    : securityProbe
                    ? "LinPEAS under the custom-program builder context only; bundled-agent confinement parity and privileged-system coverage remain unverified. Cloud metadata, brute force, online lookups and network discovery excluded. A startup repair changes the diagnostic overlay and is not an exact original-candidate security pass."
                    : "Custom-program version probes only; interactive bundled agents, confinement, return, and installed runtime remain unverified. Host-channel availability is not guest handoff readiness."
            };
            var serializedReport = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(root, "probe.json"), serializedReport);
            if (requestedReport is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(requestedReport)!);
                await File.WriteAllTextAsync(requestedReport, serializedReport);
            }
        }
        Assert.Null(cleanupError);
        Assert.True(guestShutdownObserved, "The owned audit guest did not finish normal shutdown; retained evidence is incomplete.");
        Assert.Equal(templateSha256, HashFile(template));
        Assert.Contains("LP-PROBE-END", output, StringComparison.Ordinal);
        Assert.Contains("uid=1000(builder)", output, StringComparison.Ordinal);
        Assert.Contains("fixture-arrived", output, StringComparison.Ordinal);
        Assert.Contains("PROJECT-WRITE:OK", output, StringComparison.Ordinal);
        if (diagnosticProbe)
            Assert.Contains("CLAUDE-DIAGNOSTIC-END", output, StringComparison.Ordinal);
        if (restartProbe)
        {
            Assert.Contains("RESTART-FIXTURE:restart-fixture-arrived", output, StringComparison.Ordinal);
            var serial = await File.ReadAllTextAsync(Path.Combine(root, "serial.log"));
            Assert.True(serial.Split("AGENT-PICK custom", StringSplitOptions.None).Length >= 3, "Initial and restarted agent markers must both be relayed by the privileged parent.");
        }
        if (nmapProbe)
        {
            Assert.Contains("NMAP-BUILD-EXIT:0", output, StringComparison.Ordinal);
            Assert.Contains("Nmap version 7.991", output, StringComparison.Ordinal);
            Assert.Contains("NMAP-PROBE-EXIT:0", output, StringComparison.Ordinal);
            Assert.True(hostControlAliveAfter && !hostControlUnexpectedConnection, "Owned host listener must remain alive and receive no guest connection.");
            var line = output.Split('\n').Single(value => value.StartsWith("NMAP-COVERAGE-JSON:", StringComparison.Ordinal));
            using var coverage = JsonDocument.Parse(line["NMAP-COVERAGE-JSON:".Length..]);
            Assert.Equal(1000, coverage.RootElement.GetProperty("uid").GetInt32());
            Assert.Equal(HashFile(Path.Combine(RepositoryRoot(), "scripts", "security-connectivity.json")), coverage.RootElement.GetProperty("manifestSha256").GetString());
            Assert.True(coverage.RootElement.GetProperty("boundaryAssertionsPassed").GetBoolean());
            Assert.Equal(3, coverage.RootElement.GetProperty("observations").GetArrayLength());
            Assert.Equal(templateSha256, HashFile(template));
        }
        else if (securityProbe)
        {
            Assert.Contains("LINPEAS-EXIT:0", output, StringComparison.Ordinal);
            Assert.Contains("System Information", output, StringComparison.Ordinal);
            Assert.Contains("Users Information", output, StringComparison.Ordinal);
            Assert.Contains("Software Information", output, StringComparison.Ordinal);
            Assert.Contains("LOCAL-NETWORK-END", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Enumerate and search Privilege Escalation vectors.", output, StringComparison.Ordinal);
        }
        else
            foreach (var tool in new[] { "grok", "codex", "claude" })
                Assert.Contains("TOOL-EXIT:" + tool + ":0", output, StringComparison.Ordinal);
    }

    internal static string SecurityTemplate(PublicRuntime runtime)
    {
        var requested = Environment.GetEnvironmentVariable("LAUNCHPAD_SECURITY_TEMPLATE");
        if (string.IsNullOrWhiteSpace(requested)) return runtime.KeptImage;
        var path = Path.GetFullPath(requested);
        var runtimeImages = Path.GetDirectoryName(runtime.KeptImage)! + Path.DirectorySeparatorChar;
        var migration = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar;
        Assert.True(path.StartsWith(runtimeImages, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(migration, StringComparison.OrdinalIgnoreCase), "Audit only runtime templates or owned private candidates.");
        Assert.Equal(".qcow2", Path.GetExtension(path), ignoreCase: true);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "Linked audit template.");
        var expected = Environment.GetEnvironmentVariable("LAUNCHPAD_SECURITY_TEMPLATE_SHA256");
        Assert.Matches("^[a-fA-F0-9]{64}$", expected ?? "");
        Assert.Equal(expected, HashFile(path), ignoreCase: true);
        return path;
    }

    private static async Task<string> Capture(NetworkStream stream, StringBuilder output, CancellationToken token, TaskCompletionSource<bool> restartReady)
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            output.Append(Encoding.UTF8.GetString(buffer, 0, count));
            if (output.Length > 8 * 1024 * 1024)
                throw new IOException("Private guest capture exceeded 8 MiB; required coverage is incomplete.");
            if (output.ToString().Contains("LP-RESTART-READY", StringComparison.Ordinal)) restartReady.TrySetResult(true);
            if (output.ToString().Contains("LP-PROBE-END", StringComparison.Ordinal)) break;
            if (output.ToString().Contains("tty: Permission denied", StringComparison.Ordinal)) break;
        }
        return output.ToString();
    }

    internal static async Task WaitForSerialMarker(string path, string marker, CancellationToken token, object? context = null)
    {
        var startedUtc = DateTime.UtcNow;
        DateTime? lastReadUtc = null;
        long? lastReadBytes = null;
        string? lastTail = null;
        var readCount = 0;
        var ioErrors = 0;
        string? lastError = null;
        int? lastErrorHresult = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(file);
                    var text = await reader.ReadToEndAsync(token);
                    lastReadUtc = DateTime.UtcNow;
                    lastReadBytes = file.Position;
                    lastTail = text.Length <= 2048 ? text : text[^2048..];
                    readCount++;
                    if (text.Contains(marker, StringComparison.Ordinal)) return;
                }
                catch (IOException error)
                {
                    ioErrors++;
                    lastErrorHresult = error.HResult;
                    lastError = error.Message.Length <= 512 ? error.Message : error.Message[..512];
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            // Preserve the last COMPLETED live read. Do not read the stopped
            // file here and retroactively treat a late marker as timely receipt.
            var diagnostic = Path.Combine(Path.GetDirectoryName(path)!, "serial-marker-cancelled-" + Guid.NewGuid().ToString("N") + "-private.json");
            try
            {
                var metadata = new FileInfo(path);
                await File.WriteAllTextAsync(diagnostic, JsonSerializer.Serialize(new
                {
                    schema = 1, path, marker, startedUtc, cancelledUtc = DateTime.UtcNow, context,
                    markerObservedBeforeCancellation = false, readCount, lastReadUtc, lastReadBytes, lastTail,
                    ioErrors, lastError, lastErrorHresult,
                    lengthAtCancellation = metadata.Exists ? (long?)metadata.Length : null,
                    lastWriteUtcAtCancellation = metadata.Exists ? (DateTime?)metadata.LastWriteTimeUtc : null,
                    limitations = "Last completed live read only. Cancellation may share a deadline with earlier stages. Metadata is sampled after cancellation; later final serial contents cannot establish timely marker receipt."
                }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException)
            { error.Data["diagnosticWriteError"] = diagnosticError.Message; }
            throw new OperationCanceledException("Serial marker was not observed before cancellation: " + marker + "; diagnostics: " + diagnostic, error, token);
        }
    }

    private static async Task<TcpClient> Connect(int port, Process machine, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (machine.WaitForExit(0)) throw new IOException("The owned audit guest exited before opening its channel. Inspect the retained launch evidence.");
            var client = new TcpClient();
            try { await client.ConnectAsync("127.0.0.1", port, token); return client; }
            catch (SocketException) { client.Dispose(); await Task.Delay(100, token); }
            catch { client.Dispose(); throw; }
        }
    }

    internal static int AvailablePorts(int first = 24000, int last = 24999)
    {
        for (var port = first; port <= last; port += 4)
        {
            var listeners = new List<System.Net.Sockets.TcpListener>();
            try
            {
                for (var offset = 0; offset < 4; offset++)
                {
                    var listener = new TcpListener(System.Net.IPAddress.Loopback, port + offset);
                    listeners.Add(listener);
                    listener.Start();
                }
                return port;
            }
            catch (SocketException) { }
            finally { foreach (var listener in listeners) listener.Stop(); }
        }
        throw new IOException("No free diagnostic port range.");
    }

    internal static async Task RunTool(string exe, params string[] arguments)
        => await RunToolWithTimeout(TimeSpan.FromSeconds(30), exe, arguments);

    internal static async Task RunToolWithTimeout(TimeSpan duration, string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not run " + exe);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // WSL descendants can retain redirected pipe handles after its launcher
        // exits. Bound process exit and output collection independently.
        if (!await Task.Run(() => process.WaitForExit(checked((int)duration.TotalMilliseconds))))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Owned diagnostic tool timed out: " + exe);
        }
        var streams = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, exe + ": " + streams[0] + streams[1]);
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LaunchPad.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("LaunchPad repository root unavailable.");
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string ToWslPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':' || full[2] != '\\')
            throw new ArgumentException("Diagnostic WSL transport requires a local drive path.");
        return "/mnt/" + char.ToLowerInvariant(full[0]) + "/" + full[3..].Replace('\\', '/');
    }
}
