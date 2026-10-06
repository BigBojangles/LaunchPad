using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public class GuestAgentLaunchTests
{
    [EnvironmentTheory("LAUNCHPAD_AGENT_SMOKE", "1")]
    [Trait("Category", "Integration")]
    [InlineData("grok")]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task FreshGuestStartsTheSelectedBundledAgent(string agent)
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var mainTemplateHash = GuestBaselineTests.HashFile(runtime.KeptImage);
        var template = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_TEMPLATE") ?? runtime.KeptImage;
        template = Path.GetFullPath(template);
        if (!string.Equals(template, runtime.KeptImage, StringComparison.OrdinalIgnoreCase))
        {
            var reports = Path.GetFullPath(Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults")) + Path.DirectorySeparatorChar;
            if (template.StartsWith(reports, StringComparison.OrdinalIgnoreCase))
                Assert.Equal("template.qcow2", Path.GetFileName(template));
            else
            {
                // A reviewed promoted delta can be measured before selecting it.
                // Only new disposable session overlays receive writes.
                Assert.Equal(Path.GetDirectoryName(runtime.KeptImage), Path.GetDirectoryName(template), ignoreCase: true);
                Assert.Matches("^debian-12-builder-runtime-[a-z0-9-]+\\.qcow2$", Path.GetFileName(template));
                Assert.False(File.GetAttributes(template).HasFlag(FileAttributes.ReparsePoint));
                var expected = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_TEMPLATE_SHA256");
                Assert.Matches("^[a-fA-F0-9]{64}$", expected ?? "");
                Assert.Equal(expected, GuestBaselineTests.HashFile(template), ignoreCase: true);
            }
            // Grant only read access to this diagnostic candidate, not private reports.
            await GuestBaselineTests.RunTool("icacls.exe", template, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        }
        var templateHash = GuestBaselineTests.HashFile(template);
        var daily = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_DAILY") == "1";
        var singleDiagnostic = daily && Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_SINGLE_DIAGNOSTIC") == "1";
        var supervised = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_SUPERVISED_CLOSE") == "1";
        Assert.False(daily && supervised, "Use the focused close/reopen check separately from the daily timing matrix.");
        var samples = new List<LaunchSample>();
        var assemblyHash = GuestBaselineTests.HashFile(typeof(QemuCommand).Assembly.Location);
        var runId = Guid.NewGuid().ToString("N")[..12];
        var summaryPath = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            (singleDiagnostic ? "medium-diagnostic-" : supervised ? "owner-close-" : "daily-") + agent + "-" + runId + "-private.json");
        var expectedFresh = singleDiagnostic ? 1 : daily ? 3 : 1;
        var expectedRepeat = singleDiagnostic ? 0 : daily ? 5 : supervised ? 1 : 0;
        string? root = null;
        var body = new byte[daily ? 16384 : 0];
        new Random(42).NextBytes(body);
        try
        {
            for (var sample = 0; sample < expectedFresh + expectedRepeat; sample++)
            {
                var fresh = sample < expectedFresh;
                if (fresh)
                {
                    root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
                    Directory.CreateDirectory(root);
                    await GuestBaselineTests.RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
                    await GuestBaselineTests.RunTool(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", template, Path.Combine(root, "session.qcow2"));
                }
                var result = await Launch(agent, runtime, template, templateHash, mainTemplateHash, root!, sample, fresh, daily, body);
                samples.Add(result);
                if (daily || supervised) await SaveSummary();
            }
            Assert.Equal(templateHash, GuestBaselineTests.HashFile(template));
            Assert.Equal(mainTemplateHash, GuestBaselineTests.HashFile(runtime.KeptImage));
        }
        finally { if (daily || supervised) await SaveSummary(); }

        Task SaveSummary()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
            return File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(new
            {
                capturedUtc = DateTime.UtcNow, agent, template, templateSha256 = templateHash, mainTemplateSha256 = mainTemplateHash,
                assemblySha256 = assemblyHash,
                hostOs = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                hostArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                hostProcessor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), hostLogicalProcessors = Environment.ProcessorCount,
                hostInstalledMemoryMegabytes = HostResources.Current.InstalledMemoryMegabytes,
                protocol = singleDiagnostic ? "single-fresh-medium-diagnostic" : daily ? "daily-three-fresh-five-repeat" : supervised ? "terminal-close-reopen" : "single-fresh-small-smoke",
                expectedFresh, expectedRepeat, fixtureFiles = daily ? 1001 : 1, fixturePayloadBytes = 1000L * body.Length,
                fixtureBlockSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant(),
                coverageComplete = samples.Count(s => s.LaunchKind == "fresh") == expectedFresh && samples.Count(s => s.LaunchKind == "repeat") == expectedRepeat,
                shutdownMode = supervised ? "abrupt-terminal-close-independent-owner" : Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_GRACEFUL") == "1" ? "guest-acpi-request-with-exit-check" : "qmp-quit",
                statistics = new { fresh = Statistics("fresh"), repeat = Statistics("repeat") },
                samples,
                limitations = "Fresh overlays are not cold-host boots. Initial login/onboarding only; human authentication and remote service delay are not agent prompt acceptance. Repeat boots reuse the third disposable overlay and resupply the same fixture. Cleanup mode and observed guest shutdown are recorded per launch; successful repeats do not prove all failure-path durability. Polling observes existing serial markers; no guest protocol changes. App start, host return, installer and complete resource profiling are separate measurements. No before-change comparable series exists yet, so this run alone cannot establish timing parity or improvement."
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }

        object Statistics(string kind)
        {
            var group = samples.Where(s => s.LaunchKind == kind).ToArray();
            return new { count = group.Length, stages = group.SelectMany(s => s.Stages.Keys).Distinct().ToDictionary(key => key, key =>
            {
                var values = group.Where(s => s.Stages.ContainsKey(key)).Select(s => s.Stages[key]).Order().ToArray();
                return new { count = values.Length, median = (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2, max = values[^1] };
            }) };
        }
    }

    private sealed record LaunchSample(int Sample, string LaunchKind, string Root, Dictionary<string, double> Stages,
        bool ObservedScreen, long OverlayBytesBefore, long OverlayBytesAfter, double? HostQemuCpuSeconds,
        long? HostQemuWorkingSetBytes, string? HostResourceError, bool GuestShutdownObserved);

    private static async Task<LaunchSample> Launch(string agent, PublicRuntime runtime, string template, string templateHash,
        string mainTemplateHash, string root, int sample, bool fresh, bool daily, byte[] fixtureBody)
    {
        var disk = Path.Combine(root, "session.qcow2");
        var port = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_SUPERVISED_CLOSE") == "1"
            ? GuestBaselineTests.AvailablePorts(PortChoice.First, PortChoice.Last) : GuestBaselineTests.AvailablePorts();
        var serialPath = Path.Combine(root, "serial.log");
        // Only this fixture's previous helper and QEMU have exited before reuse.
        foreach (var name in new[] { "tui.pid", "console.ready", "winsize.txt" }) File.Delete(Path.Combine(root, name));
        await File.WriteAllTextAsync(serialPath, "");
        var overlayBytesBefore = new FileInfo(disk).Length;
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null,
            serialLog: serialPath, memoryMb: GuestMemory.DefaultMegabytes, cores: 2,
            firmwareDir: runtime.FirmwareDir, workingDirectory: runtime.QemuDirectory);
        var clock = Stopwatch.StartNew();
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, runtime.QemuDirectory, args, out var process),
            "QEMU launch failed: win32 " + TestUserRunner.LastStartError);
        using var machine = process!;
        var deadlineUtc = DateTime.UtcNow.AddMinutes(2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        HiddenConsole? terminal = null;
        var output = "";
        var stages = new Dictionary<string, double>();
        string? cleanupError = null;
        var observedScreen = false;
        var lastSnapshot = TimeSpan.Zero;
        double? hostQemuCpuSeconds = null;
        long? hostQemuWorkingSetBytes = null;
        string? hostResourceError = null;
        var graceful = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_GRACEFUL") == "1";
        var supervised = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_SUPERVISED_CLOSE") == "1";
        Process? owner = null;
        string? ownerResult = null;
        var guestShutdownObserved = false;
        try
        {
            if (supervised)
            {
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                var exe = Path.Combine(GuestBaselineTests.RepositoryRoot(), "src", "LaunchPad", "bin", configuration, "net8.0", "LaunchPad.exe");
                owner = await SessionGuardian.StartAsync(machine, port, root, agent, executable: exe);
            }
            terminal = new HiddenConsole(root, QemuCommand.TuiPort(port));
            using var sizing = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            Assert.NotNull(sizing);
            sizing.Send(100, 30);
            using (var fence = await Connect(QemuCommand.FencePort(port), deadline.Token))
            {
                stages["qemuStartToHostChannelSeconds"] = clock.Elapsed.TotalSeconds;
                var body = Encoding.UTF8.GetBytes("Disposable LaunchPad agent launch fixture. No user credentials or projects supplied.\n");
                var stream = fence.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(AgentChoice.Header(agent)), deadline.Token);
                if (daily)
                {
                    await GuestBaselineTests.WaitForSerialMarker(serialPath, "AGENT-IN " + agent, deadline.Token);
                    stages["qemuStartToGuestHandoffReadySeconds"] = clock.Elapsed.TotalSeconds;
                }
                stages["transferFirstByteSeconds"] = clock.Elapsed.TotalSeconds;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(body.Length, "README.md")), deadline.Token);
                await stream.WriteAsync(body, deadline.Token);
                if (daily)
                    for (var file = 0; file < 1000; file++)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(FenceFiles.Header(fixtureBody.Length, $"medium/part-{file:D4}.dat")), deadline.Token);
                        await stream.WriteAsync(fixtureBody, deadline.Token);
                    }
                stages["qemuStartToTransferSentSeconds"] = clock.Elapsed.TotalSeconds;
            }
            if (daily)
            {
                await GuestBaselineTests.WaitForSerialMarker(serialPath, "WARN fence-ready", deadline.Token);
                stages["transferFirstByteToImportObservedSeconds"] = clock.Elapsed.TotalSeconds - stages["transferFirstByteSeconds"];
            }
            using var statusClient = await Connect(QemuCommand.StatusPort(port), deadline.Token);
            using var status = new StatusLink(statusClient);
            await GuestBaselineTests.WaitForSerialMarker(serialPath, "AGENT-PICK " + agent, deadline.Token);
            stages["qemuStartToAgentSelectedSeconds"] = clock.Elapsed.TotalSeconds;
            while (!deadline.IsCancellationRequested)
            {
                await Task.Delay(200, deadline.Token);
                output = terminal.ReadScreen();
                if (clock.Elapsed - lastSnapshot >= TimeSpan.FromSeconds(2))
                {
                    await File.WriteAllTextAsync(Path.Combine(root, "agent-screen-private.txt"), output, deadline.Token);
                    lastSnapshot = clock.Elapsed;
                }
                if (IsInitialScreen(agent, output))
                {
                    observedScreen = true;
                    stages["qemuStartToInitialScreenSeconds"] = clock.Elapsed.TotalSeconds;
                    stages["agentSelectedObservedToInitialScreenSeconds"] = clock.Elapsed.TotalSeconds - stages["qemuStartToAgentSelectedSeconds"];
                    break;
                }
                if (terminal.Process.HasExited || machine.HasExited) break;
                if (output.Contains("Error: failed to record pid-managed app-server", StringComparison.Ordinal)) break;
            }
            Assert.True(observedScreen, "The selected agent did not produce a recognized onboarding/login screen; see private evidence.");
            if (daily)
            {
                stages["qemuStartToFilesEndWaitSeconds"] = clock.Elapsed.TotalSeconds;
                await GuestBaselineTests.WaitForSerialMarker(serialPath, "FILES-END", deadline.Token,
                    new { agent, sample, fresh, deadlineUtc, remainingSecondsAtWait = (deadlineUtc - DateTime.UtcNow).TotalSeconds,
                        stages = new Dictionary<string, double>(stages) });
                stages["qemuStartToFilesEndObservedSeconds"] = clock.Elapsed.TotalSeconds;
                var serial = await ReadShared(serialPath, deadline.Token);
                Assert.Contains("FILE medium/part-0999.dat 16384", serial, StringComparison.Ordinal);
                Assert.DoesNotContain("WRITE-FAIL", serial, StringComparison.Ordinal);
                Assert.DoesNotContain("BAD-LINE", serial, StringComparison.Ordinal);
                try
                {
                    machine.Refresh();
                    hostQemuCpuSeconds = machine.TotalProcessorTime.TotalSeconds;
                    hostQemuWorkingSetBytes = machine.WorkingSet64;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { hostResourceError = ex.Message; }
            }
            // This check observes onboarding/login only. It never signs in or applies returns.
            if (supervised)
            {
                await WindowsConsoleProbeTests.Until(() => SessionGuardian.TryReadLiveOwner(root)?.TerminalPid == terminal.Process.Id, deadline.Token);
                Assert.False(machine.HasExited);
                terminal.Process.Kill();
                // Match WatchTui: release the single QMP resize connection
                // before the independent owner sends its shutdown command.
                sizing.Dispose();
                Assert.True(await Task.Run(() => machine.WaitForExit(45000)), "The terminal owner did not stop the disposable VM within its shutdown interval.");
                await owner!.WaitForExitAsync(deadline.Token);
                ownerResult = await File.ReadAllTextAsync(Path.Combine(root, SessionGuardian.ResultFile), deadline.Token);
                using var document = JsonDocument.Parse(ownerResult);
                guestShutdownObserved = document.RootElement.GetProperty("guestShutdownObserved").GetBoolean();
                Assert.False(document.RootElement.GetProperty("needsRecovery").GetBoolean());
                Assert.Contains("reboot: Power down", await ReadShared(serialPath, deadline.Token), StringComparison.Ordinal);
            }
            else
            {
                terminal.Type("\x03");
                await Task.Delay(1000, deadline.Token);
            }
        }
        finally
        {
            // Only this disposable machine is stopped. A requested shutdown is
            // not an observed shutdown; fallback must remain explicit evidence.
            try
            {
                if (supervised && !guestShutdownObserved && owner is not null)
                    _ = await Task.Run(() => machine.WaitForExit(45000));
                if (graceful && !supervised)
                {
                    guestShutdownObserved = MachineShutdown.WaitForGuestExit(machine, port);
                }
                if (!guestShutdownObserved)
                {
                    ConsoleSizeLink.Quit(port);
                    if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) { cleanupError = ex.Message; }
            TestUserRunner.ReleaseMachine(machine.Id);
            terminal?.Dispose();
            if (owner is not null)
            {
                if (!owner.HasExited) owner.WaitForExit(5000);
                owner.Dispose();
            }
            var report = new
            {
                capturedUtc = DateTime.UtcNow, agent,
                sample, launchKind = fresh ? "fresh" : "repeat",
                template, templateSha256 = templateHash, mainTemplateSha256 = mainTemplateHash, disposableOverlay = disk,
                launchIdentity = TestUserRunner.UserName,
                memoryMegabytes = GuestMemory.DefaultMegabytes, cores = 2,
                cpuModel = args[args.ToList().IndexOf("-cpu") + 1],
                observedScreen, stages, output, cleanupError, qemuExitCode = machine.WaitForExit(0) ? (int?)machine.ExitCode : null,
                gracefulRequested = graceful, guestShutdownObserved, supervisedTerminalClose = supervised, ownerResult,
                overlayBytesBefore, overlayBytesAfter = new FileInfo(disk).Length,
                hostQemuCpuSeconds, hostQemuWorkingSetBytes, hostResourceError,
                limitations = "Guest initial screen in the actual --tui helper and hidden Windows console only; no user auth/home supplied, no sign-in, no host return apply, no Windows Terminal desktop/window association, no confinement or persistence acceptance. Single samples are not benchmark acceptance."
            };
            await File.WriteAllTextAsync(Path.Combine(root, daily || supervised ? $"agent-launch-{sample:D2}-private.json" : "agent-launch-private.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (daily || supervised) File.Copy(serialPath, Path.Combine(root, $"serial-{sample:D2}.log"));
        }
        Assert.Null(cleanupError);
        if (graceful || supervised) Assert.True(guestShutdownObserved, "The guest did not finish its requested shutdown; fallback is not durability acceptance.");
        return new LaunchSample(sample, fresh ? "fresh" : "repeat", root, stages, observedScreen,
            overlayBytesBefore, new FileInfo(disk).Length, hostQemuCpuSeconds, hostQemuWorkingSetBytes, hostResourceError, guestShutdownObserved);
    }

    private static async Task<string> ReadShared(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync(token);
    }

    private static async Task<TcpClient> Connect(int port, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, port, token); return client; }
            catch (SocketException) { client.Dispose(); await Task.Delay(100, token); }
        }
    }

    private static bool IsInitialScreen(string agent, string text)
    {
        var markers = agent switch
        {
            "grok" => new[] { "Approve in your browser to finish signing in", "Sign in", "Log in", "Login", "Enter your prompt", "What would you like" },
            "codex" => new[] { "Welcome to Codex", "Sign in with ChatGPT", "OpenAI API key", "trust this directory" },
            "claude" => new[] { "Welcome to Claude Code", "Choose the text style", "Select the text style", "Let's get started" },
            _ => Array.Empty<string>()
        };
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
