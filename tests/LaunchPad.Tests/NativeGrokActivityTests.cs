using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class NativeGrokActivityTests
{
    [Fact]
    public void DurableCaptureDropsPayloadAndKeepsOriginalConsentAcrossConsumerRetries()
    {
        var (root, binding) = Fixture();
        var inbox = new GrokActivityInbox(root, binding);
        var first = new NotificationConsent(true, true, true, Guid.NewGuid().ToString("N"), "original-epoch");
        var consentReads = 0;
        var receipt = inbox.Capture(Input(binding, "UserPromptSubmit", "owned-run"), DateTimeOffset.UtcNow, () => { consentReads++; return first; });
        Assert.NotNull(receipt);
        Assert.DoesNotContain("PRIVATE-", File.ReadAllText(Path.Combine(root, GrokActivityInbox.FileName)));
        var feed = new GrokActivityFeed(root, binding);
        feed.Ingest(receipt!);
        var persisted = new GrokActivityInbox(root, binding).Read().Receipts[0];
        Assert.Equal(receipt, persisted); Assert.Equal(1, consentReads);
        Assert.False(new GrokActivityFeed(root, binding).Ingest(persisted));
        Assert.Equal(first, feed.Read().Pending[0].Consent);
        Assert.False(inbox.Acknowledge("wrong-prefix")); Assert.True(inbox.Acknowledge(receipt!.Id));
        Assert.Empty(inbox.Read().Receipts);
        var noConsent = inbox.Capture(Input(binding, "Stop", "owned-run"), DateTimeOffset.UtcNow, () => throw new IOException("owned settings unavailable"));
        Assert.Null(new GrokActivityInbox(root, binding).Read().Receipts[0].CapturedConsent);
        Assert.Equal(2, noConsent!.Sequence);
        Assert.True(feed.Ingest(noConsent)); Assert.Null(feed.Read().Pending[^1].Consent);
    }

    [Theory]
    [InlineData("{\"id\":\"01a113f1-3214-70d0-9549-4760e9fcf795\",\"type\":\"shell\",\"status\":\"running\",\"state\":\"unknown\"}")]
    [InlineData("{\"id\":\"01a113f1-3214-70d0-9549-4760e9fcf795\",\"type\":\"shell\",\"status\":\"running\",\"state\":false}")]
    [InlineData("{\"id\":\"01a113f1-3214-70d0-9549-4760e9fcf795\",\"type\":\"shell\",\"status\":\"unknown\",\"state\":\"running\"}")]
    [InlineData("{\"id\":\"01a113f1-3214-70d0-9549-4760e9fcf795\",\"type\":\"shell\",\"status\":\"running\",\"status\":\"running\"}")]
    public void MalformedTaskMetadataCannotBecomeValidProvenanceAfterRedaction(string task)
    {
        var (_, binding) = Fixture();
        var raw = Input(binding, "Stop", "run", "[" + task + "]");
        Assert.True(GrokActivityInbox.TryRedact(binding, raw, out var clean));
        var adapter = new GrokActivityAdapter(binding.Generation, binding.RootSessionId, binding.CallbackDirectory);
        Assert.True(adapter.TryAccept(Input(binding, "UserPromptSubmit", "run"), out _));
        Assert.False(adapter.TryAccept(clean, out _)); Assert.Empty(adapter.Checkpoint.BackgroundTaskIds);
        Assert.False(adapter.TryAccept(Input(binding, "UserPromptSubmit", "task-completed-01a113f1-3214-70d0-9549-4760e9fcf795"), out _));
    }

    [Fact]
    public void InboxRejectsWrongIdentitiesDuplicatesConflictingArraysAndOversizedInputWithoutCollectingConsent()
    {
        var (root, binding) = Fixture(); var inbox = new GrokActivityInbox(root, binding);
        NotificationConsent? NoRead() => throw new InvalidOperationException("Must never collect consent for invalid input");
        var input = Input(binding, "Stop", "run");
        foreach (var invalid in new[] { input.Replace(binding.RootSessionId, "wrong-root"), input.Replace("\"Stop\"", "\"PermissionRequest\""),
            input.Replace("\"backgroundTasks\":[]", "\"backgroundTasks\":[],\"background_tasks\":[{}]"),
            input.Replace("\"promptId\":", "\"promptId\":\"duplicate\",\"promptId\":"),
            input.Replace("\"promptId\":", "\"agentId\":\"child\",\"promptId\":"), new string('x', 65537) })
            Assert.Null(inbox.Capture(invalid, DateTimeOffset.UtcNow, NoRead));
        Assert.False(File.Exists(Path.Combine(root, GrokActivityInbox.FileName)));
        Assert.True(GrokActivityInbox.TryRedact(binding, input, out var clean));
        Assert.True(GrokActivityInbox.TryRedact(binding, clean, out var repeated)); Assert.Equal(clean, repeated);
    }

    [Fact]
    public void UnprovedNativeTurnCallbacksCannotPublishBusyOrCompletionEvenWithCapturedConsent()
    {
        var (root, binding) = Fixture(); var inbox = new GrokActivityInbox(root, binding); var feed = new GrokActivityFeed(root, binding);
        var consent = new NotificationConsent(true, true, true, Guid.NewGuid().ToString("N"), "owned-epoch");
        var ready = inbox.Capture(Input(binding, "SessionStart", null), DateTimeOffset.UtcNow, () => null, allowRunLifecycle: false);
        Assert.NotNull(ready); Assert.True(feed.Ingest(ready!)); Assert.Equal(AgentActivity.Idle, feed.Current().State);
        Assert.True(feed.Acknowledge(feed.Read().Pending.Select(item => item.Event.EventId).ToArray()));
        foreach (var name in new[] { "UserPromptSubmit", "Stop", "StopFailure" })
        {
            var receipt = inbox.Capture(Input(binding, name, "run"), DateTimeOffset.UtcNow, () => consent, allowRunLifecycle: false);
            Assert.NotNull(receipt); Assert.False(receipt!.AllowRunLifecycle);
            Assert.True(feed.Ingest(receipt));
        }
        Assert.Empty(feed.Read().Pending); Assert.Equal(AgentActivity.Unknown, feed.Current().State); Assert.False(feed.Read().Checkpoint.HistoryComplete);
        Assert.Null(inbox.Capture(Input(binding, "Stop", "run").Replace("\"promptId\":", "\"subagentType\":\"explore\",\"promptId\":"), DateTimeOffset.UtcNow, () => null));
        var raw = Input(binding, "Stop", "run").Replace("\"promptId\":", "\"reason\":\"session_end\",\"promptId\":");
        Assert.True(GrokActivityInbox.TryRedact(binding, raw, out var callback));
        var adapter = new GrokActivityAdapter(binding.Generation, binding.RootSessionId, binding.CallbackDirectory);
        adapter.TryAccept(Input(binding, "UserPromptSubmit", "run"), out _);
        Assert.False(adapter.TryAccept(callback, out _)); Assert.True(adapter.Snapshot.RunActive);
        var path = Path.Combine(root, GrokActivityInbox.FileName);
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var row in malformed["receipts"]!.AsArray()) row!.AsObject().Remove("allowRunLifecycle");
        File.WriteAllText(path, malformed.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new GrokActivityInbox(root, binding).Read());
    }

    [Fact]
    public void WindowsReadyWithEmptyPromptAndDistinctExplicitWorkspaceSurvivesProjectionAndReopen()
    {
        var (root, binding) = Fixture(); binding = binding with { WorkspaceRoot = Path.GetDirectoryName(binding.HostProject) };
        var input = Input(binding, "SessionStart", "").Replace("\"stopHookActive\":", "\"workspaceRoot\":" + JsonSerializer.Serialize(binding.WorkspaceRoot) + ",\"stopHookActive\":");
        var receipt = new GrokActivityInbox(root, binding).Capture(input, DateTimeOffset.UtcNow, () => null);
        Assert.NotNull(receipt);
        var feed = new GrokActivityFeed(root, binding); Assert.True(feed.Ingest(receipt!));
        Assert.Equal(AgentActivity.Idle, feed.Current().State);
        Assert.Equal(binding.WorkspaceRoot, new GrokActivityFeed(root, binding).Read().Checkpoint.WorkspaceRoot);
        Assert.Null(new GrokActivityInbox(root, binding).Capture(Input(binding, "UserPromptSubmit", ""), DateTimeOffset.UtcNow, () => null));
        Assert.Throws<InvalidDataException>(() => new GrokActivityFeed(root, binding with { WorkspaceRoot = "wrong-workspace" }).Read());
    }

    [Fact]
    public async Task AShortCompetingCaptureKeepsTheConsentTakenBeforeWaiting()
    {
        var (root, binding) = Fixture(); var inbox = new GrokActivityInbox(root, binding);
        using var held = new FileStream(Path.Combine(root, GrokActivityInbox.FileName + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var original = new NotificationConsent(true, true, true, Guid.NewGuid().ToString("N"), "original-epoch");
        var current = original; using var captured = new ManualResetEventSlim(); var reads = 0;
        var pending = Task.Run(() => inbox.Capture(Input(binding, "UserPromptSubmit", "run"), DateTimeOffset.UtcNow,
            () => { var value = current; Interlocked.Increment(ref reads); captured.Set(); return value; }));
        Assert.True(captured.Wait(2000)); current = original with { Epoch = "later-epoch" }; held.Dispose();
        var receipt = await pending.WaitAsync(TimeSpan.FromSeconds(2)); Assert.NotNull(receipt);
        Assert.Equal(1, reads); Assert.Equal(original, receipt!.CapturedConsent);
        Assert.Equal(original, new GrokActivityInbox(root, binding).Read().Receipts[0].CapturedConsent);
    }

    [Fact]
    public void FailedWriteCorruptLedgerAndContendingWriterPreserveEarlierReceipts()
    {
        var (root, binding) = Fixture(); var inbox = new GrokActivityInbox(root, binding);
        inbox.Capture(Input(binding, "UserPromptSubmit", "run"), DateTimeOffset.UtcNow, () => null);
        var path = Path.Combine(root, GrokActivityInbox.FileName); var before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => inbox.Capture(Input(binding, "Stop", "run"), DateTimeOffset.UtcNow, () => null)) is IOException or UnauthorizedAccessException);
        Assert.Equal(before, File.ReadAllBytes(path));
        using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => inbox.Read());
        Assert.Equal(before, File.ReadAllBytes(path));
        File.WriteAllText(path, "{}"); Assert.Throws<InvalidDataException>(() => inbox.Read()); Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void ReopenedNativeBoardReadsOnlyFreshActivityForItsCurrentGeneration()
    {
        var (root, binding) = Fixture(); Directory.CreateDirectory(binding.HostProject);
        var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"), exePath: Path.Combine(root, "LaunchPad.exe"));
        var locator = new GrokLocator(paths); var log = new SetupLog(paths); var launcher = new ProjectLauncher(locator, log);
        var directory = launcher.SessionDirectory(binding.HostProject)!; Directory.CreateDirectory(directory);
        using var self = Process.GetCurrentProcess();
        NativeAgentTerminal.Save(directory, new(binding.HostProject, AgentChoice.Grok, Path.Combine(root, "grok.exe"), binding.Generation,
            self.Id, self.StartTime.ToUniversalTime().Ticks, "running"));
        var adapter = new GrokActivityAdapter(binding.Generation, binding.RootSessionId, binding.CallbackDirectory);
        adapter.TryAccept(Input(binding, "UserPromptSubmit", "run"), out _);
        SessionActivityStore.Publish(new(directory, binding.Generation), adapter.Snapshot, true);
        var runtime = new WindowsProjectRuntime(new GrokSetup(paths, locator, log), new ProjectLauncher(locator, log), log, paths, new SettingsStore(paths));
        Assert.Equal(SessionLifecycle.Busy, runtime.DescribeHost(binding.HostProject)!.State);
        Assert.Equal(AgentActivity.Working, runtime.DescribeHost(binding.HostProject)!.Activity!.State);
        SessionActivityStore.Publish(new(directory, binding.Generation), adapter.Snapshot, true, synchronized: false);
        Assert.Equal(AgentActivity.Unknown, runtime.DescribeHost(binding.HostProject)!.Activity!.State);
        Assert.Equal(SessionLifecycle.Running, runtime.DescribeHost(binding.HostProject)!.State);
        NativeAgentTerminal.Save(directory, NativeAgentTerminal.Read(directory)! with { Generation = Guid.NewGuid().ToString("N") });
        Assert.Equal(AgentActivity.Unknown, runtime.DescribeHost(binding.HostProject)!.Activity!.State);
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_GROK_CALLBACK_FIXTURE", "1")]
    [Trait("Category", "Integration")]
    public async Task OwnedNativeSingleTurnProvesHookPlumbingWithoutClaimingForegroundOrBackgroundAcceptance()
    {
        Assert.True(OperatingSystem.IsWindows());
        var (root, _) = Fixture();
        var realPaths = new AppPaths();
        var program = realPaths.GrokExe;
        Assert.True(File.Exists(program));
        var project = Path.Combine(root, "project with spaces & apostrophe's"); Directory.CreateDirectory(project);
        var native = Path.Combine(root, "native"); Directory.CreateDirectory(native);
        var paths = new AppPaths(userProfile: root, grokHome: Path.Combine(root, "grok home"), appDataDir: Path.Combine(root, "appdata"),
            exePath: Environment.GetEnvironmentVariable("LAUNCHPAD_NATIVE_GROK_HELPER") ?? throw new InvalidOperationException("Explicit phase helper required"));
        Directory.CreateDirectory(paths.GrokHome);
        var listenerProbe = new TcpListener(IPAddress.Loopback, 0); listenerProbe.Start();
        var port = ((IPEndPoint)listenerProbe.LocalEndpoint).Port; listenerProbe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var calls = 0;
        var server = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var input = new StreamReader(request.Request.InputStream);
                await input.ReadToEndAsync(timeout.Token); // owned model fixture only; never retain request context
                Interlocked.Increment(ref calls);
                var result = JsonSerializer.Serialize(new { id = "chatcmpl_owned", @object = "chat.completion.chunk", created = 1, model = "launchpad-fixture",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = "Owned fixture complete." }, finish_reason = "stop" } } });
                var bytes = Encoding.UTF8.GetBytes("data: " + result + "\n\ndata: [DONE]\n\n");
                request.Response.ContentType = "text/event-stream"; request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes, timeout.Token); request.Response.Close();
            }
        }, timeout.Token);
        var config = Path.Combine(paths.GrokHome, "config.toml");
        File.WriteAllText(config, "[models]\ndefault = \"launchpad-fixture\"\n[model.\"launchpad-fixture\"]\nmodel = \"launchpad-fixture\"\nbase_url = \"http://127.0.0.1:" + port + "/v1\"\nenv_key = \"LP_NATIVE_FIXTURE_KEY\"\napi_backend = \"chat_completions\"\ncontext_window = 128000\n");
        Directory.CreateDirectory(Path.Combine(paths.GrokHome, "hooks"));
        var sentinel = Path.Combine(paths.GrokHome, "hooks", "existing-owned.json");
        var sentinelBytes = Encoding.UTF8.GetBytes("{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"cmd.exe /d /c exit 0\",\"timeout\":2}]}]}}");
        File.WriteAllBytes(sentinel, sentinelBytes);
        using var self = Process.GetCurrentProcess();
        var record = new NativeLaunchRecord(project, AgentChoice.Grok, program, Guid.NewGuid().ToString("N"), self.Id, self.StartTime.ToUniversalTime().Ticks,
            AppDataDirectory: paths.AppDataDir);
        NativeAgentTerminal.Save(native, record);
        var activity = NativeGrokActivity.TryCreate(native, record, paths); Assert.NotNull(activity);
        var expectedSession = activity!.SessionId;
        try
        {
            var start = NativeAgentTerminal.AgentStart(record); activity.Configure(start);
            start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            start.Environment["GROK_HOME"] = paths.GrokHome; start.Environment["LP_NATIVE_FIXTURE_KEY"] = "owned-fixture-not-a-secret";
            start.Environment["HTTP_PROXY"] = $"http://127.0.0.1:{port}"; start.Environment["HTTPS_PROXY"] = $"http://127.0.0.1:{port}";
            start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            start.ArgumentList.Add("--single"); start.ArgumentList.Add("Reply with the owned fixture completion. Do not use tools.");
            start.ArgumentList.Add("--model"); start.ArgumentList.Add("launchpad-fixture");
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                record = record with { State = "running", AgentPid = child.Id, AgentStartTicks = child.StartTime.ToUniversalTime().Ticks };
                NativeAgentTerminal.Save(native, record); activity.Attach(child);
                await child.WaitForExitAsync(timeout.Token);
                var output = await stdout; var errors = await stderr;
                // Output contains only this fixture's prompt/response, never a user conversation.
                File.WriteAllText(Path.Combine(root, "owned-native-stdout.txt"), output);
                File.WriteAllText(Path.Combine(root, "owned-native-stderr.txt"), errors);
                Assert.Equal(0, child.ExitCode); Assert.True(calls > 0);
                var stateDirectory = Path.Combine(native, "grok-activity", record.Generation);
                var sourceBinding = NativeGrokActivity.ReadBinding(stateDirectory)!.Activity;
                var captured = new GrokActivityInbox(stateDirectory, sourceBinding).Read().Receipts;
                Assert.Contains(captured, row => row.Callback.Contains("user_prompt_submit", StringComparison.Ordinal));
                Assert.Contains(captured, row => row.Callback.Contains("\"stop\"", StringComparison.Ordinal));
                Assert.All(captured, row => Assert.False(row.AllowRunLifecycle));
                File.WriteAllText(Path.Combine(root, "native-hook-receipts-private.json"), JsonSerializer.Serialize(captured, new JsonSerializerOptions { WriteIndented = true }));
                activity.Poll(false); Assert.False(activity.Failed);
                var state = new GrokActivityFeed(stateDirectory, NativeGrokActivity.ReadBinding(stateDirectory)!.Activity).Read();
                Assert.Equal(expectedSession, state.Binding.RootSessionId);
                var journal = File.ReadAllText(Path.Combine(native, SessionActivityStore.EventsFile));
                Assert.DoesNotContain("RunStarted", journal);
                Assert.DoesNotContain("RunFinished", journal);
                Assert.Contains("Ready", journal);
                Assert.Empty(new NotificationOutbox(Path.Combine(paths.AppDataDir, "notifications", "outbox")).Read());
                Assert.False(NativeGrokActivity.NativeOutcomeAlertsVerified);
                File.WriteAllText(Path.Combine(root, "native-hook-plumbing-proof-private.json"), JsonSerializer.Serialize(new { complete = true,
                    helper = paths.ExePath, grok = program, rootSession = expectedSession, generation = record.Generation,
                    calls, foreground = false, background = false, realRecipients = false, outcomeAlertsEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinel));
            // Preserve an owned file changed by its user instead of deleting it.
            var ownedHook = Path.Combine(paths.GrokHome, "hooks", "launchpad-" + record.Generation + ".json");
            File.AppendAllText(ownedHook, "\n "); activity.Dispose(); Assert.True(File.Exists(ownedHook));
            var cleanRecord = record with { Generation = Guid.NewGuid().ToString("N"), State = "starting", AgentPid = 0, AgentStartTicks = 0 };
            NativeAgentTerminal.Save(native, cleanRecord);
            using var clean = NativeGrokActivity.TryCreate(native, cleanRecord, paths); Assert.NotNull(clean);
            var cleanHook = Path.Combine(paths.GrokHome, "hooks", "launchpad-" + cleanRecord.Generation + ".json");
            Assert.True(File.Exists(cleanHook)); clean!.Dispose(); Assert.False(File.Exists(cleanHook));
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinel));
        }
        finally
        {
            activity.Dispose(); timeout.Cancel(); listener.Stop();
            try { await server; } catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static string Input(GrokActivityBinding binding, string kind, string? run, string tasks = "[]") =>
        "{\"hookEventName\":" + JsonSerializer.Serialize(kind) + ",\"sessionId\":" + JsonSerializer.Serialize(binding.RootSessionId)
        + ",\"cwd\":" + JsonSerializer.Serialize(binding.CallbackDirectory) + ",\"promptId\":" + JsonSerializer.Serialize(run)
        + ",\"stopHookActive\":false,\"backgroundTasks\":" + tasks + ",\"sessionCrons\":[],\"prompt\":\"PRIVATE-PROMPT\",\"toolResult\":\"PRIVATE-RESULT\"}";
    private static (string Root, GrokActivityBinding Binding) Fixture()
    {
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "installer", "LaunchPad.iss")))
            {
                var root = Path.Combine(parent.FullName, "tests", "LaunchPad.Tests", "TestResults", "migration", "activity-native-20261006", "owned-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var project = Path.Combine(root, "project");
                return (root, new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), project, project, "1.0.46"));
            }
        throw new DirectoryNotFoundException("Owned repository fixture root unavailable.");
    }
}
