using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using LaunchPad.Services.Fence;
using LaunchPad.Services;
using LaunchPad.Models;
using Xunit;

namespace LaunchPad.Tests;

[CollectionDefinition("NativeConsole", DisableParallelization = true)]
public class NativeConsoleCollection { }

[Collection("NativeConsole")]
public class WindowsConsoleProbeTests
{
    [EnvironmentFact("LAUNCHPAD_TERMINAL_PROBE", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualTerminalEntryPointRendersAndSendsInputWithoutStartingDesktop()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "terminal-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var terminal = new HiddenConsole(root, port);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("\r\nLP-terminal-proof ✓\r\n"), deadline.Token);
        await Until(() => terminal.ReadScreen().Contains("LP-terminal-proof ✓", StringComparison.Ordinal), deadline.Token);
        Assert.True(File.Exists(Path.Combine(root, "console.ready")));
        Assert.Equal(terminal.Process.Id.ToString(), (await File.ReadAllTextAsync(Path.Combine(root, "tui.pid"), deadline.Token)).Trim());
        terminal.Type("lp-input");
        var bytes = new byte[128];
        var received = new StringBuilder();
        while (!received.ToString().Contains("lp-input", StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(bytes, deadline.Token);
            Assert.True(count > 0, "The terminal closed before input reached the guest channel.");
            received.Append(Encoding.UTF8.GetString(bytes, 0, count));
        }
        terminal.Resize(92, 28);
        await Until(() => ReadShared(Path.Combine(root, "winsize.txt")) == "28 92", deadline.Token);
        const string displayTitle = "Owned \"name\" & display label";
        TuiWindow.SaveDisplayTitle(root, displayTitle);
        await Until(() => terminal.ReadTitle() == displayTitle, deadline.Token);
        Assert.Equal(displayTitle, terminal.ReadTitle());
        await File.WriteAllTextAsync(Path.Combine(root, "screen.txt"), terminal.ReadScreen(), deadline.Token);
        var ticks = terminal.Process.StartTime.ToUniversalTime().Ticks;
        await Until(() => WindowsSessionWindow.Read(root, terminal.Process.Id, ticks) is not null, deadline.Token);
        var identity = WindowsSessionWindow.Read(root, terminal.Process.Id, ticks)!;
        Assert.Equal(terminal.Process.Id, identity.ClientPid);
        Assert.True(WindowsSessionWindow.IsCurrent(identity));
        Assert.False(WindowsSessionWindow.IsCurrent(identity with { ClientStartTicks = ticks - 1 }));
        Assert.False(WindowsSessionWindow.IsCurrent(identity with { WindowStartTicks = identity.WindowStartTicks - 1 }));
        Assert.False(WindowsSessionWindow.IsCurrent(identity with { WindowHandle = -1 }));
        Assert.NotNull(WindowsSessionWindow.Focus(identity with { ClientStartTicks = ticks - 1 }));
        var watchRoot = Path.Combine(root, "host-console-watch");
        var helper = WindowsSessionWindow.StartHostWatch(LaunchPadExecutable(), watchRoot, terminal.Process.Id, ticks);
        try
        {
            await Until(() => WindowsSessionWindow.Read(watchRoot, terminal.Process.Id, ticks) is not null, deadline.Token);
            Assert.Equal(identity, WindowsSessionWindow.Read(watchRoot, terminal.Process.Id, ticks));
            await File.WriteAllTextAsync(Path.Combine(root, "window-identity.json"), System.Text.Json.JsonSerializer.Serialize(identity, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        finally
        {
            peer.Dispose();
            await terminal.Process.WaitForExitAsync(deadline.Token);
            await Until(() => !WindowsSessionWindow.MatchesProcess(helper.Pid, helper.Ticks), deadline.Token);
        }
        Assert.False(WindowsSessionWindow.IsCurrent(identity));
        // Closing our owned peer ends the actual --tui helper, with no VM or desktop UI.
        peer.Dispose();
        await terminal.Process.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, terminal.Process.ExitCode);
    }

    internal static string LaunchPadExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(GuestBaselineTests.RepositoryRoot(), "src", "LaunchPad", "bin", configuration, "net8.0", "LaunchPad.exe");
    }

    internal static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(50, token);
    }

    private static string ReadShared(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream); return reader.ReadToEnd().Trim(); }
        catch (IOException) { return ""; }
    }
}

// An actual Windows console, hidden during automated verification. No fake VT
// responder: the production helper and Windows handle the terminal exchange.
internal sealed class HiddenConsole : IDisposable
{
    public Process Process { get; }
    private nint _input;
    private nint _output;
    private bool _attached;
    private bool _restoreParent;
    private readonly bool _ownsProcess;

