using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestResendTests
{
    [EnvironmentFact("LAUNCHPAD_RESEND_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualGuestResendKeepsCustomProgramImportsNestedFilesAndUpdatesBaseline()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var candidate = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_RESEND_TEMPLATE")
            ?? throw new InvalidOperationException("Explicit disposable resend candidate required."));
        var reportRoot = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults");
        Assert.StartsWith(reportRoot + Path.DirectorySeparatorChar, candidate, StringComparison.OrdinalIgnoreCase);
        await Run("icacls.exe", candidate, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        var mainHash = GuestBaselineTests.HashFile(runtime.KeptImage);
        var candidateHash = GuestBaselineTests.HashFile(candidate);
        var root = Path.Combine(reportRoot, "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var project = Path.Combine(root, "fixture");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "README.txt"), "owned resend fixture\n");
        var disk = Path.Combine(root, "session.qcow2");
        await Run(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", candidate, disk);
        var port = GuestBaselineTests.AvailablePorts();
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null,
            serialLog: Path.Combine(root, "serial.log"), memoryMb: 4096, cores: 2,
            firmwareDir: runtime.FirmwareDir, workingDirectory: runtime.QemuDirectory);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, runtime.QemuDirectory, args, out var started), "Launch identity error: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        StatusLink? link = null;
        var output = new StringBuilder();
        Task? capture = null;
        ConsoleSizeLink? sizing = null;
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
                    python3 -c 'import os; assert os.tcgetpgrp(0)==os.getpgrp(); print("RESEND-FOREGROUND:OK",flush=True)' || exit 2
                    if [ -f nested/value.txt ]; then
                        printf 'RESEND-CONTENT:'; cat nested/value.txt
                        printf 'RESEND-UID:'; id -u
                        test -w nested/value.txt && printf 'RESEND-WRITABLE:OK\n'
                    fi
                    printf 'RESEND-READY\n'
                    exec /usr/bin/cat
                    """ + "\n");
                var stream = fence.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("AGENT custom\nAGENT-CMD lp-resend\nPROGRAM " + program.Length + " lp-resend\n"), stop.Token);
                await stream.WriteAsync(program, stop.Token);
                var manifest = await FenceHost.SendProjectAsync(stream, project, null, null, stop.Token);
                manifest.Save(Path.Combine(root, "sent.manifest"));
            }
            // The guest emits its initial status before DOOR-READY; connect in
            // the production handoff order so that status writes can drain.
            var status = await Connect(QemuCommand.StatusPort(port), machine, stop.Token);
            link = new StatusLink(status);
            await GuestBaselineTests.WaitForSerialMarker(Path.Combine(root, "serial.log"), "DOOR-READY", stop.Token);
            LiveSession.Begin(project, root, port, link, machine.Id, AgentChoice.Custom);
            Directory.CreateDirectory(Path.Combine(project, "nested"));
            await File.WriteAllTextAsync(Path.Combine(project, "nested", "value.txt"), "first-value\n");
            Assert.Null(await LiveSession.TrySendAsync(project, stop.Token));
            Assert.Equal(ContentHash("first-value\n"), SentManifest.Load(Path.Combine(root, "sent.manifest")).ContentHash("nested/value.txt"));
            await WaitOutput("RESEND-CONTENT:first-value", output, stop.Token);
            await File.WriteAllTextAsync(Path.Combine(project, "nested", "value.txt"), "second-value\n");
            Assert.Null(await LiveSession.TrySendAsync(project, stop.Token));
            Assert.Equal(ContentHash("second-value\n"), SentManifest.Load(Path.Combine(root, "sent.manifest")).ContentHash("nested/value.txt"));
            await WaitOutput("RESEND-CONTENT:second-value", output, stop.Token);
            await WaitOutput("RESEND-WRITABLE:OK", output, stop.Token);
            using var serialFile = new FileStream(Path.Combine(root, "serial.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var serialReader = new StreamReader(serialFile);
            var serial = await serialReader.ReadToEndAsync(stop.Token);
            Assert.Equal(3, serial.Split("AGENT-PICK custom", StringSplitOptions.None).Length - 1);
            Assert.Equal(2, serial.Split("DOOR-CLOSED", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("DOOR-FAILED", serial);
            Assert.Contains("RESEND-UID:1000", Output(output));
            Assert.Equal(3, Output(output).Split("RESEND-FOREGROUND:OK", StringSplitOptions.None).Length - 1);
            Assert.False(File.Exists(Path.Combine(root, "resend.failed")));
            complete = true;
        }
        finally
        {
            LiveSession.End(project, link);
            link?.Dispose();
            sizing?.Dispose();
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown)
            {
                ConsoleSizeLink.Quit(port);
                if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true);
            }
            stop.Cancel();
            if (capture is not null)
            {
                try { await capture; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "resend-output-private.txt"), Output(output));
            await File.WriteAllTextAsync(Path.Combine(root, "resend-private.json"), JsonSerializer.Serialize(new
            {
                complete, shutdown, root, project, candidate, candidateHash, mainHash,
                finalCandidateHash = GuestBaselineTests.HashFile(candidate), finalMainHash = GuestBaselineTests.HashFile(runtime.KeptImage),
                limitations = "Owned custom program, fresh disposable overlay, two resends. No user auth/home, real project return, bundled interactive agent acceptance, concurrency or complete durability/security proof."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown, "Guest shutdown must be observed before releasing this owned overlay.");
        Assert.Equal(mainHash, GuestBaselineTests.HashFile(runtime.KeptImage));
        Assert.Equal(candidateHash, GuestBaselineTests.HashFile(candidate));
    }

    internal static string Output(StringBuilder output) { lock (output) return output.ToString(); }
    internal static async Task WaitOutput(string marker, StringBuilder output, CancellationToken token)
    {
        while (!Output(output).Contains(marker, StringComparison.Ordinal))
        {
            if (Output(output).Contains("Traceback (most recent call last)", StringComparison.Ordinal))
                throw new IOException("The owned custom probe failed before readiness: " + Output(output));
            await Task.Delay(100, token);
        }
    }
    private static string ContentHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static async Task<TcpClient> Connect(int port, Process machine, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (machine.WaitForExit(0)) throw new IOException("The owned QEMU exited before opening port " + port + ". Exit code: " + machine.ExitCode);
            var client = new TcpClient();
            try { await client.ConnectAsync("127.0.0.1", port, token); return client; }
            catch (SocketException) when (attempt < 100) { client.Dispose(); await Task.Delay(100, token); }
            catch { client.Dispose(); throw; }
        }
    }
    internal static async Task Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }
}
