using System.Diagnostics;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class RestrictedQemuDiagnosticTests
{
    [EnvironmentFact("LAUNCHPAD_RESTRICTED_QEMU_DIAGNOSTIC", "1")]
    [Trait("Category", "Integration")]
    public async Task OwnedRestrictedDescendantCapturesQemuVersionAndBootErrors()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest",
            "restricted-qemu-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        RestrictedRuntimeAccess.ModifyDirectory(root);
        var disk = Path.Combine(root, "session.qcow2");
        await GuestBaselineTests.RunTool(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", runtime.KeptImage, disk);
        var port = GuestBaselineTests.AvailablePorts();
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: Path.Combine(root, "serial.log"),
            memoryMb: GuestMemory.DefaultMegabytes, cores: 2, firmwareDir: runtime.FirmwareDir, workingDirectory: runtime.QemuDirectory);
        var config = Path.Combine(root, "configuration.json");
        // Only controlled runtime/owned fixture strings; never user commands.
        var arguments = string.Join(" ", args.Select(value => "\"" + value.Replace("\"", "\\\"") + "\""));
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            exe = runtime.QemuExe, directory = runtime.QemuDirectory,
            probes = new[] { new { name = "version", arguments = "--version" }, new { name = "boot-five-second-diagnostic", arguments } }
        }));
        var source = Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", "owned-restricted-qemu.ps1");
        var script = Path.Combine(root, "probe.ps1");
        File.Copy(source, script);
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(TestUserRunner.TryStart(shell, root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Configuration", config], out var started),
            "Owned diagnostic could not start: " + TestUserRunner.LastStartError);
        using var process = started!;
        try { Assert.True(await Task.Run(() => process.WaitForExit(30000)), "Owned QEMU diagnostic must finish."); }
        finally
        {
            if (!process.WaitForExit(0)) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            TestUserRunner.ReleaseMachine(process.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "diagnostic-owner-private.json"), JsonSerializer.Serialize(new
            {
                root, disk, assemblySha256 = GuestBaselineTests.HashFile(typeof(TestUserRunner).Assembly.Location),
                scriptSha256 = GuestBaselineTests.HashFile(source), exitCode = process.ExitCode,
                limitations = "Owned disposable overlay only. Captures actual restricted descendant stdout/stderr; any surviving boot is stopped after five seconds. Not guest readiness, graceful shutdown, benchmark or agent acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "qemu-diagnostic-private.json")));
        Assert.True(report.RootElement.GetProperty("finished").GetBoolean());
        Assert.Equal(2, report.RootElement.GetProperty("tests").GetArrayLength());
        // This control verifies evidence collection, not that QEMU boot passed.
    }
}
