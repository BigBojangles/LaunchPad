using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestAgentToolsTests
{
    private static readonly SemaphoreSlim LaunchGate = new(1, 1);
    [EnvironmentFact("LAUNCHPAD_AGENT_INTERFACES", "1")]
    [Trait("Category", "Integration")]
    public Task ExactCliInterfacesAreRecordedInsideTheEnforcedGuest() => Run("interface");

    [EnvironmentFact("LAUNCHPAD_ACTIVITY_INTERFACES", "1")]
    [Trait("Category", "Integration")]
    public Task ExactActivityInterfacesAreRecordedInsideTheEnforcedGuest() => Run("activity-interface");

    [EnvironmentFact("LAUNCHPAD_CODEX_ACTIVITY", "1")]
    [Trait("Category", "Integration")]
    public Task APassiveObserverSeesTheActualInteractiveCodexRunWithoutTakingItsSession() => Run("activity-observe", "codex");

    [EnvironmentFact("LAUNCHPAD_GROK_ACTIVITY", "1")]
    [Trait("Category", "Integration")]
    public Task PassiveHooksRecordActualRepeatedInteractiveGrokRuns() => Run("grok-activity", "grok");

    [EnvironmentFact("LAUNCHPAD_GROK_BACKGROUND", "1")]
    [Trait("Category", "Integration")]
    public Task ActualGrokStopWithALiveBackgroundChildDoesNotFinishTheRootRun() => Run("grok-background", "grok");

    [EnvironmentFact("LAUNCHPAD_GROK_REPLAY", "1")]
    [Trait("Category", "Integration")]
    public async Task RetainedActualGrokCallbacksCanBeVerifiedWithoutAnotherVmLaunch()
    {
        var roots = (Environment.GetEnvironmentVariable("LAUNCHPAD_GROK_REPLAY_ROOT")
            ?? throw new InvalidOperationException("Explicit retained owned fixture required.")).Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(roots.Length, 1, 4);
        var owned = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest");
        foreach (var entry in roots)
        {
            var root = Path.GetFullPath(entry);
            Assert.Equal(owned, Path.GetDirectoryName(root), ignoreCase: true);
            Assert.Matches("^[a-f0-9]{12}$", Path.GetFileName(root));
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "agent-tools-private.json")));
            Assert.True(receipt.RootElement.GetProperty("complete").GetBoolean());
            Assert.True(receipt.RootElement.GetProperty("shutdown").GetBoolean());
            var mode = receipt.RootElement.GetProperty("mode").GetString()!;
            Assert.True(mode is "grok-activity" or "grok-background");
            await VerifyRecordedGrokCallbacks(root, mode);
        }
    }

    [EnvironmentFact("LAUNCHPAD_AGENT_TOOLS", "1")]
    [Trait("Category", "Integration")]
    public Task ActualBundledAgentsExecuteControlledChildCommandsUnderThePolicy() => Run("probe");

    [EnvironmentFact("LAUNCHPAD_AGENT_CONCURRENCY", "1")]
    [Trait("Category", "Integration")]
    public async Task ThreeActualAgentChildrenOverlapAndKeepTheirProjectsAndReturnsSeparate()
    {
        using var overlap = new ConcurrentChildren();
        var tasks = new[] { "grok", "codex", "claude" }.Select(async agent =>
        {
            try { await Run("probe", agent, overlap); }
            catch { overlap.Abort(); throw; }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        finally { await overlap.Save(); }
        Assert.Equal(3, overlap.Children.Count);
        Assert.Equal(3, overlap.Children.Select(child => child.Port).Distinct().Count());
        Assert.Equal(3, overlap.Children.Select(child => child.FixtureId).Distinct().Count());
        Assert.True(overlap.Overlapped);
    }

    private static async Task Run(string mode, string? singleAgent = null, ConcurrentChildren? overlap = null)
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var selectedHash = HashFile(runtime.KeptImage);
        var securityTemplate = Environment.GetEnvironmentVariable("LAUNCHPAD_SECURITY_TEMPLATE");
        var template = string.IsNullOrWhiteSpace(securityTemplate)
            ? Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_POLICY_TEMPLATE")
                ?? throw new InvalidOperationException("Explicit owned candidate required."))
            : SecurityTemplate(runtime);
        if (string.IsNullOrWhiteSpace(securityTemplate))
            Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
                template, StringComparison.OrdinalIgnoreCase);
        var expected = HashFile(template);
        await RunTool("icacls.exe", template, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        using var ownedHost = new OwnedHostListener();
        var targets = JsonSerializer.Serialize(new { gatewayAddress = "10.0.2.2", hostPort = ownedHost.Port });
        await File.WriteAllTextAsync(Path.Combine(root, "owned_targets.json"), targets);
        var disk = Path.Combine(root, "session.qcow2");
        var adapter = Path.Combine(RepositoryRoot(), "scripts", "guest-policy-probe.sh");
        var setupIdentities = new[] { "guest-policy-probe.sh", "diagnose-guest-policy.sh", "observe-guest-policy.py", "owned-agent-egress.sh" }
            .ToDictionary(name => name, name => HashFile(Path.Combine(RepositoryRoot(), "scripts", name)));
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(adapter), "stage", ToWslPath(disk), ToWslPath(template), expected, ToWslPath(root), "agent-tools");
        var project = Path.Combine(root, "fixture");
        Directory.CreateDirectory(project);
        var fixtureId = Guid.NewGuid().ToString("N");
        if (overlap is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(project, "owned_control.json"),
                JsonSerializer.Serialize(new { fixtureId, agent = singleAgent }));
            await File.WriteAllTextAsync(Path.Combine(project, "project-identity.txt"), fixtureId);
        }
        await File.WriteAllTextAsync(Path.Combine(project, "owned_targets.json"), targets);
        var supplied = new Dictionary<string, string>();
        foreach (var name in mode is "interface" or "activity-interface" ? new[] { "guest-agent-tools.py" } : mode == "activity-observe"
            ? new[] { "guest-agent-tools.py", "owned-model-fixture.py", "owned-codex-activity.py", "owned-codex-work.py", "codex-activity-observer.py" }
            : mode is "grok-activity" or "grok-background" ? new[] { "guest-agent-tools.py", "owned-model-fixture.py", "owned-grok-activity.py", "owned-grok-hook.py", "owned-grok-work.py" }
            : new[] { "guest-agent-tools.py", "owned-model-fixture.py", "owned-agent-boundary.py" })
        {
            var source = Path.Combine(RepositoryRoot(), "scripts", name);
            var target = Path.Combine(project, name == "owned-model-fixture.py" ? "owned_model_fixture.py"
                : name == "owned-grok-work.py" ? "owned-agent-boundary.py" : name);
            var bytes = await File.ReadAllBytesAsync(source);
            await File.WriteAllBytesAsync(target, bytes);
            supplied[name] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        }
        var program = Path.Combine(project, "lp-agent-tools");
        await File.WriteAllTextAsync(program, "#!/bin/sh\nexec python3 guest-agent-tools.py " + mode
            + (singleAgent is null ? "" : " --agent " + singleAgent) + "\n");
        // Serialize port selection until QEMU has actually opened its console.
        // Closing temporary port probes does not reserve a range for another VM.
        await LaunchGate.WaitAsync(overlap?.Stop.Token ?? CancellationToken.None);
        var port = AvailablePorts();
        var serial = Path.Combine(root, "serial.log");
        var arguments = QemuCommand.Build("whpx", disk, 0, port, "fence", null, serialLog: serial, memoryMb: 4096, cores: 2,
            firmwareDir: runtime.FirmwareDir, workingDirectory: root);
        Process? started = null;
        try { Assert.True(TestUserRunner.TryStart(runtime.QemuExe, root, arguments, out started), "Owned tool VM: " + TestUserRunner.LastStartError); }
        catch { LaunchGate.Release(); overlap?.Abort(); throw; }
        using var machine = started!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(mode == "interface" ? 3 : 6));
        var output = new StringBuilder();
        Task? capture = null;
        var complete = false;
        var shutdown = false;
        Exception? failure = null;
        ProjectReturnReceipt? returned = null;
        string? recoveryDirectory = null;
        var launchGateHeld = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, overlap?.Stop.Token ?? CancellationToken.None);
            var token = linked.Token;
            using var console = await Connect(QemuCommand.TuiPort(port), machine, token);
            LaunchGate.Release(); launchGateHeld = false;
            using var size = await ConsoleSizeLink.ConnectAsync(port, deadline.Token);
            size?.Send(100, 30);
            var nextPromptSent = false;
            capture = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await console.GetStream().ReadAsync(buffer, deadline.Token);
                    if (count == 0) break;
                    lock (output) output.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    if (mode is "activity-observe" or "grok-activity" or "grok-background" && Encoding.UTF8.GetString(buffer, 0, count).Contains("\u001b[6n", StringComparison.Ordinal))
                        await console.GetStream().WriteAsync("\u001b[1;1R"u8.ToArray(), deadline.Token);
                    if (mode is "grok-activity" or "grok-background" && !nextPromptSent && Output(output).Contains("GROK-ACTIVITY-NEXT", StringComparison.Ordinal))
                    {
                        nextPromptSent = true;
                        await console.GetStream().WriteAsync("Finish this second owned fixture turn.\r"u8.ToArray(), deadline.Token);
                    }
                }
            });
            using (var fence = await Connect(QemuCommand.FencePort(port), machine, token))
            {
                var manifest = await FenceHost.SendProjectAsync(fence.GetStream(), project, null, null, token,
                    new AgentLaunch(AgentChoice.Custom, "lp-agent-tools", program), restoreGuestState: true, restoreHostHome: false);
                manifest.Save(Path.Combine(root, "sent.manifest"));
            }
            using var status = new StatusLink(await Connect(QemuCommand.StatusPort(port), machine, deadline.Token));
            if (overlap is not null)
            {
                await WaitReady(singleAgent!, output, token);
                await overlap.Ready(new Child(singleAgent!, fixtureId, root, port, machine, DateTime.UtcNow), token);
                await console.GetStream().WriteAsync(Encoding.UTF8.GetBytes("RELEASE\n"), token);
            }
            await WaitOutput("AGENT-TOOLS-DONE", output, token);
            Assert.DoesNotContain("AGENT-TOOLS-FAILED", Output(output));
            complete = true;
            if (overlap is not null)
            {
                var recovery = ReturnRecovery.Create(root);
                SentManifest.Load(Path.Combine(root, "sent.manifest")).Save(recovery.Manifest);
                recoveryDirectory = recovery.DirectoryPath;
                // End only this controlled custom session. The production
                // exporter and strict receiver preserve its changes without apply.
                await console.GetStream().WriteAsync(Encoding.UTF8.GetBytes("EXIT\n"), token);
                returned = await Task.Run(() => ProjectPull.Receive(QemuCommand.FencePort(port), recovery.Payload, TimeSpan.FromSeconds(25)));
                recovery.SaveTransfer(project, returned);
                recovery.Record(returned.Complete ? "waiting" : "incomplete", "Owned concurrent fixture; no host apply.");
                Assert.True(returned.Complete, returned.Error);
                Assert.True(returned.HasContentIdentities);
                Assert.Contains(singleAgent + "-owned-write.txt", returned.Files);
                Assert.Equal("owned " + singleAgent + " tool write:" + fixtureId + "\n",
                    await File.ReadAllTextAsync(Path.Combine(recovery.Payload, singleAgent + "-owned-write.txt")));
                Assert.DoesNotContain("project-identity.txt", returned.Files);
                Assert.Equal(fixtureId, await File.ReadAllTextAsync(Path.Combine(project, "project-identity.txt")));
                Assert.False(File.Exists(Path.Combine(project, singleAgent + "-owned-write.txt")));
            }
        }
        catch (Exception error) { failure = error; overlap?.Abort(); }
        finally
        {
            if (launchGateHeld) LaunchGate.Release();
            shutdown = MachineShutdown.WaitForGuestExit(machine, port);
            if (!shutdown) { ConsoleSizeLink.Quit(port); if (!machine.WaitForExit(5000)) machine.Kill(entireProcessTree: true); }
            deadline.Cancel();
            if (capture is not null) try { await capture; } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            TestUserRunner.ReleaseMachine(machine.Id);
            if (mode is "activity-observe" or "grok-activity" or "grok-background") await File.WriteAllTextAsync(Path.Combine(root, "activity-terminal-private.log"), Output(output));
            await File.WriteAllTextAsync(Path.Combine(root, "agent-tools-private.json"), JsonSerializer.Serialize(new
            {
                mode, singleAgent, fixtureId, complete, shutdown, template, templateSha256 = expected, finalTemplateSha256 = HashFile(template), selectedHash, supplied, setupIdentities,
                returned, recoveryDirectory,
                hostListener = new { ownedHost.Port, ownedHost.LiveWitness, unexpectedConnections = ownedHost.Connections },
                failure = failure?.Message,
                limitations = "Owned disposable child. Custom controller requests the actual native CLIs through the same AppArmor/setpriv transition with controlled localhost model fixtures, no real auth/home/project. Separate from normal interactive UI entry, sign-in/history, complete security/performance and installation. No host return apply or scanner."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(shutdown);
        await RunTool("wsl.exe", "-d", "Ubuntu", "--", "bash", ToWslPath(adapter), "collect", ToWslPath(disk), ToWslPath(template), expected, ToWslPath(root), "agent-tools");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        using var results = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture-output", "results.json")));
        var rows = results.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(mode == "interface" ? 4 : mode == "activity-interface" ? 3 : mode is "activity-observe" or "grok-activity" or "grok-background" ? 1 : singleAgent is null ? 3 : 1, rows.Length);
        foreach (var row in rows)
        {
            Assert.False(row.GetProperty("timedOut").GetBoolean(), row.ToString());
            Assert.Equal(0, row.GetProperty("exit").GetInt32());
            if (mode == "probe") Assert.True(row.GetProperty("toolProof").GetBoolean(), row.ToString());
        }
        Assert.True(ownedHost.LiveWitness);
        Assert.Equal(0, ownedHost.Connections);
        if (mode is "grok-activity" or "grok-background")
            await VerifyRecordedGrokCallbacks(root, mode);
        if (mode == "activity-observe")
        {
            using var activityProof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture-output", "codex-activity-proof.json")));
            Assert.True(activityProof.RootElement.GetProperty("interactive").GetBoolean());
            Assert.True(activityProof.RootElement.GetProperty("completed").GetBoolean());
            Assert.True(activityProof.RootElement.GetProperty("reconnectedActive").GetBoolean());
            Assert.True(activityProof.RootElement.GetProperty("modelConsumedToolResult").GetBoolean());
            Assert.True(activityProof.RootElement.GetProperty("configUnchanged").GetBoolean());
            var eventLines = await File.ReadAllLinesAsync(Path.Combine(root, "fixture-output", "codex-activity-events.log"));
            var firstState = eventLines.First(line => line.StartsWith(AgentActivityTracker.StatePrefix, StringComparison.Ordinal));
            var state = JsonSerializer.Deserialize<LaunchPad.Models.AgentStateObservation>(firstState[AgentActivityTracker.StatePrefix.Length..], AgentActivityTracker.JsonOptions)!;
            var tracker = new AgentActivityTracker(state.Generation);
            var outcomes = 0;
            foreach (var line in eventLines)
            {
                if (line.StartsWith(AgentActivityTracker.StatePrefix, StringComparison.Ordinal)) Assert.True(tracker.TryAcceptStateLine(line), line);
                else { Assert.True(tracker.TryAcceptLine(line, out var accepted), line); Assert.Equal(LaunchPad.Models.AgentEventKind.RunFinished, accepted!.Kind); outcomes++; }
            }
            Assert.Equal(1, outcomes);
            Assert.Equal(LaunchPad.Models.AgentActivity.Idle, tracker.Snapshot.State);
            Assert.False(tracker.Snapshot.RunActive);
            Assert.False(tracker.HistoryComplete);
        }
        if (mode == "probe")
        {
            using var observed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "launchpad-policy-probe", "processes.json")));
            foreach (var agent in singleAgent is null ? new[] { "grok", "codex", "claude" } : new[] { singleAgent })
            {
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture-output", agent + "-boundary.json")));
                Assert.True(report.RootElement.GetProperty("passed").GetBoolean());
                if (overlap is not null) Assert.Equal(fixtureId, report.RootElement.GetProperty("fixtureId").GetString());
                var ancestry = report.RootElement.GetProperty("ancestry").EnumerateArray().ToArray();
                Assert.True(ancestry.Length >= 2);
                foreach (var row in ancestry)
                {
                    Assert.Equal("launchpad-agent (enforce)", row.GetProperty("label").GetString());
                    Assert.Equal("1", row.GetProperty("noNewPrivileges").GetString());
                    Assert.Equal("0000000000000000", row.GetProperty("capabilities").GetString());
                    var matching = observed.RootElement.EnumerateArray().Where(r => r.GetProperty("pid").GetInt32() == row.GetProperty("pid").GetInt32()
                        && r.GetProperty("startTicks").GetInt64() == row.GetProperty("startTicks").GetInt64()
                        && r.GetProperty("executable").GetString() == row.GetProperty("executable").GetString()).ToArray();
                    Assert.NotEmpty(matching);
                    Assert.All(matching, r =>
                    {
                        Assert.Equal(row.GetProperty("label").GetString(), r.GetProperty("label").GetString());
                        Assert.Equal(row.GetProperty("parentPid").GetInt32(), r.GetProperty("parentPid").GetInt32());
                        Assert.Equal("1", r.GetProperty("noNewPrivileges").GetString());
                        Assert.Equal("0000000000000000", r.GetProperty("capabilities").GetString());
                    });
                }
            }
            var rules = await File.ReadAllTextAsync(Path.Combine(root, "launchpad-policy-probe", "network-rules.txt"));
            Assert.Contains("-A OUTPUT -d 10.0.2.2/32 -j REJECT", rules);
            Assert.Contains("-A LP_OWNED_TOOL -d 10.0.2.2/32 -p tcp", rules);
            if (overlap is not null && singleAgent == "codex")
            {
                using var wire = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture-output", "codex-wire.json")));
                Assert.Contains(wire.RootElement.EnumerateArray(), row => row.TryGetProperty("pendingSession", out var session)
                    && session.ValueKind == JsonValueKind.Number);
            }
        }
        Assert.Equal(expected, HashFile(template));
        Assert.Equal(selectedHash, HashFile(runtime.KeptImage));
        var acceptancePath = Environment.GetEnvironmentVariable("LAUNCHPAD_AGENT_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(acceptancePath))
        {
            Assert.Equal("probe", mode);
            Assert.Null(singleAgent);
            acceptancePath = Path.GetFullPath(acceptancePath);
            Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "security") + Path.DirectorySeparatorChar,
                acceptancePath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(acceptancePath), "Refusing to overwrite boundary acceptance evidence.");
            await File.WriteAllTextAsync(acceptancePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, templateSha256 = expected, templateUnchanged = true,
                launchIdentity = TestUserRunner.UserName, completedUtc = DateTime.UtcNow,
                agents = new[] { "grok", "codex", "claude" }, boundariesPassed = true,
                guestShutdownObserved = shutdown, rawReports = root, supplied, setupIdentities,
                hostListener = new { ownedHost.Port, ownedHost.LiveWitness, unexpectedConnections = ownedHost.Connections },
                limitations = "Three real native agent tool calls and inherited child processes with controlled local model responses. Owned project/config writes, system/device/other-project denials and one live host-gateway target. No authenticated history, complete network policy, interactive UI or host return apply."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task VerifyRecordedGrokCallbacks(string root, string mode)
    {
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture-output", "grok-hook-proof.json")));
            Assert.True(proof.RootElement.GetProperty("interactive").GetBoolean());
            Assert.True(proof.RootElement.GetProperty("completed").GetBoolean());
            Assert.Null(proof.RootElement.GetProperty("failure").GetString());
            var generation = Guid.NewGuid().ToString("N");
            var projection = new GrokActivityAdapter(generation, proof.RootElement.GetProperty("sessionId").GetString()!, "/home/builder/in/project");
            var rowsFromHooks = new List<JsonDocument>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "fixture-output", "grok-hooks"), "*.json"))
                    rowsFromHooks.Add(JsonDocument.Parse(await File.ReadAllTextAsync(file)));
                var startedRuns = new HashSet<string>();
                var endedRuns = new HashSet<string>();
                var ambiguousStops = 0;
                var authoritativeStarts = new HashSet<string>();
                var generatedWakeups = new HashSet<string>();
                foreach (var callback in rowsFromHooks.OrderBy(row => row.RootElement.GetProperty("capturedNs").GetInt64()))
                {
                    var value = callback.RootElement;
                    var eventName = value.GetProperty("hookEventName").GetString();
                    var accepted = projection.TryAccept(value.GetProperty("adapterInput").GetRawText(), out var projected);
                    var identifiedStop = eventName == "Stop" && value.GetProperty("identity").TryGetProperty("promptId", out _);
                    var hasBackground = identifiedStop && value.GetProperty("stopMetadata")
                        .TryGetProperty("backgroundTasksCount", out var backgroundCount)
                        && backgroundCount.ValueKind == JsonValueKind.Number && backgroundCount.GetInt32() > 0;
                    if (hasBackground)
                    {
                        Assert.False(accepted);
                        Assert.Null(projected);
                        Assert.True(projection.Snapshot.RunActive);
                        Assert.Equal(value.GetProperty("identity").GetProperty("promptId").GetString(), projection.Snapshot.RunId);
                        ambiguousStops++;
                    }
                    else if (eventName is "SessionStart" or "UserPromptSubmit" || identifiedStop) Assert.True(accepted, value.GetRawText());
                    else Assert.False(accepted);
                    if (eventName == "UserPromptSubmit")
                    {
                        var sourcePrompt = value.GetProperty("identity").GetProperty("promptId").GetString()!;
                        if (sourcePrompt.StartsWith("task-completed-", StringComparison.Ordinal))
                        {
                            generatedWakeups.Add(sourcePrompt);
                            Assert.Equal(LaunchPad.Models.AgentEventKind.Working, projected!.Value.Kind);
                            Assert.Contains(projected.Value.RunId!, authoritativeStarts);
                        }
                        else authoritativeStarts.Add(sourcePrompt);
                    }
                    if (projected?.Value.Kind == LaunchPad.Models.AgentEventKind.RunStarted) startedRuns.Add(projected.Value.RunId!);
                    if (projected?.Value.Kind == LaunchPad.Models.AgentEventKind.RunFinished) endedRuns.Add(projected.Value.RunId!);
                }
                Assert.Equal(2, authoritativeStarts.Count);
                if (mode == "grok-activity")
                {
                    Assert.Equal(2, startedRuns.Count);
                    Assert.True(startedRuns.SetEquals(endedRuns));
                    Assert.Equal(0, ambiguousStops);
                }
                else
                {
                    Assert.True(ambiguousStops > 0);
                    Assert.Single(generatedWakeups);
                    Assert.Equal(2, startedRuns.Count);
                    Assert.True(authoritativeStarts.SetEquals(endedRuns));
                    Assert.Empty(endedRuns.Intersect(generatedWakeups));
                    Assert.Contains(projection.Snapshot.RunId!, endedRuns);
                }
                Assert.Equal(LaunchPad.Models.AgentActivity.Idle, projection.Snapshot.State);
                Assert.False(projection.Snapshot.RunActive);
            }
            finally { foreach (var row in rowsFromHooks) row.Dispose(); }
            }

    private static async Task WaitReady(string agent, StringBuilder output, CancellationToken token)
    {
        while (!Output(output).Contains("AGENT-TOOL-CHILD-READY:" + agent, StringComparison.Ordinal))
        {
            Assert.DoesNotContain("AGENT-TOOLS-DONE", Output(output));
            Assert.DoesNotContain("Traceback (most recent call last)", Output(output));
            await Task.Delay(100, token);
        }
    }

    private sealed record Child(string Agent, string FixtureId, string Root, int Port, Process Machine, DateTime ReadyUtc);

    private sealed class ConcurrentChildren : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Stop { get; } = new();
        public List<Child> Children { get; } = [];
        public bool Overlapped { get; private set; }
        public DateTime? ReleasedUtc { get; private set; }
        public async Task Ready(Child child, CancellationToken token)
        {
            bool allReady;
            lock (Children)
            {
                Children.Add(child);
                allReady = Children.Count == 3;
            }
            if (allReady)
            {
                // Force a real overlap interval and Codex's asynchronous tool
                // result; a fixture that ignores its session ID must fail.
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                Assert.All(Children, row => Assert.False(row.Machine.HasExited));
                Overlapped = true; ReleasedUtc = DateTime.UtcNow;
                _release.SetResult();
            }
            await _release.Task.WaitAsync(token);
        }
        public void Abort() => Stop.Cancel();
        public Task Save() => File.WriteAllTextAsync(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "concurrent-children-" + Guid.NewGuid().ToString("N")[..12] + "-private.json"), JsonSerializer.Serialize(new
            {
                overlapped = Overlapped, releasedUtc = ReleasedUtc,
                children = Children.Select(child => new { child.Agent, child.FixtureId, child.Root, child.Port, child.ReadyUtc }).ToArray(),
                limitations = "Three owned VMs with actual native CLI shell children held at a barrier, scoped policy probes and independent production project returns. Headless owned model fixture; no UI launch, credentials, host apply, abrupt terminal death, full security or performance acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        public void Dispose() => Stop.Dispose();
    }

    internal sealed class OwnedHostListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private int _connections;
        public int Port { get; }
        public bool LiveWitness { get; }
        public int Connections => Volatile.Read(ref _connections);
        public OwnedHostListener(IPAddress? address = null)
        {
            address ??= IPAddress.Loopback;
            _listener = new TcpListener(address, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            using (var control = new TcpClient(address.AddressFamily))
            {
                control.Connect(address, Port);
                control.GetStream().WriteByte(0x4c);
                using var accepted = _listener.AcceptTcpClient();
                accepted.ReceiveTimeout = 2000;
                LiveWitness = accepted.GetStream().ReadByte() == 0x4c;
            }
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        Interlocked.Increment(ref _connections);
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            });
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _accept.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
