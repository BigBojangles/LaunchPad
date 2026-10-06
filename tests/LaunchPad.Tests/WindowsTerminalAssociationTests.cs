using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

[Collection("NativeConsole")]
public sealed class WindowsTerminalAssociationTests
{
    [EnvironmentFact("LAUNCHPAD_WINDOWS_TERMINAL_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task SeparateActualTerminalWindowsKeepTitlesAndRejectStaleAssociations()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "windows-terminal-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var first = await OwnedTerminal.Start(Path.Combine(root, "first"), deadline.Token);
        using var second = await OwnedTerminal.Start(Path.Combine(root, "second"), deadline.Token);
        var a = await first.Identity(deadline.Token);
        var b = await second.Identity(deadline.Token);
        Assert.NotEqual(a.WindowHandle, b.WindowHandle);
        Assert.NotEqual(a.ConsoleHandle, b.ConsoleHandle);
        Assert.NotEqual(a.ClientPid, b.ClientPid);
        const string firstName = "LaunchPad owned \"first\" & title";
        const string secondName = "LaunchPad owned second label";
        TuiWindow.SaveDisplayTitle(first.Directory, firstName);
        TuiWindow.SaveDisplayTitle(second.Directory, secondName);
        await WindowsConsoleProbeTests.Until(() => Title(a).Contains(firstName) && Title(b).Contains(secondName), deadline.Token);
        Assert.NotNull(WindowsSessionWindow.Focus(a));
        Assert.True(WindowsSessionWindow.IsCurrent(a));
        // Show only this verified disposable window for the activation check.
        ShowWindowAsync((nint)a.WindowHandle, 4);
        await WindowsConsoleProbeTests.Until(() => IsWindowVisible((nint)a.WindowHandle), deadline.Token);
        using var inputWindow = new OwnedInputWindow();
        inputWindow.Click();
        await WindowsConsoleProbeTests.Until(() => GetForegroundWindow() == inputWindow.Handle, deadline.Token);
        var focusError = WindowsSessionWindow.Focus(a);
        Assert.Null(focusError);
        await WindowsConsoleProbeTests.Until(() => GetForegroundWindow() == (nint)a.WindowHandle, deadline.Token);
        Assert.Equal((nint)a.WindowHandle, GetForegroundWindow());
        var firstFocused = true;
        ShowWindowAsync((nint)a.WindowHandle, 6);
        await WindowsConsoleProbeTests.Until(() => IsIconic((nint)a.WindowHandle), deadline.Token);
        Assert.Null(WindowsSessionWindow.Focus(a));
        await WindowsConsoleProbeTests.Until(() => !IsIconic((nint)a.WindowHandle), deadline.Token);
        await WindowsConsoleProbeTests.Until(() => GetForegroundWindow() == (nint)a.WindowHandle, deadline.Token);
        Assert.Equal((nint)a.WindowHandle, GetForegroundWindow());
        Assert.NotNull(WindowsSessionWindow.Focus(a with { ClientStartTicks = a.ClientStartTicks - 1 }));
        Assert.False(WindowsSessionWindow.IsCurrent(a with { WindowHandle = b.WindowHandle, WindowPid = b.WindowPid, WindowStartTicks = b.WindowStartTicks }));
        var watchRoot = Path.Combine(root, "host-watch");
        var helper = WindowsSessionWindow.StartHostWatch(WindowsConsoleProbeTests.LaunchPadExecutable(), watchRoot, first.Client.Id, a.ClientStartTicks);
        await WindowsConsoleProbeTests.Until(() => WindowsSessionWindow.Read(watchRoot, first.Client.Id, a.ClientStartTicks) is not null, deadline.Token);
        Assert.Equal(a, WindowsSessionWindow.Read(watchRoot, first.Client.Id, a.ClientStartTicks));
        var lastTitle = Title(a);
        first.Peer.Dispose();
        await first.Client.WaitForExitAsync(deadline.Token);
        await WindowsConsoleProbeTests.Until(() => !WindowsSessionWindow.MatchesProcess(helper.Pid, helper.Ticks), deadline.Token);
        Assert.False(WindowsSessionWindow.IsCurrent(a));
        Assert.NotNull(WindowsSessionWindow.Focus(a));
        Assert.True(WindowsSessionWindow.IsCurrent(b));
        Assert.Contains(secondName, Title(b));
        await File.WriteAllTextAsync(Path.Combine(root, "association-private.json"), JsonSerializer.Serialize(new
        {
            verified = true, first = a, second = b, firstTitle = lastTitle, secondTitle = Title(b),
            firstFocused, focusError, minimizedWindowRestored = true, firstEndedWithoutAffectingSecond = true,
            note = "Actual Windows Terminal and compiled --tui/helper only; no VM, Grok sign-in, user project or full desktop acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
    }

    private static string Title(SessionWindowIdentity identity)
    {
        var text = new StringBuilder(2048);
        GetWindowTextW((nint)identity.WindowHandle, text, text.Capacity);
        return text.ToString();
    }

    // Give this isolated test host the same foreground/last-input eligibility
    // the desktop gets from a tile click. No input is sent to a user window.
    private sealed class OwnedInputWindow : IDisposable
    {
        public nint Handle { get; }
        private readonly Thread _thread;
        private uint _threadId;
        private volatile bool _clicked;
        public OwnedInputWindow()
        {
            var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var handle = CreateWindowExW(0x8 | 0x80, "BUTTON", "LaunchPad owned activation fixture", 0x10CF0000,
                    20, 20, 180, 100, 0, 0, 0, 0);
                if (handle == 0) { ready.SetException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); return; }
                ready.SetResult(handle);
                try
                {
                    while (GetMessageW(out var message, 0, 0, 0) > 0)
                    { TranslateMessage(ref message); DispatchMessageW(ref message); if (message.Id == 0x202) _clicked = true; }
                }
                finally { DestroyWindow(handle); }
            }) { IsBackground = true, Name = "LaunchPad owned focus input" };
            _thread.Start();
            Handle = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        public void Click()
        {
            Assert.True(GetWindowRect(Handle, out var bounds));
            var point = new Point { X = bounds.Left + 80, Y = bounds.Top + 65 };
            Assert.Equal(Handle, GetAncestor(WindowFromPoint(point), 2));
            GetCursorPos(out var original);
            try
            {
                Assert.True(SetCursorPos(point.X, point.Y));
                Assert.Equal(2u, SendInput(2, new[] { new Input { Type = 0, Mouse = new MouseInput { Flags = 2 } }, new Input { Type = 0, Mouse = new MouseInput { Flags = 4 } } }, Marshal.SizeOf<Input>()));
                var clock = Stopwatch.StartNew();
                while (!_clicked && clock.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(10);
                Assert.True(_clicked, "The owned window must consume the click before activation is tested.");
            }
            finally { SetCursorPos(original.X, original.Y); }
        }
        public void Dispose() { PostThreadMessageW(_threadId, 0x12, 0, 0); _thread.Join(5000); }
    }

    private sealed class OwnedTerminal : IDisposable
    {
        public string Directory { get; }
        public Process Client { get; }
        public TcpClient Peer { get; }
        private OwnedTerminal(string directory, Process client, TcpClient peer) { Directory = directory; Client = client; Peer = peer; }
        public static async Task<OwnedTerminal> Start(string directory, CancellationToken token)
        {
            System.IO.Directory.CreateDirectory(directory);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            Assert.True(File.Exists(wt), "Actual Windows Terminal is required for this opt-in check.");
            var pidFile = Path.Combine(directory, "tui.pid");
            var start = new ProcessStartInfo(wt)
            {
                Arguments = TuiWindow.TerminalArguments(WindowsConsoleProbeTests.LaunchPadExecutable(), ((IPEndPoint)listener.LocalEndpoint).Port,
                    "LaunchPad owned terminal proof", pidFile), UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            using var launcher = Process.Start(start);
            Process? client = null;
            TcpClient? peer = null;
            try
            {
                await WindowsConsoleProbeTests.Until(() => File.Exists(pidFile), token);
                client = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, token)));
                Assert.Equal("LaunchPad", client.ProcessName);
                peer = await listener.AcceptTcpClientAsync(token);
                await peer.GetStream().WriteAsync("LaunchPad owned association check\r\n"u8.ToArray(), token);
                return new OwnedTerminal(directory, client, peer);
            }
            catch
            {
                peer?.Dispose();
                if (client is not null) { try { if (!client.HasExited) client.Kill(); } finally { client.Dispose(); } }
                throw;
            }
        }
        public async Task<SessionWindowIdentity> Identity(CancellationToken token)
        {
            var ticks = Client.StartTime.ToUniversalTime().Ticks;
            SessionWindowIdentity? identity = null;
            await WindowsConsoleProbeTests.Until(() => (identity = WindowsSessionWindow.Read(Directory, Client.Id, ticks)) is not null, token);
            return identity!;
        }
        public void Dispose()
        {
            Peer.Dispose();
            try { if (!Client.WaitForExit(5000)) { Client.Kill(); Client.WaitForExit(5000); } }
            catch (InvalidOperationException) { }
            Client.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out Message message, nint window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
}
