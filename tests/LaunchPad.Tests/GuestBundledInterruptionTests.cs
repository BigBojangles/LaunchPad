using System.Diagnostics;
using System.Text.Json;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using Fixture = LaunchPad.Tests.GuestIndependentOwnerTests.Fixture;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class GuestBundledInterruptionTests
{
    [EnvironmentTheory("LAUNCHPAD_BUNDLED_INTERRUPTION", "1")]
    [Trait("Category", "Integration")]
    [InlineData("grok")]
    [InlineData("codex")]
    [InlineData("claude")]
    public Task ActualAgentTerminalDeathPreservesItsInProgressEditAndAnotherSession(string agent) => Run(agent);

    [EnvironmentTheory("LAUNCHPAD_BUNDLED_INTERRUPTION", "1")]
    [Trait("Category", "Integration")]
    [InlineData("grok", "missing-helper")]
    [InlineData("codex", "ignore-term")]
    public Task UnverifiedChildCleanupRefusesACompleteReturnAndRetainsRecovery(string agent, string fault) => Run(agent, fault);

    private static async Task Run(string agent, string? fault = null)
    {
        var runtime = PublicRuntime.Ensure(new SetupLog(new AppPaths()));
        var selectedHash = HashFile(runtime.KeptImage);
        var template = Path.GetFullPath(Environment.GetEnvironmentVariable("LAUNCHPAD_POLICY_TEMPLATE")
            ?? throw new InvalidOperationException("Explicit owned policy candidate required."));
        Assert.StartsWith(Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration") + Path.DirectorySeparatorChar,
            template, StringComparison.OrdinalIgnoreCase);
        var templateHash = HashFile(template);
        await RunTool("icacls.exe", template, "/grant", TestUserRunner.UserName + ":R", "*S-1-5-12:R");
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "guest", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await RunTool("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var desktopInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        desktopInfo.ArgumentList.Add("/c"); desktopInfo.ArgumentList.Add("pause");
        using var desktop = Process.Start(desktopInfo)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Fixture? first = null, second = null;
        var passed = false;
        DateTime? terminalKilledUtc = null;
        try
        {
            first = await Fixture.Start(runtime, template, Path.Combine(root, "first"), desktop, true, deadline.Token, agent, fault);
            second = await Fixture.Start(runtime, template, Path.Combine(root, "second"), desktop, true, deadline.Token);
            Assert.False(first.Machine.HasExited); Assert.False(second.Machine.HasExited);
            Assert.NotEqual(first.Port, second.Port);
            Assert.NotEqual(first.Identity.Generation, second.Identity.Generation);
            desktop.Kill(); await desktop.WaitForExitAsync(deadline.Token);
            await second.ExpectSize(91, 27, deadline.Token);
            Assert.False(first.Owner.HasExited);
            await first.ExpectLiveBundledChild(deadline.Token);
            terminalKilledUtc = DateTime.UtcNow;
            first.Terminal.Process.Kill();
            await first.Terminal.Process.WaitForExitAsync(deadline.Token);
            await first.Owner.WaitForExitAsync(deadline.Token);
            await first.VerifyBundledWitness(template);
            if (fault is null) first.VerifyReturn(); else first.VerifyRefusedReturn();
            Assert.False(second.Machine.HasExited); Assert.False(second.Owner.HasExited);
            await second.ExpectSize(89, 26, deadline.Token);
            second.Type("exit\r");
            await second.WaitScreen("OWNED-EXIT:second", deadline.Token);
            second.Proxy.CloseConsole();
            await second.Terminal.Process.WaitForExitAsync(deadline.Token);
            await second.Owner.WaitForExitAsync(deadline.Token);
            second.VerifyReturn();
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(first.Project, "nonce.txt")));
            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(second.Project, "nonce.txt")));
            Assert.False(File.Exists(Path.Combine(first.Project, "guest-only.txt")));
            Assert.False(File.Exists(Path.Combine(second.Project, "guest-only.txt")));
            Assert.Equal(templateHash, HashFile(template));
            Assert.Equal(selectedHash, HashFile(runtime.KeptImage));
            passed = true;
        }
        finally
        {
            if (!desktop.HasExited) { desktop.Kill(); await desktop.WaitForExitAsync(); }
            first?.Dispose(); second?.Dispose();
            await File.WriteAllTextAsync(Path.Combine(root, "bundled-interruption-private.json"), JsonSerializer.Serialize(new
            {
                passed, agent, fault, root, terminalKilledUtc, template, templateHash, selectedHash,
                finalTemplateHash = HashFile(template), finalSelectedHash = HashFile(runtime.KeptImage),
                first = first?.Report(), second = second?.Report(),
                appAssemblyHash = HashFile(typeof(SessionGuardian).Assembly.Location),
                limitations = "Actual native headless CLI replaces the foreground terminal process, explicit enforce-policy transition, controlled loopback model and real shell child. Production terminal/guardian/export/shutdown; second canonical custom session stays usable. Desktop identity is an owned departed process, not this case's UI launch/close. No credentials, real project, host apply, complete interactive job control, power-loss, full security/performance/install or Mac acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