    public HiddenConsole(string root, int port, bool attach = true)
    {
        _ownsProcess = true;
        // A GUI-subsystem child calls AttachConsole(-1) itself. Detach this
        // isolated test host before creation so it cannot accidentally attach
        // to our parent console instead of allocating its own.
        if (FreeConsole()) _restoreParent = true;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var exe = Path.Combine(GuestBaselineTests.RepositoryRoot(), "src", "LaunchPad", "bin", configuration, "net8.0", "LaunchPad.exe");
        var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1 | 8, ShowWindow = 0, CountCharsX = 100, CountCharsY = 60 };
        var command = new StringBuilder('"' + exe + "\" " + TuiWindow.Arguments(port, "LaunchPad disposable terminal check", Path.Combine(root, "tui.pid")));
        if (!CreateProcessW(exe, command, 0, 0, false, 0x10, 0, Path.GetDirectoryName(exe)!, ref info, out var created))
        {
            var error = Marshal.GetLastWin32Error();
            if (_restoreParent) { AttachConsole(-1); _restoreParent = false; }
            throw new Win32Exception(error);
        }
        CloseHandle(created.Process);
        CloseHandle(created.Thread);
        Process = Process.GetProcessById(created.ProcessId);
        AttachAndOpen();
        if (!attach) Detach();
    }

    private HiddenConsole(Process existing)
    {
        Process = System.Diagnostics.Process.GetProcessById(existing.Id);
        AttachAndOpen();
    }

    internal static HiddenConsole AttachTo(Process existing) => new(existing);

    private void AttachAndOpen()
    {
        try
        {
            var attachClock = Stopwatch.StartNew();
            while (!AttachConsole(Process.Id))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 5 && !_restoreParent)
                {
                    // Detach this isolated test host only; the parent's console stays open.
                    if (!FreeConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
                    _restoreParent = true;
                    continue;
                }
                if (error != 6 || Process.HasExited || attachClock.Elapsed > TimeSpan.FromSeconds(5)) throw new Win32Exception(error);
                // A GUI-subsystem program creates/attaches its console after startup.
                Thread.Sleep(25);
            }
            _attached = true;
            _input = CreateFileW("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
            _output = CreateFileW("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
            if (_input == -1 || _output == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (_ownsProcess) Resize(100, 30);
        }
        catch { Dispose(); throw; }
    }

    public string ReadScreen(bool viewportOnly = false)
    {
        // Full-screen agents switch buffers. Reopen the currently active buffer
        // rather than inspecting a stale handle to the starting screen.
        var active = CreateFileW("CONOUT$", 0x80000000, 3, 0, 3, 0, 0);
        if (active == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetConsoleScreenBufferInfo(active, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = info.Size.X * (viewportOnly ? info.Window.Bottom - info.Window.Top + 1 : info.Size.Y);
            // This API writes UTF-16 cells, without a terminating null. Use the
            // returned cell count rather than StringBuilder string marshalling.
            var text = new char[count];
            if (!ReadConsoleOutputCharacterW(active, text, count, new Coord(0, viewportOnly ? info.Window.Top : (short)0), out var read)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (read < 0 || read > count) throw new InvalidOperationException("Invalid console cell count.");
            return new string(text, 0, read);
        }
        finally { CloseHandle(active); }
    }

    public void Type(string text)
    {
        foreach (var character in text)
        {
            var key = character == '\r' ? (short)13 : (short)0;
            var records = new[] { new InputRecord { EventType = 1, KeyDown = true, RepeatCount = 1, VirtualKeyCode = key, UnicodeChar = character }, new InputRecord { EventType = 1, KeyDown = false, RepeatCount = 1, VirtualKeyCode = key, UnicodeChar = character } };
            if (!WriteConsoleInputW(_input, records, records.Length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    public string ReadTitle() { var title = new StringBuilder(1024); GetConsoleTitleW(title, title.Capacity); return title.ToString(); }

    public void Resize(short columns, short rows)
    {
        if (!GetConsoleScreenBufferInfo(_output, out var current)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Expanding the viewport requires expanding its buffer first. Shrink
        // only after the viewport fits; both directions occur in concurrency.
        if (!SetConsoleScreenBufferSize(_output, new Coord((short)Math.Max(columns, current.Size.X),
            (short)Math.Max(Math.Max((int)rows, 60), current.Size.Y)))) throw new Win32Exception(Marshal.GetLastWin32Error());
        var window = new SmallRect { Right = (short)(columns - 1), Bottom = (short)(rows - 1) };
        if (!SetConsoleWindowInfo(_output, true, ref window)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetConsoleScreenBufferSize(_output, new Coord(columns, (short)Math.Max((int)rows, 60)))) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        try { if (_ownsProcess && !Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        Detach();
        Process.Dispose();
    }

    private void Detach()
    {
        if (_input != 0 && _input != -1) CloseHandle(_input);
        if (_output != 0 && _output != -1) CloseHandle(_output);
        _input = 0; _output = 0;
        if (_attached) { FreeConsole(); _attached = false; }
        if (_restoreParent) { AttachConsole(-1); _restoreParent = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential)] private struct SmallRect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public short Attributes; public SmallRect Window; public Coord Maximum; }
    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)] private struct InputRecord
    {
        [FieldOffset(0)] public short EventType;
        [FieldOffset(4), MarshalAs(UnmanagedType.Bool)] public bool KeyDown;
        [FieldOffset(8)] public short RepeatCount;
        [FieldOffset(10)] public short VirtualKeyCode;
        [FieldOffset(12)] public short ScanCode;
        [FieldOffset(14)] public char UnicodeChar;
        [FieldOffset(16)] public int ControlKeyState;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CountCharsX, CountCharsY, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint Reserved3, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, int flags, nint environment, string cwd, ref StartupInfo info, out ProcessInformation created);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetConsoleTitleW(StringBuilder title, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFileW(string path, uint access, int sharing, nint attributes, int disposition, int flags, nint template);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ReadConsoleOutputCharacterW(nint output, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] buffer, int count, Coord origin, out int read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WriteConsoleInputW(nint input, InputRecord[] records, int count, out int written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(nint output, out BufferInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleWindowInfo(nint output, bool absolute, ref SmallRect window);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleScreenBufferSize(nint output, Coord size);
}
