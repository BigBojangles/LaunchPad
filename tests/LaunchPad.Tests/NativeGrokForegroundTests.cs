using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class NativeGrokForegroundTests
{
    [EnvironmentFact("LAUNCHPAD_NATIVE_GROK_FOREGROUND", "1")]
    [Trait("Category", "Integration")]
    public async Task NormalNativeConsoleShowsOneMatchedForegroundTurnWithoutEnablingLiveGates()
    {
        Assert.True(OperatingSystem.IsWindows());
        var phase = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests/LaunchPad.Tests/TestResults/migration/grok-foreground-20261007");
        var root = Path.Combine(phase, "owned-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project with spaces & apostrophe's"); Directory.CreateDirectory(project);
        var native = Path.Combine(root, "native"); Directory.CreateDirectory(native);
        var home = Path.Combine(root, "grok home"); Directory.CreateDirectory(home);
        var program = new AppPaths().GrokExe; Assert.True(File.Exists(program));
        var helper = Path.Combine(GuestBaselineTests.RepositoryRoot(), "src/LaunchPad/bin/Debug/net8.0/LaunchPad.exe");
        Assert.True(File.Exists(helper));
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var calls = 0;
        var responseReleased = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync().WaitAsync(timeout.Token);
                if (request.Request.HttpMethod != "POST" || request.Request.Url?.AbsolutePath != "/v1/chat/completions")
                { request.Response.StatusCode = 400; request.Response.Close(); continue; }
                using var input = new StreamReader(request.Request.InputStream);
                await input.ReadToEndAsync(timeout.Token); // owned model request, never persisted
                Interlocked.Increment(ref calls);
                await release.Task.WaitAsync(timeout.Token);
                var result = JsonSerializer.Serialize(new { id = "chatcmpl_owned_foreground", @object = "chat.completion.chunk", created = 1,
                    model = "launchpad-fixture", choices = new[] { new { index = 0, delta = new { role = "assistant", content = "Owned foreground complete." }, finish_reason = "stop" } } });
                var bytes = Encoding.UTF8.GetBytes("data: " + result + "\n\ndata: [DONE]\n\n");
                request.Response.ContentType = "text/event-stream"; request.Response.ContentLength64 = bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes, timeout.Token); request.Response.Close();
            }
        }, timeout.Token);
        File.WriteAllText(Path.Combine(home, "config.toml"), "[models]\ndefault = \"launchpad-fixture\"\n[model.\"launchpad-fixture\"]\nmodel = \"launchpad-fixture\"\nbase_url = \"http://127.0.0.1:" + port + "/v1\"\nenv_key = \"LP_FOREGROUND_FIXTURE_KEY\"\napi_backend = \"chat_completions\"\ncontext_window = 128000\n");
        var generation = Guid.NewGuid().ToString("N");
        NativeAgentTerminal.Save(native, new(project, AgentChoice.Grok, program, generation, AppDataDirectory: Path.Combine(root, "appdata")));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = project };
        start.ArgumentList.Add(NativeAgentTerminal.Argument); start.ArgumentList.Add(native);
        start.Environment["GROK_HOME"] = home; start.Environment["GROK_BIN_DIR"] = Path.GetDirectoryName(program)!;
        start.Environment["LP_FOREGROUND_FIXTURE_KEY"] = "owned-loopback-fixture-not-a-secret";
        start.Environment["HTTP_PROXY"] = $"http://127.0.0.1:{port}"; start.Environment["HTTPS_PROXY"] = $"http://127.0.0.1:{port}";
        start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        Process? owner = null; HiddenConsole? console = null;
        string? lastScreen = null; string? failure = null; bool complete = false; bool stopped = false; bool childStopped = false;
        NativeLaunchRecord? record = null; GrokUpdateProjectionState? working = null; GrokUpdateProjectionState? finished = null;
        try
        {
            owner = Process.Start(start) ?? throw new IOException("Owned native console did not start.");
            await Until(() => (record = NativeAgentTerminal.Read(native)) is { State: "running", AgentPid: > 0 }, owner, timeout.Token);
            console = HiddenConsole.AttachTo(owner);
            // Inspect the real TUI. Do not bypass any onboarding/trust prompt;
            // inability to reach the ordinary prompt is retained as failure.
            await Until(() =>
            {
                lastScreen = console.ReadScreen(true);
                return lastScreen.Contains("launchpad-fixture", StringComparison.OrdinalIgnoreCase);
            }, owner, timeout.Token);
            console.Type("Reply with Owned foreground complete. Do not use tools."); console.Type("\r");
            var stateDirectory = Path.Combine(native, "grok-activity", generation);
            await Until(() => Volatile.Read(ref calls) > 0, owner, timeout.Token);
            var binding = NativeGrokActivity.ReadBinding(stateDirectory) ?? throw new IOException("Native activity binding unavailable.");
            var projector = new GrokUpdateProjection(stateDirectory, binding.Activity);
            await Until(() => (working = projector.Read()).Activity is { State: AgentActivity.Working, RunActive: true }, owner, timeout.Token);
            Assert.NotNull(working!.Activity.RunId);
            await Task.Delay(1000, timeout.Token);
            Assert.Equal(working.Activity.RunId, projector.Read().Activity.RunId);
            Assert.True(projector.Read().Activity.RunActive);
            responseReleased = true; release.SetResult();
            await Until(() => (finished = projector.Read()).Activity is { State: AgentActivity.Idle, RunActive: false, LastEvent.Kind: AgentEventKind.RunFinished }, owner, timeout.Token);
            Assert.Equal(working.Activity.RunId, finished!.Activity.RunId);
            Assert.Empty(finished.Pending); Assert.False(projector.IsCurrent(finished));
            Assert.False(NativeGrokActivity.NativeRunLifecycleVerified); Assert.False(NativeGrokActivity.NativeOutcomeAlertsVerified);
            var observation = SessionActivityStore.Read(native, generation);
            Assert.True(observation?.Activity.LastEvent?.Kind is null or AgentEventKind.Ready);
            Assert.Empty(new NotificationOutbox(Path.Combine(root, "appdata", "notifications", "outbox")).Read());
            Assert.False(owner.HasExited); Assert.True(WindowsSessionWindow.MatchesProcess(record!.AgentPid, record.AgentStartTicks));
            lastScreen = console.ReadScreen(true);
            complete = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            if (console is not null) try { lastScreen = console.ReadScreen(true); } catch { }
            console?.Dispose();
            if (owner is not null)
            {
                try { if (!owner.HasExited) owner.Kill(true); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); stopped = owner.HasExited; }
                catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
                try
                {
                    if (record is { AgentPid: > 0 } && WindowsSessionWindow.MatchesProcess(record.AgentPid, record.AgentStartTicks))
                    {
                        using var child = Process.GetProcessById(record.AgentPid);
                        if (child.StartTime.ToUniversalTime().Ticks == record.AgentStartTicks)
                        { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                    childStopped = record is not { AgentPid: > 0 } || !WindowsSessionWindow.MatchesProcess(record.AgentPid, record.AgentStartTicks);
                }
                catch (Exception error) { failure = (failure ?? "") + "\nChild cleanup: " + error; }
                owner.Dispose();
            }
            timeout.Cancel(); listener.Stop();
            try { await server; } catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            catch (Exception error) { failure = (failure ?? "") + "\nModel fixture: " + error; }
            if (lastScreen is not null) File.WriteAllText(Path.Combine(root, "owned-console-screen-private.txt"), lastScreen);
            var acceptanceReached = complete;
            complete = complete && stopped && childStopped && failure is null;
            File.WriteAllText(Path.Combine(root, "native-foreground-live-private.json"), JsonSerializer.Serialize(new {
                complete, stopped, childStopped, failure, root, program, helper, generation, calls, responseReleased,
                working, finished, record, foreground = true, background = false, attention = false,
                publicLifecycleEnabled = NativeGrokActivity.NativeRunLifecycleVerified, publicAlertsEnabled = NativeGrokActivity.NativeOutcomeAlertsVerified,
                scope = "One actual native foreground TUI turn with owned loopback model, normal --native-agent owner and shadow projection. No real credentials/recipients/projects, GUI clicks, background or attention acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!stopped || !childStopped) throw new IOException("Owned native process cleanup was incomplete; retained fixture proof records the failure.");
            if (failure is not null && acceptanceReached) throw new IOException("Owned native acceptance failed; retained fixture proof records the failure.");
        }
    }
    private static async Task Until(Func<bool> condition, Process owner, CancellationToken token)
    {
        while (!condition())
        {
            if (owner.HasExited) throw new IOException("Owned native console exited before the required observation.");
            await Task.Delay(50, token);
        }
    }
}
