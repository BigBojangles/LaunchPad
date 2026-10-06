using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestPolicyRefusalTests
{
    [EnvironmentTheory("LAUNCHPAD_POLICY_REFUSAL", "1")]
    [InlineData(AgentChoice.Grok)]
    [InlineData(AgentChoice.Codex)]
    [InlineData(AgentChoice.Claude)]
    [Trait("Category", "Integration")]
    public async Task MissingEnforcedProfileNeverStartsABundledAgentOrCommitsItsImport(string agent)
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var selectedHash = HashFile(runtime.KeptImage);
        var template = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_POLICY_TEMPLATE")
            ?? throw new InvalidOperationException("Explicit owned policy candidate required."));
        Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
            template, StringComparison.OrdinalIgnoreCase);
        var expected = HashFile(template);
        await RunTool("icacls.exe", template, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var disk = Path.Combine(root, "session.qcow2");
        var adapter = Path.Combine(RepositoryRoot(), "scripts", "guest-policy-probe.sh");
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(adapter), "stage", ToWslPath(disk), ToWslPath(template), expected, ToWslPath(root), "missing-profile");
        var previous = new SentManifest();
        previous.Note("saved.txt", 3, 1, new string('a', 64));
        previous.Save(Path.Combine(root, "sent.manifest"));
        var baselineHash = HashFile(Path.Combine(root, "sent.manifest"));
        InitialImport.Begin(root);
        var port = AvailablePorts();
        var serial = Path.Combine(root, "serial.log");
        var arguments = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: serial, memoryMb: 2048, cores: 2,
            firmwareDir: runtime.FirmwareDir, workingDirectory: root);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, root, arguments, out var started), "Owned policy VM error: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var refused = false;
        var shutdown = false;
        try
        {
            using var console = await Connect(QemuCommand.TuiPort(port), machine, deadline.Token);
            using var size = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            size?.Send(80, 30);
            var project = Path.Combine(root, "fixture");
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "owned.txt"), "owned refusal fixture", deadline.Token);
            using (var fence = await Connect(QemuCommand.FencePort(port), machine, deadline.Token))
                await FenceHost.SendProjectAsync(fence.GetStream(), project, null, null, deadline.Token, new AgentLaunch(agent, null, null),
                    restoreGuestState: true, restoreHostHome: false);
            using var status = new StatusLink(await Connect(QemuCommand.StatusPort(port), machine, deadline.Token));
            var error = await Assert.ThrowsAsync<IOException>(() => InitialImport.WaitForMarkerAsync(serial, "DOOR-READY safe-import safe-merge", deadline.Token));
            Assert.Contains("policy", error.Message);
            refused = true;
            Assert.True(SessionGuardian.NeedsRecovery(root));
            Assert.Equal(baselineHash, HashFile(Path.Combine(root, "sent.manifest")));
        }
        finally
        {
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown) { ConsoleSizeLink.Quit(port); if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true); }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "policy-refusal-private.json"), JsonSerializer.Serialize(new
            {
                agent, refused, shutdown, template, templateSha256 = expected, finalTemplateSha256 = HashFile(template),
                selectedHash, baselineHash, finalBaselineHash = HashFile(Path.Combine(root, "sent.manifest")),
                limitations = "Deliberate policy-file absence only in a new owned child. Actual bundled launch request and production readiness barrier. No real saved disk, credentials, host apply, scanner, agent tool or complete security verdict."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown);
        // Read after the owned writer exits; includes the entire final log.
        Assert.DoesNotContain("AGENT-PICK " + agent, await File.ReadAllTextAsync(serial));
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(adapter), "collect", ToWslPath(disk), ToWslPath(template), expected, ToWslPath(root), "missing-profile");
        var reports = Path.Combine(root, "launchpad-policy-probe");
        Assert.Contains("uid=0(root)", await File.ReadAllTextAsync(Path.Combine(reports, "identity.txt")));
        Assert.Equal("completed", (await File.ReadAllTextAsync(Path.Combine(reports, "observer-finished.txt"))).Trim());
        Assert.DoesNotContain("launchpad-agent", await File.ReadAllTextAsync(Path.Combine(reports, "loaded-profiles.txt")));
        var observations = Path.Combine(reports, "processes.json");
        if (File.Exists(observations))
        {
            using var identities = JsonDocument.Parse(await File.ReadAllTextAsync(observations));
            Assert.DoesNotContain(identities.RootElement.EnumerateArray(), row => row.GetProperty("executable").GetString() is { } exe
                && (exe.EndsWith("/grok") || exe.EndsWith("/codex") || exe.EndsWith("/claude")));
        }
        Assert.Equal(expected, HashFile(template));
        Assert.Equal(selectedHash, HashFile(runtime.KeptImage));
    }
}
