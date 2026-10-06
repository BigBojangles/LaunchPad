using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace LaunchPad.Tests;

internal static class NativeDesktopInput
{
    internal static void Click(Process desktop, DesktopState state, string control, bool right = false)
    {
        Assert.Equal(desktop.Id, state.Pid);
        Assert.Equal(desktop.StartTime.ToUniversalTime().Ticks, state.StartTicks);
        Assert.False(desktop.HasExited);
        var target = state.Controls[control];
        var point = new Point { X = target.X, Y = target.Y };
        Assert.Equal((nint)target.Window, GetAncestor(WindowFromPoint(point), 2));
        GetWindowThreadProcessId((nint)target.Window, out var windowPid);
        Assert.Equal((uint)desktop.Id, windowPid);
        if (!GetCursorPos(out var original)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The owned input probe cannot read the input desktop cursor.");
        try
        {
            if (!SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The owned input probe cannot move the input desktop cursor.");
            // Let the real pointer-over style reveal the menu button.
            Thread.Sleep(100);
            Assert.Equal((nint)target.Window, GetAncestor(WindowFromPoint(point), 2));
            Send([new() { Type = 0, Mouse = new() { Flags = right ? 8u : 2u } },
                new() { Type = 0, Mouse = new() { Flags = right ? 16u : 4u } }]);
        }
        finally { SetCursorPos(original.X, original.Y); }
    }
    internal static void CloseWindow(Process desktop, DesktopState state)
    {
        Assert.Equal(desktop.Id, state.Pid);
        Assert.Equal(desktop.StartTime.ToUniversalTime().Ticks, state.StartTicks);
        Assert.False(desktop.HasExited);
        Assert.NotEqual(0, state.Window);
        GetWindowThreadProcessId((nint)state.Window, out var windowPid);
        Assert.Equal((uint)desktop.Id, windowPid);
        if (!PostMessageW((nint)state.Window, 0x10, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The verified owned desktop could not receive WM_CLOSE.");
    }
    internal static void Key(Process desktop, ushort key)
    {
        Foreground(desktop);
        Send([new() { Type = 1, Keyboard = new() { VirtualKey = key } },
            new() { Type = 1, Keyboard = new() { VirtualKey = key, Flags = 2 } }]);
    }
    internal static void Text(Process desktop, string text)
    {
        Foreground(desktop);
        Send([new() { Type = 1, Keyboard = new() { VirtualKey = 0x11 } },
            new() { Type = 1, Keyboard = new() { VirtualKey = 0x41 } },
            new() { Type = 1, Keyboard = new() { VirtualKey = 0x41, Flags = 2 } },
            new() { Type = 1, Keyboard = new() { VirtualKey = 0x11, Flags = 2 } }]);
        foreach (var character in text)
        {
            Foreground(desktop);
            Send([new() { Type = 1, Keyboard = new() { Scan = character, Flags = 4 } },
                new() { Type = 1, Keyboard = new() { Scan = character, Flags = 6 } }]);
        }
    }
    private static void Foreground(Process desktop)
    {
        Assert.False(desktop.HasExited);
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        Assert.Equal((uint)desktop.Id, pid);
    }
    private static void Send(Input[] input) => Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()));
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
