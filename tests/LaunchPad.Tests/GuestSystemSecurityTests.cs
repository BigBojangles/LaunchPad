using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public class GuestSystemSecurityTests
{
    [EnvironmentFact("LAUNCHPAD_GUEST_LYNIS")]
    [Trait("Category", "Integration")]
    public async Task LynisAuditsTheRunningGuestWithExplicitPrivilegedDiagnosticSetup()
    {
        var archive = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_LYNIS")!;
        var requestedReport = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_LYNIS_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(requestedReport))
        {
            requestedReport = Path.GetFullPath(requestedReport);
            var generated = Path.GetFullPath(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults")) + Path.DirectorySeparatorChar;
            Assert.True(requestedReport.StartsWith(generated, StringComparison.OrdinalIgnoreCase), "Private system-audit evidence must remain under TestResults.");
            Assert.False(File.Exists(requestedReport), "Refusing to overwrite system-audit evidence.");
        }
        else requestedReport = null;
        var toolSha256 = HashFile(archive);
        Assert.Equal("b315c848323572500225312de7e9a3bf3b0c462f5b2d6f4ff56fe6ae521ad169", toolSha256);
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var template = SecurityTemplate(runtime);
        var templateSha256 = HashFile(template);
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var disk = Path.Combine(root, "session.qcow2");
        await RunTool(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", template, disk);
        var script = Path.Combine(RepositoryRoot(), "scripts", "security-lynis-overlay.sh");
        var auditSetupSha256 = HashFile(Path.Combine(RepositoryRoot(), "scripts", "security-lynis-guest.sh"));
        var overlayAdapterSha256 = HashFile(script);
        await RunToolWithTimeout(TimeSpan.FromMinutes(3), "wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(script), "stage",
            ToWslPath(disk), ToWslPath(archive), ToWslPath(template), ToWslPath(root));
        Assert.Equal(templateSha256, HashFile(template));
        // The custom Windows QEMU fails this fixture's expanded relative chain
        // even though the official qemu-img resolves its absolute info request.
        // Reset only the disposable header's address to the same hashed template.
        await RunTool(runtime.ImgExe, "rebase", "-u", "-f", "qcow2", "-F", "qcow2", "-b", template, disk);
        await RunTool(runtime.ImgExe, "info", "--backing-chain", "--output=json", disk);
        var port = AvailablePorts();
        var args = QemuCommand.Build("whpx", Path.GetRelativePath(runtime.QemuDirectory, disk), 0, port, "fence", null,
            serialLog: Path.GetRelativePath(runtime.QemuDirectory, Path.Combine(root, "serial.log")),
            memoryMb: 2048, cores: 2, firmwareDir: Path.GetRelativePath(runtime.QemuDirectory, runtime.FirmwareDir));
        var elapsed = Stopwatch.StartNew();
        await File.WriteAllTextAsync(Path.Combine(root, "launch-private.json"), JsonSerializer.Serialize(new
        {
            runtime.QemuExe, runtime.QemuDirectory, args, stageCompletedUtc = DateTime.UtcNow,
            scope = "Owned disposable system-audit fixture; no real project or user disk."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, runtime.QemuDirectory, args, out var process),
            "Diagnostic guest launch failed: " + TestUserRunner.LastStartError);
        Assert.NotNull(process);
        using var machine = process!;
        string? cleanupError = null;
        var finished = false;
        var guestShutdownObserved = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            var serial = Path.Combine(root, "serial.log");
            while (true)
            {
                try
                {
                    using var file = new FileStream(serial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(file);
                    if ((await reader.ReadToEndAsync(deadline.Token)).Contains("LP-LYNIS-FINISHED:", StringComparison.Ordinal)) break;
                }
                catch (IOException) { }
                Assert.False(machine.HasExited, "Owned system-audit VM exited before reporting completion; retain its fixture.");
                await Task.Delay(100, deadline.Token);
            }
            finished = true;
        }
        finally
        {
            try
            {
                guestShutdownObserved = MachineShutdown.WaitForGuestExit(machine, port);
                if (!guestShutdownObserved && !machine.HasExited) ConsoleSizeLink.Quit(port);
                if (!machine.WaitForExit(5000))
                {
                    machine.Kill(entireProcessTree: true);
                    if (!machine.WaitForExit(5000)) throw new IOException("Diagnostic VM did not stop; do not open its disk for collection.");
                }
            }
            catch (Exception ex) { cleanupError = ex.Message; }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "lynis-probe-private.json"), JsonSerializer.Serialize(new
            {
                template = template, templateSha256, toolSha256, auditSetupSha256, overlayAdapterSha256,
                launchIdentity = TestUserRunner.UserName,
                disposableOverlay = disk,
                privilegedSystemAuditSetup = true,
                agentSudoGranted = false,
                auditFinishedMarkerObserved = finished,
                guestShutdownObserved,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                cleanupError,
                verdict = "BLOCKED",
                limitations = "Privileged guest-system audit only; standard-agent boundaries and report findings need separate verification/triage. No shipping scanners or template modification."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.Null(cleanupError);
        Assert.True(guestShutdownObserved, "System-audit guest shutdown was incomplete; do not collect an unverified disk.");
        await CollectCompletedAudit(runtime, template, templateSha256, archive, toolSha256, script, root,
            requestedReport, auditSetupSha256, overlayAdapterSha256, collectionOnly: false);
    }

    [EnvironmentFact("LAUNCHPAD_LYNIS_RECOLLECT_ROOT")]
    [Trait("Category", "Integration")]
    public async Task CollectOwnedCompletedGuestSystemAuditWithoutRepeatingTheScan()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_LYNIS_RECOLLECT_ROOT")!);
        Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest") + Path.DirectorySeparatorChar,
            root, StringComparison.OrdinalIgnoreCase);
        var archive = Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_LYNIS")!;
        var requestedReport = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_GUEST_LYNIS_REPORT_PATH")!);
        Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "security") + Path.DirectorySeparatorChar,
            requestedReport, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(requestedReport));
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var template = SecurityTemplate(runtime);
        var templateSha256 = HashFile(template);
        var toolSha256 = HashFile(archive);
        Assert.Equal("b315c848323572500225312de7e9a3bf3b0c462f5b2d6f4ff56fe6ae521ad169", toolSha256);
        var script = Path.Combine(RepositoryRoot(), "scripts", "security-lynis-overlay.sh");
        var setupSha256 = HashFile(Path.Combine(RepositoryRoot(), "scripts", "security-lynis-guest.sh"));
        var adapterSha256 = HashFile(script);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "lynis-probe-private.json")));
        var evidence = document.RootElement;
        Assert.Equal(template, evidence.GetProperty("template").GetString());
        Assert.Equal(templateSha256, evidence.GetProperty("templateSha256").GetString());
        Assert.Equal(toolSha256, evidence.GetProperty("toolSha256").GetString());
        Assert.Equal(setupSha256, evidence.GetProperty("auditSetupSha256").GetString());
        Assert.Equal(adapterSha256, evidence.GetProperty("overlayAdapterSha256").GetString());
        Assert.Equal(TestUserRunner.UserName, evidence.GetProperty("launchIdentity").GetString());
        Assert.Equal(Path.Combine(root, "session.qcow2"), evidence.GetProperty("disposableOverlay").GetString());
        Assert.True(evidence.GetProperty("privilegedSystemAuditSetup").GetBoolean());
        Assert.False(evidence.GetProperty("agentSudoGranted").GetBoolean());
        Assert.True(evidence.GetProperty("auditFinishedMarkerObserved").GetBoolean());
        Assert.True(evidence.GetProperty("guestShutdownObserved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("cleanupError").ValueKind);
        await CollectCompletedAudit(runtime, template, templateSha256, archive, toolSha256, script, root,
            requestedReport, setupSha256, adapterSha256, collectionOnly: true);
    }

    private static async Task CollectCompletedAudit(PublicRuntime runtime, string template, string templateSha256,
        string archive, string toolSha256, string script, string root, string? requestedReport,
        string auditSetupSha256, string overlayAdapterSha256, bool collectionOnly)
    {
        var disk = Path.Combine(root, "session.qcow2");
        Assert.True(FenceFiles.TryResolveUnlinked(root, "session.qcow2", out _));
        var infoStart = new ProcessStartInfo(runtime.ImgExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "info", "--output=json", disk }) infoStart.ArgumentList.Add(argument);
        using (var infoProcess = Process.Start(infoStart)!)
        {
            var infoText = await infoProcess.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(infoProcess.WaitForExit(5000));
            Assert.Equal(0, infoProcess.ExitCode);
            using var info = JsonDocument.Parse(infoText);
            var backing = info.RootElement.GetProperty("backing-filename").GetString()!;
            Assert.Equal(template, Path.GetFullPath(Path.IsPathFullyQualified(backing) ? backing : Path.Combine(root, backing)), ignoreCase: true);
        }
        Assert.Equal(templateSha256, HashFile(template));
        await RunTool(runtime.ImgExe, "rebase", "-u", "-f", "qcow2", "-F", "qcow2", "-b",
            Path.GetRelativePath(root, template).Replace('\\', '/'), disk);
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(script), "collect",
            ToWslPath(disk), ToWslPath(archive), ToWslPath(template), ToWslPath(root));
        Assert.Equal(templateSha256, HashFile(template));
        Assert.Contains("uid=0(root)", await File.ReadAllTextAsync(Path.Combine(root, "identity.txt")), StringComparison.Ordinal);
        Assert.Equal("0", (await File.ReadAllTextAsync(Path.Combine(root, "exit.txt"))).Trim());
        Assert.Contains("production bl-proof process and owned host-gateway rule observed",
            await File.ReadAllTextAsync(Path.Combine(root, "runtime-ready.txt")), StringComparison.Ordinal);
        Assert.Contains("-A OUTPUT -d 10.0.2.2/32 -j REJECT",
            await File.ReadAllTextAsync(Path.Combine(root, "netfilter-private.txt")), StringComparison.Ordinal);
        var report = await File.ReadAllTextAsync(Path.Combine(root, "lynis.dat"));
        Assert.Contains("lynis_version=3.1.7", report, StringComparison.Ordinal);
        Assert.Contains("hardening_index=", report, StringComparison.Ordinal);
        Assert.Contains("tests_executed=", report, StringComparison.Ordinal);
        var executed = Regex.Match(report, @"(?m)^tests_executed=([^\r\n]*)");
        var executedIds = executed.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(executed.Success && executedIds.Length > 0 && executedIds.All(id => Regex.IsMatch(id, @"^[A-Z0-9]+-\d+$")),
            "Lynis must record actual executed test IDs, not only emit help/version text.");
        Assert.Matches(@"(?m)^finish=true\r?$", report);
        var skipped = Regex.Match(report, @"(?m)^tests_skipped=([^\r\n]*)");
        var skippedIds = skipped.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (requestedReport is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(requestedReport)!);
            await File.WriteAllTextAsync(requestedReport, JsonSerializer.Serialize(new
            {
                templateSha256, toolSha256, auditSetupSha256, overlayAdapterSha256,
                toolVersion = "3.1.7",
                candidateUnchanged = true,
                productionRuntimeInitializationObserved = true,
                guestShutdownObserved = true,
                collectionOnly,
                auditIdentity = "uid=0(root)",
                privilegedSystemAuditSetup = true,
                agentSudoGranted = false,
                exitCode = 0,
                testsExecuted = executedIds.Length,
                testsSkipped = skippedIds.Length,
                executedTestIds = executedIds,
                skippedTestIds = skippedIds,
                privateRawReports = root,
                verdict = "UNTRIAGED",
                limitations = "Privileged system audit in a disposable overlay. Tool completion and score do not pass security; unavailable checks and findings require triage."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
