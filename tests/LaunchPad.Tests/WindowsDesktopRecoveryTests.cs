using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class WindowsDesktopRecoveryTests
{
    [EnvironmentFact("LAUNCHPAD_WINDOWS_DESKTOP_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualRecoveryWindowPreservesUnreadableRecordsAndNextStartupUsesTheSamePrivatePaths()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "desktop-recovery-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(root, "settings"));
        var settings = Path.Combine(root, "settings", "settings.json");
        await File.WriteAllTextAsync(settings, "null");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var stopped = NativeDesktopFixture.Start(root);
        Process? repaired = null;
        var passed = false;
        DesktopState? initial = null, final = null;
        try
        {
            async Task<DesktopState> State(Process process, Func<DesktopState, bool> expected)
            {
                DesktopState? result = null;
                await WindowsConsoleProbeTests.Until(() =>
                {
                    Assert.False(process.HasExited, "The native recovery fixture ended; see private evidence.");
                    result = NativeDesktopFixture.Read(root);
                    return result?.Pid == process.Id && expected(result);
                }, deadline.Token);
                return result!;
            }
            initial = await State(stopped, state => state.Recovery);
            Assert.NotEqual(0, initial.Window);
            Assert.Equal("null", await File.ReadAllTextAsync(settings));
            Assert.False(File.Exists(Path.Combine(root, "settings", "projects.json")));
            Assert.True(File.Exists(Path.Combine(root, "settings", "logs", "setup.log")));
            NativeDesktopInput.CloseWindow(stopped, initial);
            await stopped.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, stopped.ExitCode);
            // Explicit repair of this owned fixture; production never replaces it.
            await File.WriteAllTextAsync(Path.Combine(root, "settings-original-private.txt"), "null");
            await File.WriteAllTextAsync(settings, "{}");
            repaired = NativeDesktopFixture.Start(root);
            final = await State(repaired, state => state.HomeReady && !state.Recovery);
            Assert.Equal("Native project", final.DisplayName);
            Assert.True(File.Exists(Path.Combine(root, "settings", "projects.json")));
            NativeDesktopInput.CloseWindow(repaired, final);
            await repaired.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, repaired.ExitCode);
            passed = true;
        }
        finally
        {
            if (!stopped.HasExited) { stopped.Kill(); await stopped.WaitForExitAsync(); }
            if (repaired is not null) { if (!repaired.HasExited) { repaired.Kill(); await repaired.WaitForExitAsync(); } repaired.Dispose(); }
            await File.WriteAllTextAsync(Path.Combine(root, "desktop-recovery-private.json"), JsonSerializer.Serialize(new
            {
                passed, initial, final, root,
                limitations = "Actual owned Win32 Avalonia recovery/home windows, private settings and scoped logging, preserved malformed record, explicit fixture-file repair and process restart, verified WM_CLOSE. No pointer input, Retry/folder button invocation, user-record change, VM, sign-in or complete UI acceptance."
            }, NativeDesktopFixture.Json));
        }
    }
}
