using System.Diagnostics;
using System.Text.Json;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class WindowsDesktopWorkflowTests
{
    [EnvironmentFact("LAUNCHPAD_WINDOWS_DESKTOP_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualAvaloniaDesktopUsesNativeInputAndRetainsNamesSettingsAndOneTimeTips()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "desktop-" + Guid.NewGuid().ToString("N")[..12]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var passed = false;
        using var desktop = NativeDesktopFixture.Start(root);
        Process? reopened = null;
        DesktopState? final = null;
        try
        {
            async Task<DesktopState> Until(Process process, Func<DesktopState, bool> predicate)
            {
                DesktopState? state = null;
                await WindowsConsoleProbeTests.Until(() =>
                {
                    Assert.False(process.HasExited, "The owned desktop ended; see its private error report.");
                    state = NativeDesktopFixture.Read(root);
                    return state is not null && state.Pid == process.Id && state.StartTicks == process.StartTime.ToUniversalTime().Ticks && predicate(state);
                }, deadline.Token);
                return state!;
            }
            var state = await Until(desktop, s => s.HomeReady && s.MenuTipSeen && s.Controls.ContainsKey("name"));
            Assert.True(state.MenuTipVisible);
            NativeDesktopInput.Click(desktop, state, "name", right: true);
            state = await Until(desktop, s => s.MenuOpen);
            Assert.Equal(new[] { "Open project", "Open folder", "Saved work", "Send files", "Agent", "More…" }, state.MenuCaptions);
            NativeDesktopInput.Key(desktop, 0x1b);
            state = await Until(desktop, s => !s.MenuOpen);
            NativeDesktopInput.Click(desktop, state, "menu");
            await Until(desktop, s => s.MenuOpen);
            NativeDesktopInput.Key(desktop, 0x1b);
            state = await Until(desktop, s => !s.MenuOpen);
            NativeDesktopInput.Click(desktop, state, "name");
            await Until(desktop, s => s.Editing);
            NativeDesktopInput.Text(desktop, "Cancelled native label");
            NativeDesktopInput.Key(desktop, 0x1b);
            state = await Until(desktop, s => !s.Editing);
            Assert.Equal("Native project", state.DisplayName);
            NativeDesktopInput.Click(desktop, state, "name");
            await Until(desktop, s => s.Editing);
            NativeDesktopInput.Text(desktop, "Native renamed project");
            NativeDesktopInput.Key(desktop, 0x0d);
            state = await Until(desktop, s => !s.Editing && s.DisplayName == "Native renamed project");
            var project = Path.Combine(root, "Grok", "Projects", "Native project");
            Assert.True(Directory.Exists(project));
            Assert.False(Directory.Exists(Path.Combine(root, "Grok", "Projects", "Native renamed project")));
            NativeDesktopInput.Click(desktop, state, "settings");
            state = await Until(desktop, s => s.PreferencesOpen && s.Controls.ContainsKey("tips"));
            NativeDesktopInput.Click(desktop, state, "tips");
            NativeDesktopInput.Click(desktop, state, "agent");
            NativeDesktopInput.Key(desktop, 0x24); // Home, then Claude.
            NativeDesktopInput.Key(desktop, 0x28);
            NativeDesktopInput.Key(desktop, 0x28);
            NativeDesktopInput.Key(desktop, 0x0d);
            NativeDesktopInput.Click(desktop, state, "save");
            state = await Until(desktop, s => !s.PreferencesOpen && !s.ShowTips && s.DefaultAgent == "claude");
            NativeDesktopInput.Click(desktop, state, "close");
            await desktop.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, desktop.ExitCode);
            reopened = NativeDesktopFixture.Start(root);
            final = await Until(reopened, s => s.HomeReady && s.Controls.ContainsKey("name"));
            Assert.Equal("Native renamed project", final.DisplayName);
            Assert.False(final.ShowTips);
            Assert.Equal("claude", final.DefaultAgent);
            Assert.True(final.MenuTipSeen); Assert.False(final.MenuTipVisible);
            var stored = new SettingsStore(new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "settings")));
            Assert.Equal(final.DisplayName, stored.DisplayNameFor(project));
            NativeDesktopInput.Click(reopened, final, "close");
            await reopened.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, reopened.ExitCode);
            passed = true;
        }
        finally
        {
            if (!desktop.HasExited) { desktop.Kill(); await desktop.WaitForExitAsync(); }
            if (reopened is not null) { if (!reopened.HasExited) { reopened.Kill(); await reopened.WaitForExitAsync(); } reopened.Dispose(); }
            await File.WriteAllTextAsync(Path.Combine(root, "desktop-workflow-private.json"), JsonSerializer.Serialize(new
            {
                passed, final, root,
                limitations = "Actual Win32 Avalonia main/settings windows and SendInput into verified owned-process windows only. Production UI/menu/settings code, private records, supplied setup result, no VM launch, credentials, real project, full desktop-to-agent focus or user UI acceptance."
            }, NativeDesktopFixture.Json));
        }
    }
}
