using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

public sealed class GuestPolicyTests
{
    [EnvironmentFact("LAUNCHPAD_AGENT_POLICY_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task ExactGuestRecordsLoadedPolicyAndActualBundledExecutableIdentities()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var selectedHash = HashFile(runtime.KeptImage);
        var template = Environment.GetEnvironmentVariable("LAUNCHPAD_POLICY_TEMPLATE") ?? runtime.KeptImage;
        var confined = Environment.GetEnvironmentVariable("LAUNCHPAD_POLICY_CONFINED") == "1";
        if (template != runtime.KeptImage)
        {
            Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
                Path.GetFullPath(template), StringComparison.OrdinalIgnoreCase);
            await RunTool("icacls.exe", template, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        }
        var expected = HashFile(template);
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var disk = Path.Combine(root, "session.qcow2");
        var adapter = Path.Combine(RepositoryRoot(), "scripts", "guest-policy-probe.sh");
        var sourceIdentities = new[] { adapter, Path.Combine(RepositoryRoot(), "scripts", "diagnose-guest-policy.sh"),
            Path.Combine(RepositoryRoot(), "scripts", "observe-guest-policy.py"), Path.Combine(RepositoryRoot(), "scripts", "guest-policy-boundary.py")
        }.ToDictionary(path => Path.GetFileName(path)!, HashFile);
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(adapter), "stage", ToWslPath(disk), ToWslPath(template), expected, ToWslPath(root));
        var port = AvailablePorts();
        var serial = Path.Combine(root, "serial.log");
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: serial, memoryMb: 2048, cores: 2,
            firmwareDir: runtime.FirmwareDir, workingDirectory: root);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, root, args, out var started), "Owned policy VM error: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var output = new StringBuilder();
        Task? capture = null;
        var complete = false;
        var shutdown = false;
        try
        {
            using var console = await Connect(QemuCommand.TuiPort(port), machine, deadline.Token);
            using var size = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            size?.Send(80, 30);
            capture = Task.Run(async () =>
            {
                var bytes = new byte[4096];
                while (true)
                {
                    var count = await console.GetStream().ReadAsync(bytes, deadline.Token);
                    if (count == 0) break;
                    lock (output) output.Append(Encoding.UTF8.GetString(bytes, 0, count));
                }
            });
            var project = Path.Combine(root, "fixture");
            Directory.CreateDirectory(project);
            var programPath = Path.Combine(project, "lp-policy-probe");
            var program = """
                #!/bin/sh
                printf 'POLICY-UID:'; id -u
                printf 'POLICY-CUSTOM-LABEL:'; cat /proc/self/attr/current
                for agent in grok codex claude; do
                    printf 'POLICY-AGENT:%s\n' "$agent"
                    timeout 15 "$agent" --version
                    printf 'POLICY-AGENT-EXIT:%s:%s\n' "$agent" "$?"
                done
                printf 'POLICY-GUEST-DONE\n'
                read answer
                """ + "\n";
            // Entry intentionally accepts no CLI arguments in product launches.
            // Version probes call the actual binaries within the same enforced
            // transition, separately from the product's interactive smoke.
            if (confined) program = program.Replace("timeout 15 \"$agent\" --version",
                "timeout 15 aa-exec -p launchpad-agent -- setpriv --no-new-privs -- \"$agent\" --version");
            if (confined)
            {
                await File.WriteAllTextAsync(Path.Combine(project, "boundary.py"), await File.ReadAllTextAsync(
                    Path.Combine(RepositoryRoot(), "scripts", "guest-policy-boundary.py"), deadline.Token), deadline.Token);
                program = program.Replace("printf 'POLICY-GUEST-DONE\\n'",
                    "aa-exec -p launchpad-agent -- setpriv --no-new-privs -- python3 boundary.py\nprintf 'POLICY-GUEST-DONE\\n'");
            }
            await File.WriteAllTextAsync(programPath, program, deadline.Token);
            using (var fence = await Connect(QemuCommand.FencePort(port), machine, deadline.Token))
            {
                var manifest = await FenceHost.SendProjectAsync(fence.GetStream(), project, null, null, deadline.Token,
                    new AgentLaunch(AgentChoice.Custom, "lp-policy-probe", programPath), restoreGuestState: true, restoreHostHome: false);
                manifest.Save(Path.Combine(root, "sent.manifest"));
            }
            using var status = new StatusLink(await Connect(QemuCommand.StatusPort(port), machine, deadline.Token));
            await WaitOutput("POLICY-GUEST-DONE", output, deadline.Token);
            Assert.Contains("POLICY-UID:1000", Output(output));
            foreach (var agent in new[] { "grok", "codex", "claude" }) Assert.Contains("POLICY-AGENT-EXIT:" + agent + ":0", Output(output));
            if (confined) Assert.Contains("POLICY-BOUNDARY-DONE", Output(output));
            await WaitForSerialMarker(serial, "LP-POLICY-SNAPSHOT-DONE", deadline.Token);
            complete = true;
        }
        finally
        {
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown) { ConsoleSizeLink.Quit(port); if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true); }
            deadline.Cancel();
            if (capture is not null) try { await capture; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "policy-probe-private.json"), JsonSerializer.Serialize(new
            {
                complete, shutdown, confined, root, template, templateSha256 = expected, finalTemplateSha256 = HashFile(template), selectedHash,
                sourceIdentities, output = Output(output),
                limitations = "Owned disposable overlay; production boot loads its policy. Root read-only snapshot/observer and owned canary setup are distinguished from actual unprivileged CLI versions and generic Python child permission checks. Generic children are not real agent tool calls. No agent sudo, real project, credentials, host apply, full scanner/security verdict or performance acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown);
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(adapter), "collect", ToWslPath(disk), ToWslPath(template), expected, ToWslPath(root));
        Assert.Contains("uid=0(root)", await File.ReadAllTextAsync(Path.Combine(root, "launchpad-policy-probe", "identity.txt")));
        if (confined)
        {
            var reports = Path.Combine(root, "launchpad-policy-probe");
            Assert.Contains("launchpad-agent (enforce)", await File.ReadAllTextAsync(Path.Combine(reports, "loaded-profiles.txt")));
            Assert.Contains("ActiveState=active", await File.ReadAllTextAsync(Path.Combine(reports, "service-state.txt")));
            Assert.Equal("0", (await File.ReadAllTextAsync(Path.Combine(reports, "parser-exit.txt"))).Trim());
            using var observed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(reports, "processes.json")));
            foreach (var agent in new[] { "/usr/local/bin/grok", "/codex-linux-x64/vendor/x86_64-unknown-linux-musl/bin/codex", "/claude-code-linux-x64/claude" })
            {
                var rows = observed.RootElement.EnumerateArray().Where(row => row.GetProperty("executable").GetString()!.EndsWith(agent, StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(rows);
                Assert.All(rows, row =>
                {
                    Assert.Equal("launchpad-agent (enforce)", row.GetProperty("label").GetString());
                    Assert.Equal("1", row.GetProperty("noNewPrivileges").GetString());
                    Assert.Equal("0000000000000000", row.GetProperty("capabilities").GetString());
                });
            }
        }
        Assert.Equal(expected, HashFile(template));
        Assert.Equal(selectedHash, HashFile(runtime.KeptImage));
    }
}
