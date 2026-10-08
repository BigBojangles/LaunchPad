using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;

var inputRoot = args[0];
var qemuRoot = args[1];
var scratch = args[2];
Directory.CreateDirectory(scratch);
var qemu = Path.Combine(qemuRoot, "fence", "qemu-system-x86_64.exe");
var img = Path.Combine(qemuRoot, "qemu-img.exe");
var firmware = Path.Combine(qemuRoot, "share");
var backing = Path.Combine(inputRoot, "runtime-policy2-standalone.qcow2");
var images = Path.Combine(scratch, "images"); Directory.CreateDirectory(images);
RuntimeImageFile CopyAsset(string name)
{
    var output = Path.Combine(images, name);
    File.Copy(Path.Combine(inputRoot, "boot", name), output, false);
    using var file = File.OpenRead(output);
    return new(name, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
}
var metadata = new DirectBootManifest("6.1.0-53-amd64", CopyAsset("vmlinuz-6.1.0-53-amd64"), CopyAsset("initrd.img-6.1.0-53-amd64"));
var runtime = new RuntimeImageManifest(1, "direct-boot-policy2-proof", new("debian-12-builder-proof.qcow2", new string('0', 64)), [], [], DirectBoot: metadata);
var boot = RuntimeBoot.Read(scratch, runtime)!;
string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
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
var samples = new List<double>();
for (var n = 1; n <= 3; n++)
{
    var directory = Path.Combine(scratch, "timing-" + n); Directory.CreateDirectory(directory);
    var disk = Path.Combine(directory, "session.qcow2"); await CreateDisk(disk);
    var serial = Path.Combine(directory, "serial.log"); var port = Port();
    var command = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: serial, memoryMb: 2048, cores: 2,
        firmwareDir: firmware, workingDirectory: directory, directBoot: boot);
    File.WriteAllText(Path.Combine(directory, "command.json"), JsonSerializer.Serialize(new { exe = qemu, cwd = directory, args = command }));
    var summary = BootRunV3.Run(qemu, string.Join(" ", command.Select(Quote)), directory,
        new[] { "file:" + serial }, new[] { 0 }, new[] { "IMPORT-READY" }, 30000, 0, Path.Combine(directory, "boot"));
    Console.WriteLine(summary);
    var row = summary.Split('\n').Single(x => x.StartsWith("marker\tIMPORT-READY\t"));
    var milliseconds = double.Parse(row.Trim().Split('\t')[2], System.Globalization.CultureInfo.InvariantCulture);
    if (milliseconds < 0) throw new IOException("IMPORT-READY not observed within timing bound.");
    samples.Add(milliseconds / 1000);
}
var sorted = samples.Order().ToArray();
File.WriteAllText(Path.Combine(scratch, "timing.json"), JsonSerializer.Serialize(new { samplesSeconds = samples, medianSeconds = sorted[1], minSeconds = sorted[0], maxSeconds = sorted[2], rangeSeconds = sorted[2] - sorted[0], baselineMedianSeconds = 14.78, kernel = metadata }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"TIMING median={sorted[1]:F3}s range={sorted[0]:F3}-{sorted[2]:F3}s");

var project = Path.Combine(scratch, "project"); Directory.CreateDirectory(project);
File.WriteAllText(Path.Combine(project, "hello.txt"), "hello-direct-boot");
var program = Path.Combine(scratch, "boot-proof-agent");
File.WriteAllText(program, "#!/usr/bin/python3\nimport pathlib,sys,os\np=pathlib.Path.cwd()\nprint('LP-PROOF:IMPORT:'+p.joinpath('hello.txt').read_text(),flush=True)\nprint('LP-PROOF:PERSIST:'+(p.joinpath('saved.txt').read_text() if p.joinpath('saved.txt').exists() else 'NONE'),flush=True)\nfor line in sys.stdin:\n if line.startswith('WRITE '):\n  token=line.strip().split(' ',1)[1]\n  with open('saved.txt','w') as f:\n   f.write(token);f.flush();os.fsync(f.fileno())\n  print('LP-PROOF:SAVED:'+token,flush=True)\n elif line.strip()=='EXIT':\n  print('LP-PROOF:EXIT',flush=True);break\n");
var session = Path.Combine(scratch, "workflow"); Directory.CreateDirectory(session);
var ownedDisk = Path.Combine(session, "session.qcow2"); await CreateDisk(ownedDisk);
File.WriteAllText(Path.Combine(session, "session-runtime.json"), JsonSerializer.Serialize(new { version = runtime.Version, directBoot = metadata }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
var token = Guid.NewGuid().ToString("N");
var results = new Dictionary<string, object?> { ["scope"] = "Owned custom-program transport/workspace proof; not vendor agent, GUI, restricted-account or save-copyback acceptance", ["attempt"] = 1, ["startedUtc"] = DateTime.UtcNow, ["import"] = false, ["terminal"] = false, ["statusSizeReceipt"] = false, ["shutdown"] = false, ["reopenPersistedWork"] = false };
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
        try
        {
            using var tui = await Connect(QemuCommand.TuiPort(port));
            var terminal = new StringBuilder(); var gate = new object();
            var reader = Task.Run(async () => {
                var buffer = new byte[8192];
                try { int length; while ((length = await tui.GetStream().ReadAsync(buffer, deadline.Token)) > 0) { lock (gate) terminal.Append(Encoding.UTF8.GetString(buffer, 0, length)); } }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            });
            bool Seen(string value) { lock (gate) return terminal.ToString().Contains(value); }
            using var sizing = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            sizing?.Send(80, 30);
            await Wait(() => File.Exists(serial) && SerialText(serial).Contains("IMPORT-READY"), "IMPORT-READY");
            using (var fence = await Connect(QemuCommand.FencePort(port)))
                await FenceHost.SendProjectAsync(fence.GetStream(), project, null, null, deadline.Token,
                    new AgentLaunch(AgentChoice.Custom, "boot-proof-agent", program), restoreGuestState: true, restoreHostHome: false, preserveRepositoryMetadata: true);
            using var status = new StatusLink(await Connect(QemuCommand.StatusPort(port)));
            status.SendWinsize(30, 80);
            await Wait(() => Seen("LP-PROOF:IMPORT:hello-direct-boot"), "import/terminal output");
            results["import"] = true;
            if (n == 1)
            {
                await tui.GetStream().WriteAsync(Encoding.ASCII.GetBytes("WRITE " + token + "\n"), deadline.Token);
                await Wait(() => Seen("LP-PROOF:SAVED:" + token), "terminal input acknowledgement");
                results["terminal"] = true;
                await Wait(() => status.SizeAccepted, "status SIZE-OK"); results["statusSizeReceipt"] = true;
            }
            else
            {
                await Wait(() => Seen("LP-PROOF:PERSIST:" + token), "persisted saved work on reopen");
                results["reopenPersistedWork"] = true;
            }
            lock (gate) File.WriteAllText(Path.Combine(directory, "terminal.txt"), terminal.ToString());
            sizing?.Dispose();
            if (!ConsoleSizeLink.RequestPowerDown(port)) throw new IOException("ACPI request failed.");
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0 || !SerialText(serial).Contains("Power down")) throw new IOException("Clean guest power-down not confirmed.");
            results["shutdown"] = true;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); results["forcedStop"] = true; }
            File.WriteAllText(Path.Combine(directory, "stderr.txt"), await errors);
        }
    }
}
catch (Exception error) { results["error"] = error.ToString(); }
File.WriteAllText(Path.Combine(scratch, "workflow.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(results));