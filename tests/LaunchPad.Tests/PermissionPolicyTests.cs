using System.Diagnostics;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class PermissionPolicyTests
{
    [Fact]
    public void SavedPresetsPreserveLegacyConfirmationAndSurviveProjectPathChanges()
    {
        var fixture = new Fixture();
        fixture.Settings.RememberProject("owned", fixture.Project);
        fixture.Settings.SaveWindowsTestPermission(fixture.Project, "confirm");
        fixture.Settings.SavePermissionPolicy(fixture.Project, new(1, "strict", [new("API.Example.com", 443)]));
        var restored = fixture.Reload();
        Assert.Equal("confirm", restored.WindowsTestPermissionFor(fixture.Project));
        Assert.Equal("api.example.com", Assert.Single(restored.PermissionPolicyFor(fixture.Project.ToUpperInvariant()).Destinations).Host);
        var moved = Path.Combine(fixture.Root, "renamed-metadata-only");
        restored.UpdateProjectPath(fixture.Project, "new label", moved);
        Assert.Equal("strict", fixture.Reload().PermissionPolicyFor(moved).Preset);
        Assert.Equal("confirm", fixture.Reload().WindowsTestPermissionFor(moved));
        Assert.True(Directory.Exists(fixture.Project));
    }

    [Theory]
    [InlineData("administrator", "api.example.com", 443)]
    [InlineData("standard", "*.example.com", 443)]
    [InlineData("strict", "https://api.example.com", 443)]
    [InlineData("strict", "api.example.com", 0)]
    public void InvalidPolicyCannotReplacePreviouslySavedChoice(string preset, string host, int port)
    {
        var fixture = new Fixture();
        fixture.Settings.SavePermissionPolicy(fixture.Project, ProjectPermissionPolicy.Standard);
        var before = File.ReadAllBytes(fixture.Paths.SettingsFile);
        Assert.Throws<InvalidDataException>(() => fixture.Settings.SavePermissionPolicy(fixture.Project, new(1, preset, [new(host, port)])));
        Assert.Equal(before, File.ReadAllBytes(fixture.Paths.SettingsFile));
    }

    [Fact]
    public void UnknownPersistedPresetRefusesInsteadOfFallingBack()
    {
        var fixture = new Fixture();
        fixture.Settings.SavePermissionPolicy(fixture.Project, ProjectPermissionPolicy.Standard);
        File.WriteAllText(fixture.Paths.SettingsFile, File.ReadAllText(fixture.Paths.SettingsFile).Replace("standard", "unknown"));
        Assert.Throws<InvalidDataException>(() => fixture.Reload().PermissionPolicyFor(fixture.Project));
    }

    [Fact]
    public void StaleWindowCannotOverwriteAnotherCompletedPolicyEdit()
    {
        var fixture = new Fixture();
        fixture.Settings.SavePermissionPolicy(fixture.Project, ProjectPermissionPolicy.Standard);
        var stale = fixture.Reload();
        fixture.Reload().SavePermissionPolicy(fixture.Project, new(1, "troubleshoot", []));
        Assert.Throws<IOException>(() => stale.SavePermissionPolicy(fixture.Project, new(1, "strict", [])));
        Assert.Equal("troubleshoot", fixture.Reload().PermissionPolicyFor(fixture.Project).Preset);
        Assert.Equal("standard", stale.PermissionPolicyFor(fixture.Project).Preset);
    }

    [Fact]
    public async Task UnsupportedStrictStopsBeforeSweepDiskOrMachineStart()
    {
        var fixture = new Fixture();
        fixture.Settings.SavePermissionPolicy(fixture.Project, new(1, "strict", []));
        var calls = 0;
        var sessions = Path.Combine(fixture.Root, "not-created-sessions");
        var fence = new FenceSession(new SetupLog(fixture.Paths), launchAccountReady: () => true,
            keptImage: () => { calls++; return Path.Combine(fixture.Root, "debian-12-builder-test.qcow2"); }, sessionsRoot: () => sessions,
            starter: _ => { calls++; return true; }, readSettings: fixture.Reload);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fence.StartAsync(fixture.Project, null, null, CancellationToken.None));
        Assert.Contains("destination-filtered", error.Message);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(sessions));
    }

    [Fact]
    public void StrictForcesConfirmationWithoutChangingSavedBaseChoice()
    {
        var fixture = new Fixture();
        var permission = PermissionPolicies.TestPermission(new(1, "strict", []), "automatic", fixture.Project, fixture.Owner, fixture.Request,
            null, null, fixture.Now, true, fixture.Uptime);
        Assert.Equal("confirm", permission);
        Assert.Equal("automatic", fixture.Settings.WindowsTestPermissionFor(fixture.Project));
    }

    [Fact]
    public void TemporaryGrantMatchesNamedToolAndBothFixedClocks()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Grant();
        Assert.Equal("automatic", fixture.Permission(fixture.Now.AddMinutes(29), fixture.Uptime + 1740000));
        Assert.Equal("confirm", fixture.Permission(fixture.Now.AddMinutes(30), fixture.Uptime + 1799999));
        Assert.Equal("confirm", fixture.Permission(fixture.Now.AddMinutes(1), fixture.Uptime + 1800000));
        Assert.Equal("confirm", fixture.Permission(fixture.Now.AddMinutes(-1), fixture.Uptime + 1));
        Assert.Equal("confirm", fixture.Permission(fixture.Now.AddMinutes(1), fixture.Uptime - 1));
        Assert.Equal("confirm", fixture.Permission(fixture.Now, fixture.Uptime, fixture.Request with { Tool = "powershell" }));
        Assert.Equal("confirm", fixture.Permission(fixture.Now, fixture.Uptime, fixture.Request with { Tool = "project", Program = "app.exe" }));
    }

    [Fact]
    public void SessionEndAndOwnerReplacementInvalidateGrants()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Grant();
        Assert.Throws<InvalidDataException>(() => fixture.Permission(fixture.Now, fixture.Uptime, owner: fixture.Owner with { OwnerStartTicks = fixture.Owner.OwnerStartTicks + 1 }));
        Assert.Throws<InvalidDataException>(() => fixture.Permission(fixture.Now, fixture.Uptime, fixture.Request with { Generation = Guid.NewGuid().ToString("N") }));
        Assert.Throws<InvalidDataException>(() => fixture.Permission(fixture.Now, fixture.Uptime, live: false));
        PermissionPolicies.End(fixture.Paths, fixture.Owner, fixture.Now.AddMinutes(1));
        Assert.Equal("confirm", fixture.Permission(fixture.Now.AddMinutes(2), fixture.Uptime + 120000));
        Assert.Throws<InvalidOperationException>(() => fixture.Grant());
    }

    [Fact]
    public void ChangedSavedPresetDoesNotRelabelAppliedSessionOrKeepException()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Grant();
        fixture.Settings.SavePermissionPolicy(fixture.Project, new(1, "strict", []));
        Assert.Equal("troubleshoot", PermissionPolicies.Applied(fixture.Paths, fixture.Project, fixture.Owner)!.Policy.Preset);
        Assert.Equal("confirm", fixture.Permission(fixture.Now, fixture.Uptime));
        fixture.Settings.SavePermissionPolicy(fixture.Project, ProjectPermissionPolicy.Standard);
        Assert.Equal("confirm", fixture.Permission(fixture.Now, fixture.Uptime));
    }

    [Fact]
    public async Task OverlappingRevokeCannotBeOverwrittenByAGrantReadBeforeIt()
    {
        var fixture = new Fixture(); fixture.Begin();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var grant = Task.Run(() => PermissionPolicies.Grant(fixture.Paths, fixture.Settings, fixture.Project, fixture.Owner, "dotnet", null, fixture.Now,
            () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); return true; }, fixture.Uptime));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var revoke = Task.Run(() => PermissionPolicies.Revoke(fixture.Paths, fixture.Project, fixture.Owner));
        await Task.Delay(50);
        Assert.False(revoke.IsCompleted);
        release.Set(); await Task.WhenAll(grant, revoke).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(PermissionPolicies.Exceptions(fixture.Paths, fixture.Project, fixture.Owner)!.Grants);
        Assert.Equal("confirm", fixture.Permission(fixture.Now, fixture.Uptime));
    }

    [Fact]
    public void BackwardClockDisablesTemporaryGrantsUntilSessionEnd()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Grant();
        var wall = fixture.Now; var ticks = fixture.Uptime;
        var clock = new PermissionClock(() => wall, () => ticks);
        Assert.True(clock.Read().Valid);
        wall = wall.AddSeconds(-1); ticks += 1000;
        Assert.False(clock.Read().Valid);
        wall = fixture.Now.AddMinutes(1); ticks += 60000;
        Assert.False(clock.Read().Valid);
        PermissionPolicies.DisableTemporary(fixture.Paths, fixture.Owner, "Clock moved backward");
        Assert.Equal("confirm", fixture.Permission(wall, ticks));
        Assert.Throws<InvalidOperationException>(() => fixture.Grant());
    }

    [Fact]
    public void MalformedStoredGrantFailsClosedWithAValidationError()
    {
        var fixture = new Fixture(); fixture.Begin(); var grant = fixture.Grant();
        var file = Path.Combine(WindowsTestControls.ProjectRoot(fixture.Paths, fixture.Project), fixture.Owner.Generation + "-exceptions.json");
        void Save(TemporaryTestPermission changed) => File.WriteAllText(file, JsonSerializer.Serialize(new SessionPermissionExceptions(1, fixture.Owner.Generation, [changed], null), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Save(grant with { ProjectPath = "bad\0path" });
        Assert.Throws<InvalidDataException>(() => fixture.Permission(fixture.Now, fixture.Uptime));
        Save(grant with { IssuedUtc = DateTimeOffset.MaxValue, ExpiresUtc = DateTimeOffset.MaxValue });
        Assert.Throws<InvalidDataException>(() => fixture.Permission(fixture.Now, fixture.Uptime));
        Save(grant with { ExpiresUptimeMs = fixture.Uptime + 1800001 });
        Assert.Throws<InvalidDataException>(() => fixture.Permission(fixture.Now, fixture.Uptime));
    }

    [Fact]
    public async Task OwnerMonitorStopsAnUnconfirmedTestWhenItsNamedPermissionExpires()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Grant();
        var ticks = fixture.Uptime;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = WindowsTestControls.ProjectRoot(fixture.Paths, fixture.Project);
        var workspace = Path.Combine(root, fixture.Request.Generation, fixture.Request.RequestId, "workspace"); Directory.CreateDirectory(workspace);
        var controlled = new ControlledWindowsTestExecutor(root, () => "confirm", (_, authorize) => new Executor(async token =>
        {
            Assert.True(authorize()); started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new("finished", 0);
        }), requestPermission: request => fixture.Permission(fixture.Now, Interlocked.Read(ref ticks), request));
        var work = controlled.ExecuteAsync(fixture.Request, workspace, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Interlocked.Exchange(ref ticks, fixture.Uptime + 1800000);
        var result = await work.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("canceled", result.Outcome);
        Assert.Contains("expired or changed", result.Error);
    }

    [Fact]
    public void FailedClockWarningWriteCannotEnableALaterRequest()
    {
        var fixture = new Fixture(); fixture.Begin(); fixture.Grant();
        var wall = fixture.Now; var ticks = fixture.Uptime;
        var clock = new PermissionClock(() => wall, () => ticks);
        Assert.Equal("automatic", PermissionPolicies.ForRequest(fixture.Paths, fixture.Project, fixture.Owner, fixture.Request, clock));
        wall = wall.AddSeconds(-1); ticks += 1000;
        var appliedPath = Path.Combine(WindowsTestControls.ProjectRoot(fixture.Paths, fixture.Project), fixture.Owner.Generation + "-permissions.json");
        using (var held = new FileStream(appliedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<UnauthorizedAccessException>(() => PermissionPolicies.ForRequest(fixture.Paths, fixture.Project, fixture.Owner, fixture.Request, clock));
            Assert.Throws<UnauthorizedAccessException>(() => PermissionPolicies.ForRequest(fixture.Paths, fixture.Project, fixture.Owner, fixture.Request, clock));
        }
        wall = fixture.Now.AddSeconds(1);
        Assert.Equal("confirm", PermissionPolicies.ForRequest(fixture.Paths, fixture.Project, fixture.Owner, fixture.Request, clock));
        Assert.NotNull(PermissionPolicies.Applied(fixture.Paths, fixture.Project, fixture.Owner)!.TemporaryPermissionsDisabledReason);
    }

    [Fact]
    public void WritableHandoffMetadataCannotChangeAnyTrustedOwnerField()
    {
        var fixture = new Fixture();
        var expected = fixture.Owner with { DesktopPid = 222, DesktopStartTicks = 333 };
        SessionGuardian.RequireMatchingHandoff(expected, expected);
        var changed = new[]
        {
            expected with { OwnerPid = expected.OwnerPid + 1 }, expected with { OwnerStartTicks = expected.OwnerStartTicks + 1 },
            expected with { MachinePid = expected.MachinePid + 1 }, expected with { MachineStartTicks = expected.MachineStartTicks + 1 },
            expected with { QmpPort = expected.QmpPort + 1 }, expected with { Generation = Guid.NewGuid().ToString("N") },
            expected with { AgentId = "claude" }, expected with { ProjectPath = fixture.Root },
            expected with { DesktopPid = 223 }, expected with { DesktopStartTicks = 334 }, expected with { TerminalPid = 444 }
        };
        Assert.All(changed, value => Assert.Throws<InvalidDataException>(() => SessionGuardian.RequireMatchingHandoff(value, expected)));
    }

    private sealed class Executor(Func<CancellationToken, Task<WindowsTestExecution>> action) : IWindowsTestExecutor
    {
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string copy, CancellationToken token) => action(token);
    }
    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "permissions-20261007", "owned-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }
        public AppPaths Paths { get; }
        public SettingsStore Settings { get; }
        public SessionOwnerIdentity Owner { get; }
        public WindowsTestRequest Request { get; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public long Uptime { get; } = Environment.TickCount64;
        public Fixture()
        {
            Project = Path.Combine(Root, "project"); Directory.CreateDirectory(Project);
            Paths = new AppPaths(userProfile: Root, appDataDir: Path.Combine(Root, "settings")); Settings = new(Paths);
            using var process = Process.GetCurrentProcess(); var stamp = process.StartTime.ToUniversalTime().Ticks;
            Owner = new(process.Id, stamp, process.Id, stamp, PortChoice.First, Guid.NewGuid().ToString("N"), "codex", Project);
            Request = new(1, Owner.Generation, Guid.NewGuid().ToString("N"), "dotnet", null, ".", ["build"], false, 30, [], []);
        }
        public SettingsStore Reload() => new(Paths);
        public void Begin()
        { Settings.SaveWindowsTestPermission(Project, "confirm"); Settings.SavePermissionPolicy(Project, new(1, "troubleshoot", [])); PermissionPolicies.Begin(Paths, Owner, Settings.PermissionPolicyFor(Project), Now); }
        public TemporaryTestPermission Grant() => PermissionPolicies.Grant(Paths, Settings, Project, Owner, "dotnet", null, Now, () => true, Uptime);
        public string Permission(DateTimeOffset now, long ticks, WindowsTestRequest? request = null, SessionOwnerIdentity? owner = null, bool live = true)
        {
            owner ??= Owner;
            return PermissionPolicies.TestPermission(Reload().PermissionPolicyFor(Project), Reload().WindowsTestPermissionFor(Project), Project, owner, request ?? Request,
                PermissionPolicies.Applied(Paths, Project, owner), PermissionPolicies.Exceptions(Paths, Project, owner), now, live, ticks);
        }
    }
}
