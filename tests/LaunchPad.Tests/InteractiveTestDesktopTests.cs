using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
[SupportedOSPlatform("windows")]
public sealed class InteractiveTestDesktopTests
{
    [EnvironmentFact("LAUNCHPAD_INTERACTIVE_TEST_DESKTOP", "1")]
    [Trait("Category", "Integration")]
    public async Task OwnedRestrictedGuiAcceptsInputReturnsAndCancelsOnlyItsJob()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "interactive-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        RestrictedRuntimeAccess.ModifyDirectory(root);
        var script = Path.Combine(root, "gui.ps1");
        File.Copy(Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", "owned-windows-test-gui.ps1"), script);
        using var unrelated = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 120\"" })!;
        var passed = false;
        InteractiveTestDesktop? desktop = null;
        int? guiPid = null, childPid = null;
        try
        {
            desktop = InteractiveTestDesktop.Create(15);
            var process = desktop.StartProcess(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Root", root], root);
            guiPid = process.Id;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await WindowsConsoleProbeTests.Until(() => File.Exists(Path.Combine(root, "gui-ready.json")) || process.HasExited, deadline.Token);
            Assert.False(process.HasExited, "GUI fixture ended before readiness; see retained gui-error.txt and restricted handoff reports.");
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "gui-ready.json")));
            Assert.Equal(process.Id, ready.RootElement.GetProperty("pid").GetInt32());
            childPid = ready.RootElement.GetProperty("childPid").GetInt32();
            using var child = Process.GetProcessById(childPid.Value);
            _ = child.Handle; // Retain identity/exit observation before cancellation.
            Assert.EndsWith("\\" + TestUserRunner.UserName, ready.RootElement.GetProperty("identity").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(ready.RootElement.GetProperty("admin").GetBoolean());
            Assert.Equal("WinSta0", ready.RootElement.GetProperty("station").GetString());
            Assert.Equal(desktop.Name, ready.RootElement.GetProperty("desktop").GetString());
            var guardRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchPad", "test-desktops", desktop.Id);
            using var controls = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(guardRoot, "controls.json")));
            var controlWindow = (nint)controls.RootElement.GetProperty("window").GetInt64();
            var guardPid = controls.RootElement.GetProperty("pid").GetInt32();
            var inputProof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lease = desktop;
            var thread = new Thread(() =>
            {
                try
                {
                    if (!SetThreadDesktop(lease.Desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    lease.Activate();
                    var timer = Stopwatch.StartNew();
                    while (GetWindowThreadProcessId(GetForegroundWindow(), out var actualPid) != 0 && actualPid != process.Id && timer.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(20);
                    NativeDesktopInput.Text(process, "owned GUI input");
                    var button = GetDlgItem(controlWindow, 100);
                    GetWindowThreadProcessId(controlWindow, out var observedGuardPid);
                    Assert.Equal((uint)guardPid, observedGuardPid);
                    Assert.NotEqual(0, button);
                    // Real pointer input, restricted to the verified owned
                    // return button on this owned desktop.
                    Assert.True(GetWindowRect(button, out var bounds));
                    var point = new Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
                    Assert.True(SetCursorPos(point.X, point.Y));
                    Assert.Equal(controlWindow, GetAncestor(WindowFromPoint(point), 2));
                    var input = new Input[] { new() { Type = 0, Mouse = new() { Flags = 2 } }, new() { Type = 0, Mouse = new() { Flags = 4 } } };
                    Assert.Equal(2u, SendInput(2, input, Marshal.SizeOf<Input>()));
                    inputProof.SetResult();
                }
                catch (Exception error) { inputProof.SetException(error); }
            }) { IsBackground = true };
            thread.Start();
            await inputProof.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WindowsConsoleProbeTests.Until(() => desktop.Returned, deadline.Token);
            Assert.False(process.HasExited); // Return is distinct from Cancel.
            await WindowsConsoleProbeTests.Until(() =>
            {
                try
                {
                    using var typed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "input.json")));
                    return typed.RootElement.GetProperty("text").GetString() == "owned GUI input";
                }
                catch (IOException) { return false; }
                catch (JsonException) { return false; }
            }, deadline.Token);
            using var inputReport = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "input.json")));
            Assert.Equal("owned GUI input", inputReport.RootElement.GetProperty("text").GetString());
            Assert.True(desktop.Cancel(TimeSpan.FromSeconds(5)));
            Assert.True(process.WaitForExit(5000));
            Assert.True(child.WaitForExit(5000));
            Assert.False(unrelated.HasExited);
            Assert.Equal("Default", InteractiveTestDesktop.InputName());
            passed = true;
        }
        finally
        {
            int? guiExitCode = desktop?.TestProcess is { HasExited: true } ended ? ended.ExitCode : null;
            desktop?.Dispose();
            if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(5000); }
            await File.WriteAllTextAsync(Path.Combine(root, "phase-private.json"), JsonSerializer.Serialize(new
            { passed, root, desktopId = desktop?.Id, guiPid, childPid, guiExitCode, assemblySha256 = GuestBaselineTests.HashFile(typeof(InteractiveTestDesktop).Assembly.Location),
                limits = passed ? "Owned GUI input, trusted Return button and API cancellation passed. No lock/RDP/owner-crash/deadline/Cancel-button proof; full guest bridge remains pending."
                    : "Fixture failed; no GUI/input/Return acceptance is claimed. Inspect retained reports for the exact reached stage. No real projects or VM operations." }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
}
