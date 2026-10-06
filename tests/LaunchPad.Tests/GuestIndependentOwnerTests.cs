using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestIndependentOwnerTests
{
    [EnvironmentFact("LAUNCHPAD_INDEPENDENT_OWNER_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task TwoActualGuestsKeepResizeAndRecoverReturnsAfterTheirDesktopExits()
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var mainTemplateHash = GuestBaselineTests.HashFile(runtime.KeptImage);
        var template = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_INDEPENDENT_OWNER_TEMPLATE") ?? runtime.KeptImage);
        if (!template.Equals(runtime.KeptImage, StringComparison.OrdinalIgnoreCase))
        {
            var allowed = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar;
            Assert.StartsWith(allowed, template, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("template.qcow2", Path.GetFileName(template));
            await Run("icacls.exe", template, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        }
        var templateHash = GuestBaselineTests.HashFile(template);
        var canonical = Environment.GetEnvironmentVariable("LAUNCHPAD_TERMINAL_CANONICAL") == "1";
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..8]);
        Assert.False(Directory.Exists(root), "The owned fixture must never reuse an existing directory.");
        Directory.CreateDirectory(root);
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var desktopInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        desktopInfo.ArgumentList.Add("/c"); desktopInfo.ArgumentList.Add("pause");
        var nativeDesktop = Environment.GetEnvironmentVariable("LAUNCHPAD_NATIVE_DESKTOP_OWNER") == "1";
        var nativeClose = Environment.GetEnvironmentVariable("LAUNCHPAD_NATIVE_DESKTOP_CLOSE") ?? "click";
        Assert.Contains(nativeClose, new[] { "click", "message" });
        var desktopRoot = Path.Combine(root, "desktop");
        using var desktop = nativeDesktop ? NativeDesktopFixture.Start(desktopRoot) : Process.Start(desktopInfo)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Fixture? first = null; Fixture? second = null;
        var passed = false;
        try
        {
            if (nativeDesktop)
                await WindowsConsoleProbeTests.Until(() => NativeDesktopFixture.Read(desktopRoot)?.HomeReady == true, deadline.Token);
            first = await Fixture.Start(runtime, template, Path.Combine(root, "first"), desktop, canonical, deadline.Token);
            second = await Fixture.Start(runtime, template, Path.Combine(root, "second"), desktop, canonical, deadline.Token);
            Assert.False(first.Machine.HasExited); Assert.False(second.Machine.HasExited);
            Assert.NotEqual(first.Port, second.Port);
            Assert.NotEqual(first.Identity.Generation, second.Identity.Generation);
            // Only this owned short-lived desktop identity ends. Neither real
            // LaunchPad nor any user terminal/project is touched.
            if (nativeDesktop && nativeClose == "message") NativeDesktopInput.CloseWindow(desktop, NativeDesktopFixture.Read(desktopRoot)!);
            else if (nativeDesktop) NativeDesktopInput.Click(desktop, NativeDesktopFixture.Read(desktopRoot)!, "close");
            else desktop.Kill();
            await desktop.WaitForExitAsync(deadline.Token);
            if (nativeDesktop) Assert.Equal(0, desktop.ExitCode);
            await first.ExpectSize(92, 28, deadline.Token);
            await second.ExpectSize(88, 26, deadline.Token);
            Assert.False(first.Owner.HasExited); Assert.False(second.Owner.HasExited);

            first.Type("exit\r");
            await first.WaitScreen("OWNED-EXIT:first", deadline.Token);
            // Controlled transport EOF exercises the actual terminal's
            // AfterConsole path while the GUI identity is gone.
            first.Proxy.CloseConsole();
            await first.Terminal.Process.WaitForExitAsync(deadline.Token);
            await first.Owner.WaitForExitAsync(deadline.Token);
            first.VerifyReturn();
            Assert.False(second.Machine.HasExited); Assert.False(second.Owner.HasExited);
            await second.ExpectSize(90, 27, deadline.Token);

            // Actual abrupt terminal process death uses the guardian's return
            // path, independently of the first session's already-ended owner.
            second.Terminal.Process.Kill();
            await second.Terminal.Process.WaitForExitAsync(deadline.Token);
            await second.Owner.WaitForExitAsync(deadline.Token);
            second.VerifyReturn();
            Assert.True(first.Machine.HasExited); Assert.True(second.Machine.HasExited);
            Assert.Equal("first", File.ReadAllText(Path.Combine(first.Project, "nonce.txt")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(second.Project, "nonce.txt")));
            Assert.False(File.Exists(Path.Combine(first.Project, "guest-only.txt")));
            Assert.False(File.Exists(Path.Combine(second.Project, "guest-only.txt")));
            Assert.Equal(templateHash, GuestBaselineTests.HashFile(template));
            Assert.Equal(mainTemplateHash, GuestBaselineTests.HashFile(runtime.KeptImage));
            passed = true;
        }
        finally
        {
            if (!desktop.HasExited) { desktop.Kill(); await desktop.WaitForExitAsync(); }
            first?.Dispose(); second?.Dispose();
            await File.WriteAllTextAsync(Path.Combine(root, "independent-owners-private.json"), JsonSerializer.Serialize(new
            {
                passed, root, canonical, nativeDesktop, nativeClose, template, templateHash, mainTemplate = runtime.KeptImage, mainTemplateHash,
                finalTemplateHash = GuestBaselineTests.HashFile(template), finalMainTemplateHash = GuestBaselineTests.HashFile(runtime.KeptImage),
                first = first?.Report(), second = second?.Report(),
                limitations = "Two actual concurrent owned custom-program VMs, recorded input mode, production native terminal/guardian and return paths. nativeDesktop records actual owned Avalonia close versus a departed process fixture. Native desktop uses private records and supplied setup result; VMs are independently started fixtures, not UI-launched sessions. Controlled terminal EOF and actual terminal death. No user credentials/projects, bundled concurrency, host apply, full app-to-agent focus, power-loss, benchmark or security acceptance. All disks/reports preserved."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Project { get; }
        public int Port { get; private set; }
        public Process Machine { get; private set; } = null!;
        public Process Owner { get; private set; } = null!;
        public HiddenConsole Terminal { get; private set; } = null!;
        public ConsoleProxy Proxy { get; private set; } = null!;
        public SessionOwnerIdentity Identity { get; private set; } = null!;
        private string _screen = "";
        private string? _result;
        private readonly List<string> _sizes = [];
        private string? _cleanupError;
        private string? _bundledAgent;
        private string? _fixtureId;
        private readonly Dictionary<string, string> _supplied = [];
        private GuestAgentToolsTests.OwnedHostListener? _hostListener;
        private Fixture(string root) { Root = root; Project = Path.Combine(root, "fixture"); }
        public static async Task<Fixture> Start(PublicRuntime runtime, string template, string root, Process desktop, bool canonical, CancellationToken token,
            string? bundledAgent = null, string? childFault = null)
        {
            var fixture = new Fixture(root);
            try
            {
                Directory.CreateDirectory(fixture.Project);
                var name = Path.GetFileName(root);
                await File.WriteAllTextAsync(Path.Combine(fixture.Project, "nonce.txt"), name, token);
                var programPath = Path.Combine(root, "lp-independent");
                var program = canonical ? """
                    #!/usr/bin/python3
                    import os,pathlib,termios
                    name=pathlib.Path('nonce.txt').read_text()
                    foreground=os.tcgetpgrp(0)
                    assert foreground==os.getpgrp(), 'Agent must own its foreground terminal'
                    assert termios.tcgetattr(0)[3]&termios.ISIG, 'Canonical signals must be enabled'
                    assert termios.tcgetattr(0)[3]&termios.ICANON, 'Canonical input must be enabled'
                    pathlib.Path('guest-only.txt').write_text('guest-'+name)
                    print('OWNED-TTY-FG:'+str(foreground)+':PGRP:'+str(os.getpgrp()),flush=True)
                    print('OWNED-READY:'+name+':UID:'+str(os.getuid()),flush=True)
                    try:
                        while True:
                            command=input()
                            if command=='size':
                                size=os.get_terminal_size(1)
                                print('OWNED-SIZE:'+name+':'+str(size.lines)+' '+str(size.columns),flush=True)
                            elif command=='exit': break
                    except KeyboardInterrupt:
                        print('OWNED-SIGINT:'+name,flush=True)
                    print('OWNED-EXIT:'+name,flush=True)
                    """ : """
                    #!/usr/bin/python3
                    import os,pathlib,termios,tty
                    name=pathlib.Path('nonce.txt').read_text()
                    pathlib.Path('guest-only.txt').write_text('guest-'+name)
                    def report(message): print(message,end='\r\n',flush=True)
                    original=termios.tcgetattr(0)
                    report('OWNED-TTY-LFLAGS:'+str(original[3]))
                    try: report('OWNED-TTY-FG:'+str(os.tcgetpgrp(0)))
                    except OSError as error: report('OWNED-TTY-FG-ERROR:'+str(error.errno))
                    # A terminal UI consumes Ctrl+C bytes itself. Canonical
                    # signal behavior remains a separate, recorded guest gap.
                    tty.setraw(0)
                    report('OWNED-READY:'+name+':UID:'+str(os.getuid()))
                    command=''
                    try:
                        while True:
                            character=os.read(0,1)
                            if not character or character==b'\x03': break
                            if character in (b'\r',b'\n'):
                                if command=='size':
                                    size=os.get_terminal_size(1)
                                    report('OWNED-SIZE:'+name+':'+str(size.lines)+' '+str(size.columns))
                                elif command=='exit': break
                                command=''
                            else: command+=character.decode('ascii')
                    finally: termios.tcsetattr(0,termios.TCSADRAIN,original)
                    report('OWNED-EXIT:'+name)
                    """;
                if (bundledAgent is not null)
                {
                    Assert.Contains(bundledAgent, new[] { "grok", "codex", "claude" });
                    Assert.True(canonical);
                    fixture._bundledAgent = bundledAgent;
                    fixture._fixtureId = Guid.NewGuid().ToString("N");
                    fixture._hostListener = new GuestAgentToolsTests.OwnedHostListener();
                    var control = JsonSerializer.Serialize(new { agent = bundledAgent, fixtureId = fixture._fixtureId, nonce = name, fault = childFault });
                    var targets = JsonSerializer.Serialize(new { gatewayAddress = "10.0.2.2", hostPort = fixture._hostListener.Port });
                    await File.WriteAllTextAsync(Path.Combine(root, "owned_terminal.json"), control, token);
                    await File.WriteAllTextAsync(Path.Combine(fixture.Project, "owned_terminal.json"), control, token);
                    await File.WriteAllTextAsync(Path.Combine(root, "owned_targets.json"), targets, token);
                    foreach (var script in new[] { "guest-agent-terminal.py", "owned-model-fixture.py", "owned-agent-interruption.py" })
                    {
                        var source = Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", script);
                        var target = Path.Combine(fixture.Project, script == "owned-model-fixture.py" ? "owned_model_fixture.py" : script);
                        await File.WriteAllBytesAsync(target, await File.ReadAllBytesAsync(source, token), token);
                        fixture._supplied[script] = GuestBaselineTests.HashFile(target);
                    }
                    foreach (var script in new[] { "guest-policy-probe.sh", "observe-guest-policy.py", "diagnose-guest-policy.sh", "owned-agent-egress.sh" })
                        fixture._supplied[script] = GuestBaselineTests.HashFile(Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", script));
                    program = "#!/bin/sh\nexec python3 guest-agent-terminal.py\n";
                    var adapter = Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", "guest-policy-probe.sh");
                    await GuestBaselineTests.RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", GuestBaselineTests.ToWslPath(adapter), "stage",
                        GuestBaselineTests.ToWslPath(Path.Combine(root, "session.qcow2")), GuestBaselineTests.ToWslPath(template),
                        GuestBaselineTests.HashFile(template), GuestBaselineTests.ToWslPath(root), "agent-terminal");
                }
                else
                    await Run(runtime.ImgExe, "create", "-f", "qcow2", "-F", "qcow2", "-b", template, Path.Combine(root, "session.qcow2"));
                await File.WriteAllTextAsync(programPath, program + "\n", token);
                fixture.Port = GuestBaselineTests.AvailablePorts(PortChoice.First, PortChoice.Last);
                var args = QemuCommand.Build("whpx", Path.Combine(root, "session.qcow2"), 0, fixture.Port, "fence", null,
                    serialLog: Path.Combine(root, "serial.log"), memoryMb: bundledAgent is null ? 2048 : 4096, cores: 2,
                    firmwareDir: runtime.FirmwareDir, workingDirectory: root);
                await File.WriteAllTextAsync(Path.Combine(root, "launch-private.json"), JsonSerializer.Serialize(new
                { runtime.QemuExe, runtime.QemuDirectory, workingDirectory = root, args }, new JsonSerializerOptions { WriteIndented = true }), token);
                Assert.True(TestUserRunner.TryStart(runtime.QemuExe, root, args, out var machine), "Launch identity error: " + TestUserRunner.LastStartError);
                fixture.Machine = machine!;
                File.WriteAllText(Path.Combine(root, "qemu.pid"), machine!.Id.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(root, "qmp.txt"), fixture.Port.ToString(CultureInfo.InvariantCulture));
                var generation = Guid.NewGuid().ToString("N");
                var ownerInfo = new ProcessStartInfo(WindowsConsoleProbeTests.LaunchPadExecutable()) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { SessionGuardian.Argument, root, machine.Id.ToString(CultureInfo.InvariantCulture),
                    machine.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), fixture.Port.ToString(CultureInfo.InvariantCulture),
                    generation, bundledAgent ?? AgentChoice.Custom, fixture.Project, desktop.Id.ToString(CultureInfo.InvariantCulture),
                    desktop.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) }) ownerInfo.ArgumentList.Add(arg);
                fixture.Owner = Process.Start(ownerInfo)!;
                await WindowsConsoleProbeTests.Until(() => SessionGuardian.TryReadLiveOwner(root)?.Generation == generation, token);
                Assert.True(TestUserRunner.HandMachineTo(machine.Id, fixture.Owner.Id));
                File.WriteAllText(Path.Combine(root, "session-owner.armed"), JsonSerializer.Serialize(generation));
                fixture.Proxy = new ConsoleProxy(QemuCommand.TuiPort(fixture.Port), machine);
                fixture.Terminal = new HiddenConsole(root, fixture.Proxy.Port, attach: false);
                using (var sizing = await ConsoleSizeLink.ConnectAsync(fixture.Port, token))
                {
                    Assert.NotNull(sizing); sizing.Send(100, 30);
                    using (var fence = await Connect(QemuCommand.FencePort(fixture.Port), machine, token))
                    {
                        var manifest = await FenceHost.SendProjectAsync(fence.GetStream(), fixture.Project, null, null, token,
                            new(AgentChoice.Custom, "lp-independent", programPath), restoreGuestState: true, restoreHostHome: false,
                            preserveRepositoryMetadata: false);
                        manifest.Save(Path.Combine(root, "sent.manifest"));
                    }
                    using var status = new StatusLink(await Connect(QemuCommand.StatusPort(fixture.Port), machine, token));
                    await GuestBaselineTests.WaitForSerialMarker(Path.Combine(root, "serial.log"), "DOOR-READY", token);
                    if (bundledAgent is null) await fixture.WaitScreen("OWNED-READY:" + name + ":UID:1000", token);
                    else
                    {
                        await GuestBaselineTests.WaitForSerialMarker(Path.Combine(root, "serial.log"), "OWNED-TERMINAL-CHILD-READY:" + bundledAgent, token);
                        await File.WriteAllTextAsync(Path.Combine(root, "screen-private.txt"), fixture.Screen(), token);
                    }
                    await WindowsConsoleProbeTests.Until(() => SessionGuardian.TryReadLiveOwner(root)?.TerminalPid == fixture.Terminal.Process.Id, token);
                    fixture.Identity = SessionGuardian.TryReadLiveOwner(root)!;
                }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        private string Screen()
        {
            using var view = HiddenConsole.AttachTo(Terminal.Process);
            _screen = view.ReadScreen(viewportOnly: true); return _screen;
        }
        public async Task WaitScreen(string marker, CancellationToken token)
        {
            try { await WindowsConsoleProbeTests.Until(() => CheckedScreen().Contains(marker, StringComparison.Ordinal), token); }
            finally { await File.WriteAllTextAsync(Path.Combine(Root, "screen-private.txt"), _screen); }
        }
        public void Type(string input) { using var view = HiddenConsole.AttachTo(Terminal.Process); view.Type(input); }
        public async Task ExpectSize(short columns, short rows, CancellationToken token)
        {
            using (var view = HiddenConsole.AttachTo(Terminal.Process)) view.Resize(columns, rows);
            await WindowsConsoleProbeTests.Until(() => ReadShared(Path.Combine(Root, "winsize.txt")) == rows + " " + columns, token);
            var marker = "OWNED-SIZE:" + Path.GetFileName(Root) + ":" + rows + " " + columns;
            // Query repeatedly while the owner takes over the former desktop's
            // QMP connection; readiness and eventual size are observed in Linux.
            while (!CheckedScreen().Contains(marker, StringComparison.Ordinal)) { Type("size\r"); await Task.Delay(250, token); }
            _sizes.Add(marker);
            Assert.False(Machine.HasExited);
            await File.WriteAllTextAsync(Path.Combine(Root, "screen-private.txt"), _screen, token);
        }
        public void VerifyReturn()
        {
            _result = File.ReadAllText(Path.Combine(Root, SessionGuardian.ResultFile));
            using var document = JsonDocument.Parse(_result);
            Assert.True(document.RootElement.GetProperty("guestShutdownObserved").GetBoolean());
            Assert.False(document.RootElement.GetProperty("needsRecovery").GetBoolean());
            var recovery = ReturnRecovery.Open(document.RootElement.GetProperty("recoveryDirectory").GetString()!);
            var receipt = recovery.LoadTransfer(Project);
            Assert.True(receipt.Complete); Assert.True(receipt.HasContentIdentities);
            Assert.Contains("guest-only.txt", receipt.Files);
            Assert.Equal("guest-" + Path.GetFileName(Root), File.ReadAllText(Path.Combine(recovery.Payload, "guest-only.txt")));
            if (_bundledAgent is not null)
            {
                Assert.Contains("agent-progress.txt", receipt.Files);
                var final = File.ReadAllText(Path.Combine(Root, "final-agent-progress.txt"));
                Assert.Equal(final, File.ReadAllText(Path.Combine(recovery.Payload, "agent-progress.txt")));
                var stopped = _bundledAgent + ":" + _fixtureId + ":stopped\n";
                if (_bundledAgent != "codex" || final == stopped)
                {
                    Assert.Equal(stopped, final);
                    Assert.Contains("fixture-output/tool-interrupted.json", receipt.Files);
                }
                else
                {
                    // Codex can kill its own command without invoking its TERM
                    // handler. Never invent an unperformed shutdown write.
                    Assert.Equal(_bundledAgent + ":" + _fixtureId + ":partial\n", final);
                    Assert.False(File.Exists(Path.Combine(Root, "fixture-output", "tool-interrupted.json")));
                }
                Assert.False(File.Exists(Path.Combine(Project, "agent-progress.txt")));
                Assert.True(_hostListener!.LiveWitness);
                Assert.Equal(0, _hostListener.Connections);
            }
            Assert.DoesNotContain("nonce.txt", receipt.Files); // Unchanged baseline files are not returned.
            Assert.Contains("reboot: Power down", ReadShared(Path.Combine(Root, "serial.log")));
            Assert.True(SessionCompletion.Returned(Root, Identity));
            Assert.True(SessionCompletion.ShutdownObserved(Root, Identity));
        }
        public object Report() => new { Root, Project, Port, identity = Identity, _bundledAgent, _fixtureId, supplied = _supplied, _sizes, _result, _cleanupError };
        public async Task VerifyBundledWitness(string template)
        {
            Assert.NotNull(_bundledAgent);
            Assert.True(Machine.HasExited);
            await GuestBaselineTests.RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash",
                GuestBaselineTests.ToWslPath(Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", "guest-policy-probe.sh")), "collect",
                GuestBaselineTests.ToWslPath(Path.Combine(Root, "session.qcow2")), GuestBaselineTests.ToWslPath(template),
                GuestBaselineTests.HashFile(template), GuestBaselineTests.ToWslPath(Root), "agent-terminal");
            using var witness = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root, "launchpad-policy-probe", "terminal-witness.json")));
            Assert.Equal(0, witness.RootElement.GetProperty("rootUid").GetInt32());
            Assert.Equal(_fixtureId, witness.RootElement.GetProperty("expected").GetProperty("fixtureId").GetString());
            Assert.Equal(_bundledAgent, witness.RootElement.GetProperty("expected").GetProperty("agent").GetString());
            var observed = witness.RootElement.GetProperty("observed").EnumerateArray().ToArray();
            Assert.True(observed.Length >= 2);
            Assert.All(observed, row =>
            {
                Assert.Equal("launchpad-agent (enforce)", row.GetProperty("label").GetString());
                Assert.Equal("1", row.GetProperty("noNewPrivileges").GetString());
                Assert.Equal("0000000000000000", row.GetProperty("capabilities").GetString());
                Assert.All(row.GetProperty("uid").EnumerateArray(), value => Assert.Equal(1000, value.GetInt32()));
            });
            var native = observed[^1];
            Assert.EndsWith("/" + _bundledAgent, native.GetProperty("executable").GetString());
            Assert.Equal(native.GetProperty("pid").GetInt32(), native.GetProperty("processGroup").GetInt32());
            Assert.Contains("TUI-PID " + native.GetProperty("pid").GetInt32(), ReadShared(Path.Combine(Root, "serial.log")));
        }
        public async Task ExpectLiveBundledChild(CancellationToken token)
        {
            int Latest()
            {
                var serial = ReadShared(Path.Combine(Root, "serial.log"));
                Assert.DoesNotContain("OWNED-TERMINAL-CHILD-ENDED:" + _bundledAgent, serial);
                return System.Text.RegularExpressions.Regex.Matches(serial, "OWNED-TERMINAL-CHILD-ALIVE:" + _bundledAgent + ":([0-9]+)")
                    .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
            }
            var before = Latest();
            await WindowsConsoleProbeTests.Until(() => Latest() > before, token);
        }
        public void VerifyRefusedReturn()
        {
            _result = File.ReadAllText(Path.Combine(Root, SessionGuardian.ResultFile));
            using var document = JsonDocument.Parse(_result);
            Assert.True(document.RootElement.GetProperty("guestShutdownObserved").GetBoolean());
            Assert.True(document.RootElement.GetProperty("needsRecovery").GetBoolean());
            var recovery = ReturnRecovery.Open(document.RootElement.GetProperty("recoveryDirectory").GetString()!);
            Assert.False(recovery.LoadTransfer(Project).Complete);
            Assert.False(SessionCompletion.Returned(Root, Identity));
            Assert.True(SessionCompletion.ShutdownObserved(Root, Identity));
            var serial = ReadShared(Path.Combine(Root, "serial.log"));
            Assert.Contains("AGENT-CHILDREN-FAILED", serial);
            Assert.Contains("RETURN-FAILED", serial);
            Assert.DoesNotContain("RETURN-COMPLETE", serial);
            Assert.True(File.Exists(Path.Combine(Root, "session.qcow2")));
            Assert.False(File.Exists(Path.Combine(Project, "agent-progress.txt")));
        }
        private string CheckedScreen()
        {
            var output = Screen();
            if (output.Contains("Traceback (most recent call last)", StringComparison.Ordinal))
                throw new IOException("The owned program failed; see its private screen report.");
            return output;
        }
        public void Dispose()
        {
            try
            {
                if (Machine is not null && !Machine.HasExited)
                {
                    if (Terminal is not null && !Terminal.Process.HasExited) Terminal.Process.Kill();
                    if (!Machine.WaitForExit(45000)) { ConsoleSizeLink.Quit(Port); if (!Machine.WaitForExit(5000)) SessionSweep.StopAsLaunchAccount(Machine.Id); }
                }
                if (Machine is not null) TestUserRunner.ReleaseMachine(Machine.Id);
                Terminal?.Dispose(); Proxy?.Dispose();
                if (Owner is not null && !Owner.HasExited && !Owner.WaitForExit(5000)) Owner.Kill();
            }
            catch (Exception error) { _cleanupError = error.Message; }
            finally { _hostListener?.Dispose(); Owner?.Dispose(); Machine?.Dispose(); }
        }
        private static string ReadShared(string path)
        {
            try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream); return reader.ReadToEnd().Trim(); }
            catch (IOException) { return ""; }
        }
    }

    internal sealed class ConsoleProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _forward;
        private TcpClient? _terminal, _guest;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ConsoleProxy(int guestPort, Process machine)
        {
            _listener.Start();
            _forward = Task.Run(async () =>
            {
                try
                {
                    _terminal = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _guest = await Connect(guestPort, machine, _stop.Token);
                    var input = _terminal.GetStream().CopyToAsync(_guest.GetStream(), _stop.Token);
                    var output = _guest.GetStream().CopyToAsync(_terminal.GetStream(), _stop.Token);
                    await Task.WhenAny(input, output);
                    CloseConsole();
                    await Task.WhenAll(input, output);
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
        public void CloseConsole() { _terminal?.Dispose(); _guest?.Dispose(); }
        public void Dispose() { _stop.Cancel(); CloseConsole(); _listener.Stop(); try { _forward.GetAwaiter().GetResult(); } finally { _stop.Dispose(); } }
    }
}
