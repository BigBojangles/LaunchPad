using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsTestControlsTests
{
    [Fact]
    public void PermissionSurvivesRestartAndInvalidSavedPolicyCannotPermitTests()
    {
        var fixture = new Fixture();
        var settings = new SettingsStore(fixture.Paths);
        Assert.Equal("automatic", settings.WindowsTestPermissionFor(fixture.Project));
        settings.SaveWindowsTestPermission(fixture.Project, "confirm");
        Assert.Equal("confirm", new SettingsStore(fixture.Paths).WindowsTestPermissionFor(fixture.Project));
        var json = File.ReadAllText(fixture.Paths.SettingsFile).Replace("confirm", "unknown");
        File.WriteAllText(fixture.Paths.SettingsFile, json);
        Assert.Throws<IOException>(() => new SettingsStore(fixture.Paths).WindowsTestPermissionFor(fixture.Project));
        File.WriteAllText(fixture.Paths.SettingsFile, "{broken");
        Assert.Throws<IOException>(() => new SettingsStore(fixture.Paths));
    }

    [Fact]
    public async Task ApprovalIsBoundToExactRequestAndResultsAreRecordedAfterCollection()
    {
        var fixture = new Fixture();
        var execution = new Executor((_, _, _) => Task.FromResult(new WindowsTestExecution("finished", 7)));
        var work = fixture.Start(() => "confirm", execution);
        var pending = await fixture.Wait("awaiting-approval");
        var wrong = new WindowsTestControlCommand(1, pending.State.Generation, pending.State.RequestId, new string('0', 64),
            pending.State.InstanceId, Guid.NewGuid().ToString("N"), "approve", DateTimeOffset.UtcNow);
        var decisionPath = Path.Combine(pending.Directory, "decision.json");
        File.WriteAllText(decisionPath + ".fixture", JsonSerializer.Serialize(wrong, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(decisionPath + ".fixture", decisionPath, true);
        await Task.Delay(250);
        Assert.Equal(0, execution.Calls);
        WindowsTestControls.Send(pending, "approve");
        Assert.Equal(7, (await work.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        var result = WindowsTestControls.ReadState(pending.Directory)!;
        Assert.Equal("finished", result.Stage);
        Assert.Equal("finished", result.Outcome);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(1, execution.Calls);
        Assert.True(File.Exists(Path.Combine(pending.Directory, "response.json")));
    }

    [Theory]
    [InlineData("deny", "failed")]
    [InlineData("cancel", "canceled")]
    public async Task DenyOrCancelNeverStartsTestAndLeavesOwnerTokenActive(string action, string outcome)
    {
        var fixture = new Fixture();
        using var owner = new CancellationTokenSource();
        var execution = new Executor((_, _, _) => Task.FromResult(new WindowsTestExecution("finished", 0)));
        var work = fixture.Start(() => "confirm", execution, owner.Token);
        var pending = await fixture.Wait("awaiting-approval");
        WindowsTestControls.Send(pending, action);
        Assert.Equal(outcome, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(0, execution.Calls);
        Assert.False(owner.IsCancellationRequested);
        Assert.True(Directory.Exists(Path.Combine(pending.Directory, "workspace")));
    }

    [Fact]
    public async Task MissingApprovalExpiresRatherThanLaunchingLater()
    {
        var fixture = new Fixture();
        var execution = new Executor((_, _, _) => Task.FromResult(new WindowsTestExecution("finished", 0)));
        var result = await fixture.Start(() => "confirm", execution, approvalWait: TimeSpan.FromMilliseconds(200));
        Assert.Equal("failed", result.Outcome);
        Assert.Contains("expired", result.Error);
        Assert.Equal(0, execution.Calls);
    }

    [Fact]
    public async Task PolicyChangedImmediatelyBeforeStartCannotBypassConfirmation()
    {
        var fixture = new Fixture();
        var reads = 0;
        var execution = new Executor((_, _, _) => Task.FromResult(new WindowsTestExecution("finished", 0)));
        var result = await fixture.Start(() => ++reads == 1 ? "automatic" : "confirm", execution);
        Assert.Equal("failed", result.Outcome);
        Assert.Contains("changed", result.Error);
        Assert.Equal(0, execution.Calls);
    }

    [Fact]
    public async Task PermissionTightenedDuringBootstrapIsRejectedByPreResumeCallback()
    {
        var fixture = new Fixture();
        var permission = "automatic";
        Func<bool>? authorize = null;
        var resumed = false;
        var execution = new Executor((_, _, _) =>
        {
            permission = "confirm"; // An owned stand-in for the paused bootstrap.
            resumed = authorize!();
            return Task.FromResult(new WindowsTestExecution(resumed ? "finished" : "failed", resumed ? 0 : null));
        });
        var result = await fixture.Start(() => permission, execution, factory: (_, callback) => { authorize = callback; return execution; });
        Assert.False(resumed);
        Assert.Equal("failed", result.Outcome);
    }

    [Fact]
    public async Task AFailedControlViewCannotPreventFrozenResultDelivery()
    {
        var fixture = new Fixture();
        var result = await fixture.Start(() => "automatic", new FailedView(), controlled: false);
        Assert.Equal("finished", result.Outcome);
        Assert.Equal(9, result.ExitCode);
    }

    [Fact]
    public async Task RunningCancelReturnsRetainedArtifactAndDoesNotCancelVmOwner()
    {
        var fixture = new Fixture(artifact: true);
        using var owner = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new Executor(async (_, copy, token) =>
        {
            File.WriteAllText(Path.Combine(copy, "artifact.txt"), "kept canceled output");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new WindowsTestExecution("finished", 0);
        });
        var work = fixture.Start(() => "automatic", execution, owner.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await fixture.Wait("running");
        WindowsTestControls.Send(running, "cancel");
        var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("canceled", result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.False(owner.IsCancellationRequested);
        Assert.Equal("kept canceled output", File.ReadAllText(Path.Combine(running.Directory, "results", "artifacts", "artifact.txt")));
    }

    [Fact]
    public async Task ExplicitShowAndReturnAreAcknowledgedWithoutEndingTest()
    {
        var fixture = new Fixture();
        var desktop = new Desktop();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<IWindowsTestDesktopControl?>? ready = null;
        var execution = new Executor(async (_, _, token) =>
        {
            ready!(desktop);
            await finish.Task.WaitAsync(token);
            ready(null);
            return new("finished", 0);
        });
        var work = fixture.Start(() => "automatic", execution, factory: (callback, _) => { ready = callback; return execution; });
        var running = await fixture.Wait("running", state => state.DesktopAvailable);
        var show = WindowsTestControls.Send(running, "show");
        await fixture.Wait("running", state => state.AcknowledgedCommand == show);
        Assert.Equal(1, desktop.Shown);
        var returned = WindowsTestControls.Send(running, "return");
        await fixture.Wait("running", state => state.AcknowledgedCommand == returned);
        Assert.Equal(1, desktop.Returned);
        Assert.False(work.IsCompleted);
        finish.SetResult();
        Assert.Equal("finished", (await work.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    private sealed class Desktop : IWindowsTestDesktopControl
    {
        public int Shown, Returned;
        public void Activate() => Shown++;
        public bool ReturnToLaunchPad() { Returned++; return true; }
    }
    private sealed class FailedView : IControlledWindowsTestExecutor
    {
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string copy, CancellationToken token)
            => Task.FromResult(new WindowsTestExecution("finished", 9));
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string copy, string hash, CancellationToken token)
            => ExecuteAsync(request, copy, token);
        public void RecordResult(WindowsTestResponse response) => throw new IOException("Owned unavailable result view");
    }
    private sealed class Executor(Func<WindowsTestRequest, string, CancellationToken, Task<WindowsTestExecution>> run) : IWindowsTestExecutor
    {
        public int Calls;
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string copy, CancellationToken token)
        { Calls++; return run(request, copy, token); }
    }
    private sealed class Fixture
    {
        private readonly string _generation = Guid.NewGuid().ToString("N");
        public string Project { get; }
        public AppPaths Paths { get; }
        private readonly WindowsTestRequest _request;
        public Fixture(bool artifact = false)
        {
            var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-controls-20261007", "fixture-" + Guid.NewGuid().ToString("N"));
            Project = Path.Combine(root, "project"); Directory.CreateDirectory(Project);
            Paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "settings"));
            _request = new(1, _generation, Guid.NewGuid().ToString("N"), "dotnet", null, ".", ["build"], false, 30, artifact ? ["artifact.txt"] : [], []);
        }
        public async Task<WindowsTestResponse> Start(Func<string> permission, IWindowsTestExecutor execution,
            CancellationToken token = default, TimeSpan? approvalWait = null, Func<Action<IWindowsTestDesktopControl?>, Func<bool>, IWindowsTestExecutor>? factory = null, bool controlled = true)
        {
            using var packet = new MemoryStream();
            await WindowsTestProtocol.WriteRequestAsync(packet, _request, Project, token); packet.Position = 0;
            using var output = new MemoryStream();
            var root = WindowsTestControls.ProjectRoot(Paths, Project);
            return await new WindowsTestBridge(root, _generation, controlled
                ? new ControlledWindowsTestExecutor(root, permission, factory ?? ((_, _) => execution), approvalWait)
                : execution).ProcessAsync(packet, output, token);
        }
        public async Task<WindowsTestControlEntry> Wait(string stage, Func<WindowsTestControlState, bool>? condition = null)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var entry = WindowsTestControls.List(Paths, Project).FirstOrDefault();
                if (entry?.State.Stage == stage && (condition is null || condition(entry.State))) return entry;
                await Task.Delay(20, limit.Token);
            }
        }
    }
}
