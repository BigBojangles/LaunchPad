using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class NativeAgentTests
{
    [Fact]
    public void ExplicitModesSurviveReloadAndNativePackageIgnoresVmAvailability()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-native-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "state"), exePath: Path.Combine(root, "LaunchPad.exe"));
            var settings = new SettingsStore(paths);
            var project = Path.Combine(root, "project");
            Assert.Equal("fenced", settings.LaunchModeFor(project));
            settings.SaveLaunchMode(project, "native");
            settings.SaveAgent(project, AgentChoice.Claude, null, null);
            var reloaded = new SettingsStore(paths);
            Assert.Equal("native", reloaded.LaunchModeFor(project));
            Assert.Equal(AgentChoice.Claude, reloaded.AgentFor(project).Id);
            var customProgram = Path.Combine(root, "custom program.cmd");
            File.WriteAllText(customProgram, "owned fixture");
            reloaded.SaveAgent(project, AgentChoice.Custom, Path.GetFileName(customProgram), customProgram, "native");
            Assert.Equal(customProgram, new SettingsStore(paths).AgentFor(project).SourceFile);
            Assert.Throws<ArgumentException>(() => reloaded.SaveLaunchMode(project, "automatic"));
            File.WriteAllText(Path.Combine(root, "native-only.txt"), "owned fixture");
            var log = new SetupLog(paths);
            var locator = new GrokLocator(paths);
            var runtime = new WindowsProjectRuntime(new GrokSetup(paths, locator, log), new ProjectLauncher(locator, log), log, paths, reloaded);
            Assert.True(runtime.NativeOnly);
            Assert.False(runtime.HasVirtualMachine);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(runtime.FenceStartAvailability.Blocked);
                Assert.Throws<InvalidOperationException>(() => runtime.CreateFencedSession());
            }
            Assert.False(Directory.Exists(Path.Combine(root, "qemu")));
            Assert.False(Directory.Exists(Path.Combine(root, "images")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_FIXTURE", "1")]
    [Trait("Category", "Integration")]
    public async Task TwoOwnedNativeWrappersKeepTheirAgentProjectAndLifetimeAcrossDesktopRecreation()
    {
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "native-20261006", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "state"),
            exePath: Environment.GetEnvironmentVariable("LAUNCHPAD_NATIVE_EXE")
                ?? Path.Combine(RepositoryRoot(), "src", "LaunchPad", "bin", "Debug", "net8.0", "LaunchPad.exe"));
        var log = new SetupLog(paths);
        var programs = Path.Combine(root, "programs with spaces & $ punctuation");
        Directory.CreateDirectory(programs);
        var projects = new[] { Path.Combine(root, "first project & apostrophe's %name%"), Path.Combine(root, "second project") };
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var systemPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
        try
        {
            foreach (var agent in new[] { "codex", "claude" })
                await File.WriteAllTextAsync(Path.Combine(programs, agent + ".cmd"),
                    "@echo off\r\necho " + agent + "> native-agent.txt\r\npowershell -NoLogo -NoProfile -Command \"$deadline=(Get-Date).AddSeconds(25); while (!(Test-Path -LiteralPath 'release-native') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }\"\r\nexit /b 0\r\n");
            Environment.SetEnvironmentVariable("PATH", programs + ";" + systemPath + ";" + Path.Combine(systemPath, "WindowsPowerShell", "v1.0"));
            var settings = new SettingsStore(paths);
            var launcher = new ProjectLauncher(new GrokLocator(paths), log);
            for (var i = 0; i < projects.Length; i++)
            {
                Directory.CreateDirectory(projects[i]);
                settings.RememberProject("owned " + i, projects[i]);
                settings.SaveLaunchMode(projects[i], "native");
                settings.SaveAgent(projects[i], i == 0 ? "codex" : "claude", null, null);
                Assert.True(launcher.TryLaunch(projects[i], settings.AgentFor(projects[i]), out var error), error);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var i = 0; i < projects.Length; i++)
            {
                var project = projects[i];
                await WindowsConsoleProbeTests.Until(() => File.Exists(Path.Combine(project, "native-agent.txt")) && NativeAgentTerminal.IsLive(launcher.Record(project)), timeout.Token);
                Assert.Equal(i == 0 ? "codex" : "claude", (await File.ReadAllTextAsync(Path.Combine(project, "native-agent.txt"))).Trim());
            }
            var reopened = new ProjectLauncher(new GrokLocator(paths), log);
            Assert.All(projects, project => Assert.True(reopened.WasLaunched(project)));
            Assert.NotEqual(reopened.Record(projects[0])!.Pid, reopened.Record(projects[1])!.Pid);
            var runtime = new WindowsProjectRuntime(new GrokSetup(paths, new GrokLocator(paths), log), reopened, log, paths, new SettingsStore(paths));
            Assert.Equal("codex", runtime.DescribeHost(projects[0])!.AgentId);
            Assert.Equal("claude", runtime.DescribeHost(projects[1])!.AgentId);
            Assert.Equal(LaunchPad.Models.SessionLifecycle.Running, runtime.DescribeHost(projects[0])!.State);
            Assert.Contains("activity is unavailable", runtime.DescribeHost(projects[0])!.Error);
            foreach (var project in projects)
                await WindowsConsoleProbeTests.Until(() => runtime.DescribeHost(project)?.Window is not null, timeout.Token);
            Assert.NotEqual(runtime.DescribeHost(projects[0])!.WindowHandle, runtime.DescribeHost(projects[1])!.WindowHandle);
            Assert.Equal(reopened.Record(projects[0])!.Pid, runtime.DescribeHost(projects[0])!.Window!.ClientPid);
            foreach (var project in projects) await File.WriteAllTextAsync(Path.Combine(project, "release-native"), "owned release");
            foreach (var project in projects)
            {
                await WindowsConsoleProbeTests.Until(() => reopened.Record(project) is { State: "stopped", ExitCode: 0 } && !reopened.WasLaunched(project), timeout.Token);
                Assert.Equal("stopped", reopened.Record(project)!.State);
            }
            // All retained diagnostics and project writes belong to this fixture.
        }
        finally
        {
            foreach (var project in projects) if (Directory.Exists(project)) await File.WriteAllTextAsync(Path.Combine(project, "release-native"), "owned release");
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void AnUnstartableNativeHelperCannotMakeTheNextAttemptLookSuccessful()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-native-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var program = Path.Combine(root, "owned.exe");
            File.WriteAllText(program, "owned fixture, never executed");
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "state"), exePath: Path.Combine(root, "missing-helper.exe"));
            var launcher = new ProjectLauncher(new GrokLocator(paths), new SetupLog(paths));
            var agent = new AgentLaunch(AgentChoice.Custom, "owned.exe", program);
            Assert.False(launcher.TryLaunch(root, agent, out _));
            Assert.False(launcher.WasLaunched(root));
            Assert.Equal("failed", launcher.Record(root)!.State);
            Assert.False(launcher.TryLaunch(root, agent, out _));
            Assert.False(launcher.WasLaunched(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
