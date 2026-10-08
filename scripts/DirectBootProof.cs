using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;

// Use the same verified runtime resolver as the app. All payload and session
// writes remain inside this experimental worktree, never the installed runtime.
var bundle = Path.GetFullPath(args[0]);
var scratch = Path.GetFullPath(args[1]);
if (!scratch.StartsWith(Path.GetDirectoryName(bundle)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new IOException("Proof session must remain inside the worktree.");
if (Directory.Exists(scratch) || File.Exists(scratch)) throw new IOException("Use a fresh proof directory; existing evidence is preserved.");
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("LAUNCHPAD_QEMU", bundle);
var appRuntime = PublicRuntime.Ensure(new LaunchPad.Services.SetupLog(new LaunchPad.Services.AppPaths(appDataDir: Path.Combine(scratch, "appdata"))));
var qemu = appRuntime.QemuExe;
var img = appRuntime.ImgExe;
var firmware = appRuntime.FirmwareDir;
var backing = appRuntime.KeptImage;
var runtime = RuntimeImages.Read(bundle).Manifest!;
var boot = appRuntime.DirectBoot ?? throw new IOException("The app did not resolve the bundled direct-boot assets.");
var metadata = boot.Manifest;
int Port()
{
    // Reserve the whole application port block during selection; QEMU then owns it.
    for (var i = 0; i < 100; i++)
    {
        var first = Random.Shared.Next(48000, 57000); var held = new List<TcpListener>();
        try { for (var n = 0; n < 5; n++) { var l = new TcpListener(IPAddress.Loopback, first + n); l.Start(); held.Add(l); } return first; }
        catch (SocketException) { }
        finally { foreach (var l in held) l.Stop(); }
    }
    throw new IOException("No available owned port block.");
}
async Task CreateDisk(string disk)
{
    if (File.Exists(disk) || Directory.Exists(disk)) throw new IOException("Existing proof disk is preserved.");
    var start = new ProcessStartInfo(img) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scratch };
    foreach (var arg in new[] { "create", "-q", "-f", "qcow2", "-b", backing, "-F", "qcow2", disk }) start.ArgumentList.Add(arg);
    using var p = Process.Start(start)!;
    await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new IOException("Overlay creation failed.");
}
string SerialText(string path)
{
    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(input);
    return reader.ReadToEnd();
}
var project = Path.Combine(scratch, "project"); Directory.CreateDirectory(project);
File.WriteAllText(Path.Combine(project, "hello.txt"), "hello-direct-boot");
var binary = Enumerable.Range(0, 16384).Select(i => (byte)(i % 251)).ToArray();
File.WriteAllBytes(Path.Combine(project, "payload.bin"), binary);
var expectedHash = Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant();
var program = Path.Combine(scratch, "boot-proof-agent");
File.WriteAllText(program, "#!/usr/bin/python3\nimport pathlib,sys,os,hashlib\np=pathlib.Path.cwd()\nprint('LP-PROOF:HASH:'+hashlib.sha256(p.joinpath('payload.bin').read_bytes()).hexdigest(),flush=True)\nprint('LP-PROOF:IMPORT:'+p.joinpath('hello.txt').read_text(),flush=True)\nprint('LP-PROOF:PERSIST:'+(p.joinpath('saved.txt').read_text() if p.joinpath('saved.txt').exists() else 'NONE'),flush=True)\nfor line in sys.stdin:\n if line.startswith('WRITE '):\n  token=line.strip().split(' ',1)[1]\n  with open('saved.txt','w') as f:\n   f.write(token);f.flush();os.fsync(f.fileno())\n  print('LP-PROOF:SAVED:'+token,flush=True)\n elif line.strip()=='EXIT':\n  print('LP-PROOF:EXIT',flush=True);break\n");
var session = Path.Combine(scratch, "workflow"); Directory.CreateDirectory(session);
var ownedDisk = Path.Combine(session, "session.qcow2");
if (RuntimeBoot.ForSession(boot, runtime.Version, session, File.Exists(ownedDisk)) is null)
    throw new IOException("New session did not select direct boot.");
await CreateDisk(ownedDisk);
File.WriteAllText(Path.Combine(session, "session-runtime.json"), JsonSerializer.Serialize(new { version = runtime.Version, directBoot = metadata }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
var token = Guid.NewGuid().ToString("N");
var results = new Dictionary<string, object?> { ["scope"] = "App runtime resolver + normal QEMU/import/status transport; owned custom program; not physical GUI, vendor-agent or restricted-account acceptance", ["attempt"] = 1, ["startedUtc"] = DateTime.UtcNow, ["appResolvedLocalBundle"] = true, ["newSessionDirectBoot"] = true, ["import"] = false, ["terminal"] = false, ["statusSizeReceipt"] = "unsupported-by-policy2", ["shutdown"] = false, ["reopenPersistedWork"] = false };
try
{
    for (var n = 1; n <= 2; n++)
    {
        var directory = Path.Combine(session, "run-" + n); Directory.CreateDirectory(directory);
        var serial = Path.Combine(directory, "serial.log"); var port = Port();
        var selectedBoot = RuntimeBoot.ForSession(boot, runtime.Version, session, true) ?? throw new IOException("Saved matching direct boot record not selected.");
        var command = QemuCommand.Build("whpx", ownedDisk, 0, port, "fence", null, serialLog: serial, memoryMb: 2048, cores: 2, firmwareDir: firmware, workingDirectory: directory, directBoot: selectedBoot);
        File.WriteAllText(Path.Combine(directory, "command.json"), JsonSerializer.Serialize(new { exe = qemu, cwd = directory, args = command }));
        var start = new ProcessStartInfo(qemu) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardError = true };
        foreach (var arg in command) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        async Task Wait(Func<bool> predicate, string label)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate())
            {
                if (process.HasExited || clock.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("Missing workflow marker: " + label);
                await Task.Delay(25, deadline.Token);
            }
        }
        async Task<TcpClient> Connect(int target)
        {
            for (var retry = 0; ; retry++)
            {
                var client = new TcpClient();
                try { await client.ConnectAsync(IPAddress.Loopback, target, deadline.Token); return client; }
                catch (SocketException) when (retry < 100) { client.Dispose(); await Task.Delay(50, deadline.Token); }
            }
        }
        var terminal = new StringBuilder(); var gate = new object();
        try
        {
            // Keep fence connected before guest reads it: waiting for IMPORT-READY
            // first can expose EOF and launch the default Grok on an empty import.
            using var fence = await Connect(QemuCommand.FencePort(port));
            using var tui = await Connect(QemuCommand.TuiPort(port));
            var reader = Task.Run(async () => {
                var buffer = new byte[8192];
                try { int length; while ((length = await tui.GetStream().ReadAsync(buffer, deadline.Token)) > 0) { lock (gate) terminal.Append(Encoding.UTF8.GetString(buffer, 0, length)); } }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            });
            bool Seen(string value) { lock (gate) return terminal.ToString().Contains(value); }
            using var sizing = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            sizing?.Send(80, 30);
            await Wait(() => File.Exists(serial) && SerialText(serial).Contains("IMPORT-READY"), "IMPORT-READY");
                await FenceHost.SendProjectAsync(fence.GetStream(), project, null, null, deadline.Token,
                    new AgentLaunch(AgentChoice.Custom, "boot-proof-agent", program), restoreGuestState: true, restoreHostHome: false, preserveRepositoryMetadata: true);
            fence.Dispose(); // EOF commits the complete import; no bytes are sent after this point.
            using var status = new StatusLink(await Connect(QemuCommand.StatusPort(port)));
            status.SendWinsize(30, 80);
            await Wait(() => Seen("LP-PROOF:IMPORT:hello-direct-boot"), "import/terminal output");
            await Wait(() => Seen("LP-PROOF:HASH:" + expectedHash), "intact binary contents");
            await Wait(() => SerialText(serial).Contains("AGENT-IN custom") && SerialText(serial).Contains("AGENT-PICK custom"), "requested custom agent selected");
            results["import"] = true;
            results["customAgentSelected"] = true;
            if (n == 1)
            {
                await tui.GetStream().WriteAsync(Encoding.ASCII.GetBytes("WRITE " + token + "\n"), deadline.Token);
                await Wait(() => Seen("LP-PROOF:SAVED:" + token), "terminal input acknowledgement");
                results["terminal"] = true;
                await Wait(() => SerialText(serial).Contains("GUEST-SIZE 30 80"), "actual guest resize processing"); results["guestResizeObserved"] = true;
            }
            else
            {
                await Wait(() => Seen("LP-PROOF:PERSIST:" + token), "persisted saved work on reopen");
                results["reopenPersistedWork"] = true;
            }
            lock (gate) File.WriteAllText(Path.Combine(directory, "terminal.txt"), terminal.ToString());
            sizing?.Dispose();
            if (!await Task.Run(() => MachineShutdown.WaitForGuestExit(process, port)))
                throw new IOException("Application clean guest shutdown did not complete within 30 seconds.");
            if (process.ExitCode != 0 || !SerialText(serial).Contains("Power down")) throw new IOException("Clean guest power-down not confirmed.");
            results["shutdown"] = true;
        }
        finally
        {
            lock (gate) File.WriteAllText(Path.Combine(directory, "terminal.txt"), terminal.ToString());
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); results["forcedStop"] = true; }
            File.WriteAllText(Path.Combine(directory, "stderr.txt"), await errors);
        }
    }
}
catch (Exception error) { results["error"] = error.ToString(); }
File.WriteAllText(Path.Combine(scratch, "workflow.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results));
return results.ContainsKey("error") ? 1 : 0;
