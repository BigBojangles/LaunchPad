using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LaunchPad.Services.Fence;

// Runs as the launcher's user, on its own desktop thread. Test applications
// cannot choose a target desktop, process, command or path through these buttons.
[SupportedOSPlatform("windows")]
internal sealed class TestDesktopControls : IDisposable
{
    private const uint CloseControls = 0x8001;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WindowProcedure _procedure;
    private Thread? _thread;
    private nint _window;
    private Exception? _failure;
    private int _return, _cancel;
    public bool ReturnRequested => Volatile.Read(ref _return) != 0;
    public bool CancelRequested => Volatile.Read(ref _cancel) != 0;
    public bool IsAlive => _thread?.IsAlive == true;
    public nint WindowHandle => _window;

    private TestDesktopControls() => _procedure = HandleMessage;

    public static TestDesktopControls Start(nint desktop)
    {
        var controls = new TestDesktopControls();
        controls._thread = new Thread(() => controls.Run(desktop))
        { IsBackground = true, Name = "LaunchPad trusted Windows test controls" };
        controls._thread.SetApartmentState(ApartmentState.STA);
        controls._thread.Start();
        if (!controls._ready.Wait(TimeSpan.FromSeconds(5)) || controls._failure is not null)
        {
            controls.Dispose();
            throw new InvalidOperationException("Trusted Return/Cancel controls did not become ready.", controls._failure);
        }
        return controls;
    }

    private void Run(nint desktop)
    {
        nint instance = 0;
        var className = "LaunchPadTestControls-" + Guid.NewGuid().ToString("N");
        try
        {
            // Must occur before this thread creates any window/message hooks.
            if (!SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
            instance = GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), ClassName = className,
                Background = (nint)6, Cursor = LoadCursor(0, (nint)32512)
            };
            if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = CreateWindowEx(0x88, className, "LaunchPad - Windows test controls", 0x80c80000,
                20, 20, 480, 140, 0, 0, instance, 0);
            if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            CreateChild("STATIC", "Return leaves the test running. Cancel stops this test and its children.", 0x50000000, 12, 12, 448, 34, 0, instance);
            CreateChild("BUTTON", "Return to LaunchPad", 0x50010000, 12, 52, 210, 34, 100, instance);
            CreateChild("BUTTON", "Cancel test and return", 0x50010000, 232, 52, 224, 34, 101, instance);
            ShowWindow(_window, 4);
            _ready.Set();
            while (true)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error) { _failure = error; _ready.Set(); }
        finally
        {
            if (_window != 0) DestroyWindow(_window);
            _window = 0;
            if (instance != 0) UnregisterClass(className, instance);
        }
    }

    private void CreateChild(string type, string caption, uint style, int x, int y, int width, int height, int id, nint instance)
    {
        if (CreateWindowEx(0, type, caption, style, x, y, width, height, _window, (nint)id, instance, 0) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint HandleMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == 0x111)
        {
            var command = wParam.ToInt64() & 0xffff;
            if (command == 100) Interlocked.Exchange(ref _return, 1);
            if (command == 101) Interlocked.Exchange(ref _cancel, 1);
            return 0;
        }
        if (message == 0x10) { Interlocked.Exchange(ref _return, 1); return 0; }
        if (message == CloseControls) { DestroyWindow(window); return 0; }
        if (message == 0x2) { PostQuitMessage(0); return 0; }
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window != 0) PostMessage(_window, CloseControls, 0, 0);
        _thread?.Join(2000);
        GC.KeepAlive(_procedure);
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string type, string caption, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadCursor(nint instance, nint cursor);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
