using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class NativeAgentLocatorTests
{
    [Fact]
    public void StandaloneCurrentCodexIsFoundWithoutSelectingOldOrDaemonPackages()
    {
        var root = Fixture();
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", null);
            var paths = Paths(root);
            var locator = new NativeAgentLocator(new GrokLocator(paths), paths);
            var old = Stub(root, ".codex/packages/standalone/releases/old/bin/codex.exe");
            _ = Stub(root, ".codex/packages/app-server-daemon/current/bin/codex.exe");
            _ = Stub(root, ".sandbox-bin/codex.exe");
            _ = Stub(root, ".codex/packages/standalone/current/bin/codex.cmd");
            Assert.Null(locator.Find(new(AgentChoice.Codex, null, null)));
            var current = Stub(root, ".codex/packages/standalone/current/bin/codex.exe");
            Assert.Equal(current, locator.Find(new(AgentChoice.Codex, null, null)));
            _ = Stub(root, ".codex/packages/standalone/current/bin/claude.exe");
            Assert.Null(locator.Find(new(AgentChoice.Claude, null, null)));
            Assert.Null(locator.Find(new(AgentChoice.Grok, null, null)));
            Assert.NotEqual(old, locator.Find(new(AgentChoice.Codex, null, null)));
        }
        finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
    }

    [Fact]
    public void ExistingPathLocalAndNpmChoicesStayAheadOfStandaloneCodex()
    {
        var root = Fixture();
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", null);
            var paths = Paths(root);
            var locator = new NativeAgentLocator(new GrokLocator(paths), paths);
            _ = Stub(root, ".codex/packages/standalone/current/bin/codex.exe");
            var npm = Stub(root, "AppData/Roaming/npm/codex.cmd");
            Assert.Equal(npm, locator.Find(new(AgentChoice.Codex, null, null)));
            var local = Stub(root, ".local/bin/codex.exe");
            Assert.Equal(local, locator.Find(new(AgentChoice.Codex, null, null)));
            var onPath = Stub(root, "programs with spaces/codex.cmd");
            Environment.SetEnvironmentVariable("PATH", "\"" + Path.GetDirectoryName(onPath) + "\"");
            Assert.Equal(onPath, locator.Find(new(AgentChoice.Codex, null, null)));
            Assert.Equal(onPath, locator.Find(new(AgentChoice.Custom, Path.GetFileName(onPath), onPath)));
        }
        finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_CODEX_VERSION", "1")]
    [Trait("Category", "Integration")]
    public async Task SelectedInstalledNativeCodexRunsVersionOnlyWithAnOwnedHome()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Fixture();
        var paths = new AppPaths();
        var program = new NativeAgentLocator(new GrokLocator(paths), paths).Find(new(AgentChoice.Codex, null, null));
        Assert.NotNull(program);
        Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_NATIVE_CODEX_EXPECTED")!), program,
            StringComparer.OrdinalIgnoreCase);
        var before = Hash(program!);
        Assert.Equal(Environment.GetEnvironmentVariable("LAUNCHPAD_NATIVE_CODEX_SHA256"), before, ignoreCase: true);
        var home = Path.Combine(root, "codex-home");
        Directory.CreateDirectory(home);
        var record = new NativeLaunchRecord(root, AgentChoice.Codex, program!, Guid.NewGuid().ToString("N"));
        var start = NativeAgentTerminal.AgentStart(record);
        start.ArgumentList.Add("--version");
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["CODEX_HOME"] = home;
        foreach (var key in new[] { "CODEX_API_KEY", "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID" })
            start.Environment.Remove(key);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var version = (await output).Trim();
        var stderr = await errors;
        var after = Hash(program!);
        await File.WriteAllTextAsync(Path.Combine(root, "native-codex-version-private.json"),
            JsonSerializer.Serialize(new { schema = 1, program, workingDirectory = root, ownedHome = home,
                before, after, exitCode = process.ExitCode, version, stderr,
                scope = "Actual selected executable through native AgentStart, version only; no TUI/model/login/hook execution or VM" }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(0, process.ExitCode);
        Assert.Matches("^codex-cli [0-9]+\\.[0-9]+\\.[0-9]+$", version);
        Assert.Equal(before, after);
        Assert.False(File.Exists(Path.Combine(home, "auth.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "qemu")));
        Assert.False(Directory.Exists(Path.Combine(root, "images")));
    }

    private static AppPaths Paths(string root) => new(userProfile: root, grokHome: Path.Combine(root, "grok"),
        appDataDir: Path.Combine(root, "state"), exePath: Path.Combine(root, "LaunchPad.exe"));
    private static string Fixture()
    {
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "native-codex-discovery-20261007", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static string Stub(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Owned discovery fixture, never executed.");
        return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
