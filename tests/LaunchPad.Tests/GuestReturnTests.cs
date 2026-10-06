using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

public sealed class GuestReturnTests
{
    [EnvironmentFact("LAUNCHPAD_RETURN_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualGuestReturnsContentEditsWithoutGitMetadataAndLeavesHostUntouched()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var candidate = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_RETURN_TEMPLATE")
            ?? throw new InvalidOperationException("Explicit disposable return candidate required."));
        var reports = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults");
        Assert.True(candidate.StartsWith(reports + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(runtime.KeptImage, StringComparison.OrdinalIgnoreCase), "Owned candidate or exact selected template required.");
        var nativeApply = Environment.GetEnvironmentVariable("LAUNCHPAD_RETURN_APPLY_NATIVE") == "1";
        await Run("icacls.exe", candidate, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        var mainHash = GuestBaselineTests.HashFile(runtime.KeptImage);
        var candidateHash = GuestBaselineTests.HashFile(candidate);
        var root = Path.Combine(reports, "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var project = Path.Combine(root, "fixture");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "edit.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(project, "unchanged.txt"), "keep");
        var disk = Path.Combine(root, "session.qcow2");
        await Run(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", candidate, disk);
        var port = GuestBaselineTests.AvailablePorts();
        var args = QemuCommand.Build("whpx", disk, 0, port, "fence", null,
            serialLog: Path.Combine(root, "serial.log"), memoryMb: 4096, cores: 2,
            firmwareDir: runtime.FirmwareDir, workingDirectory: runtime.QemuDirectory);
        Assert.True(TestUserRunner.TryStart(runtime.QemuExe, runtime.QemuDirectory, args, out var started),
            "Launch identity error: " + TestUserRunner.LastStartError);
        using var machine = started!;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        StatusLink? status = null;
        ConsoleSizeLink? sizing = null;
        Task? capture = null;
        var output = new StringBuilder();
        var complete = false;
        var shutdown = false;
        ProjectReturnReceipt? receipt = null;
        ReturnRecovery? recovery = null;
        SentManifest? baseline = null;
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
                var programText = """
                    #!/usr/bin/python3
                    import os, pathlib, sys
                    from hashlib import sha256
                    path=pathlib.Path('edit.txt')
                    stamp=path.stat()
                    path.write_bytes(b'new')
                    os.utime(path, ns=(stamp.st_atime_ns, stamp.st_mtime_ns))
                    pathlib.Path('empty.txt').write_bytes(b'')
                    pathlib.Path('large.dat').write_bytes(b'x'*131073)
                    for name in ['.git/config','nested/.GIT/config']:
                        metadata=pathlib.Path(name)
                        metadata.parent.mkdir(parents=True, exist_ok=True)
                        metadata.write_bytes(b'VM-only metadata')
                    print('RETURN-UID:'+str(os.getuid()),flush=True)
                    print('RETURN-READY',flush=True)
                    input()
                    """ + "\n";
                if (nativeApply) programText = programText.Replace("b'x'*131073", "b'x'*2097275", StringComparison.Ordinal);
                var program = Encoding.UTF8.GetBytes(programText);
                var stream = fence.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("AGENT custom\nAGENT-CMD lp-return\nPROGRAM " + program.Length + " lp-return\n"), stop.Token);
                await stream.WriteAsync(program, stop.Token);
                baseline = await FenceHost.SendProjectAsync(stream, project, null, null, stop.Token);
                baseline.Save(Path.Combine(root, "sent.manifest"));
            }
            status = new StatusLink(await Connect(QemuCommand.StatusPort(port), machine, stop.Token));
            await GuestBaselineTests.WaitForSerialMarker(Path.Combine(root, "serial.log"), "DOOR-READY", stop.Token);
            await WaitOutput("RETURN-READY", output, stop.Token);
            using var returned = await Connect(QemuCommand.FencePort(port), machine, stop.Token);
            recovery = ReturnRecovery.Create(root);
            var receive = Task.Run(() => ProjectPull.Receive(returned.GetStream(), recovery.Payload, TimeSpan.FromSeconds(30)));
            await console.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\n"), stop.Token);
            receipt = await receive.WaitAsync(stop.Token);
            recovery.SaveTransfer(project, receipt);
            Assert.True(receipt.Complete, receipt.Error);
            Assert.True(receipt.HasContentIdentities);
            Assert.Equal(new[] { "edit.txt", "empty.txt", "large.dat" }, receipt.Files.Order(StringComparer.Ordinal));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(recovery.Payload, "edit.txt")));
            Assert.Equal(nativeApply ? 2097275 : 131073, new FileInfo(Path.Combine(recovery.Payload, "large.dat")).Length);
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(project, "edit.txt")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(project, "unchanged.txt")));
            Assert.Contains("RETURN-UID:1000", Output(output));
            await GuestBaselineTests.WaitForSerialMarker(Path.Combine(root, "serial.log"), "RETURN-COMPLETE", stop.Token);
            complete = true;
        }
        finally
        {
            status?.Dispose();
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
                try { await capture; }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            TestUserRunner.ReleaseMachine(machine.Id);
            await File.WriteAllTextAsync(Path.Combine(root, "return-output-private.txt"), Output(output));
            await File.WriteAllTextAsync(Path.Combine(root, "return-private.json"), JsonSerializer.Serialize(new
            {
                complete, shutdown, root, project, candidate, candidateHash, mainHash, receipt,
                finalCandidateHash = GuestBaselineTests.HashFile(candidate), finalMainHash = GuestBaselineTests.HashFile(runtime.KeptImage),
                limitations = "Owned custom program on a fresh disposable overlay; actual return reception without host apply, Git operations, credentials or full confinement/durability acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown, "Owned guest shutdown must be observed.");
        Assert.Equal(mainHash, GuestBaselineTests.HashFile(runtime.KeptImage));
        Assert.Equal(candidateHash, GuestBaselineTests.HashFile(candidate));
        if (nativeApply)
        {
            Assert.NotNull(receipt); Assert.NotNull(recovery); Assert.NotNull(baseline);
            baseline.Save(recovery.Manifest);
            var diskBeforeApply = GuestBaselineTests.HashFile(disk);
            var receiptBefore = GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json"));
            var scanner = new NativeScanner();
            var backups = 0;
            var result = await ReturnApplier.ApplyAsync(project, recovery, receipt, baseline, scanner,
                () => { backups++; return Task.FromResult(true); });
            await File.WriteAllTextAsync(Path.Combine(root, "native-return-apply-private.json"), JsonSerializer.Serialize(new {
                result, backups, scanner.Results, diskBeforeApply, diskAfterApply = GuestBaselineTests.HashFile(disk),
                receiptBefore, receiptAfter = GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json")),
                assemblySha256 = GuestBaselineTests.HashFile(typeof(WindowsAmsiScanner).Assembly.Location),
                candidateHash, candidate, shutdown,
                limitations = "Actual unprivileged guest edits/export, normal shutdown and native scanned apply only to owned host fixture. Git metadata excluded, recovery/prior content retained, real owned VM disk unchanged by host apply. Backup callback injected; no Git writes, real project, credentials, UI acceptance or complete confinement/AV coverage."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(result.Applied, result.Message); Assert.Equal(1, backups);
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(project, "edit.txt")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(project, "unchanged.txt")));
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(recovery.DirectoryPath, "previous", "edit.txt")));
            Assert.Equal(0, new FileInfo(Path.Combine(project, "empty.txt")).Length);
            Assert.Equal(2097275, new FileInfo(Path.Combine(project, "large.dat")).Length);
            Assert.Equal(diskBeforeApply, GuestBaselineTests.HashFile(disk));
            Assert.Equal(receiptBefore, GuestBaselineTests.HashFile(Path.Combine(recovery.DirectoryPath, "transfer.json")));
            Assert.False(Directory.Exists(Path.Combine(project, ".git")));
            Assert.All(scanner.Results, item => Assert.Equal(FileScanVerdict.Allowed, item.Verdict));
        }
    }

    private sealed class NativeScanner : IReturnFileScanner
    {
        public List<NativeFileScanResult> Results { get; } = new();
        public FileScanVerdict Scan(Stream input, string name) { var result = WindowsAmsiScanner.Scan(input, name); Results.Add(result); return result.Verdict; }
    }
}
