using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

/// <summary>Uses the session's console owner, never a foreground window or title match.</summary>
public static class WindowsSessionWindow
{
    public const string Argument = "--host-console-watch";
    public const string IdentityFile = "console-window.json";
    public static bool IsRequest(string[] args) => args.Length == 4 && args[0] == Argument;

    public static SessionWindowIdentity? Capture(int clientPid, long clientTicks)
    {
        if (!OperatingSystem.IsWindows() || !MatchesProcess(clientPid, clientTicks)) return null;
        var console = GetConsoleWindow();
        var window = RelatedWindow(console);
        if (console == 0 || window == 0 || !TryWindowProcess(console, out var consolePid, out var consoleTicks)
            || !TryWindowProcess(window, out var windowPid, out var windowTicks)) return null;
        var identity = new SessionWindowIdentity(clientPid, clientTicks, (long)console, consolePid, consoleTicks,
            (long)window, windowPid, windowTicks);
        return IsCurrent(identity) ? identity : null;
    }

    public static bool IsCurrent(SessionWindowIdentity identity) => OperatingSystem.IsWindows()
        && MatchesProcess(identity.ClientPid, identity.ClientStartTicks)
        && MatchesWindow((nint)identity.ConsoleHandle, identity.ConsolePid, identity.ConsoleStartTicks)
        && MatchesWindow((nint)identity.WindowHandle, identity.WindowPid, identity.WindowStartTicks)
        && RelatedWindow((nint)identity.ConsoleHandle) == (nint)identity.WindowHandle;

    public static string? Focus(SessionWindowIdentity identity)
    {
        if (!IsCurrent(identity)) return "That session window closed or changed. Refresh the project list and try again.";
        var handle = (nint)identity.WindowHandle;
        // A ConPTY message window without an owner is not a visible terminal.
        if (!IsWindowVisible(handle)) return "The session's visible terminal window is not available yet.";
        if (IsIconic(handle)) ShowWindowAsync(handle, 9);
        if (!IsCurrent(identity)) return "The session window changed before it could be focused.";
        return SetForegroundWindow(handle) || GetForegroundWindow() == handle ? null
            : "Windows could not activate this session. Select its terminal on the taskbar.";
    }

    public static SessionWindowIdentity? Read(string directory, int clientPid, long clientTicks)
    {
        try
        {
            if (!FenceFiles.TryResolveUnlinked(directory, IdentityFile, out var path)
                || !File.Exists(path) || new FileInfo(path).Length > 8192) return null;
            var identity = JsonSerializer.Deserialize<SessionWindowIdentity>(File.ReadAllText(path), JsonFile.Options);
            return identity is not null && identity.ClientPid == clientPid && identity.ClientStartTicks == clientTicks
                && IsCurrent(identity) ? identity : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static void Publish(string directory, SessionWindowIdentity identity)
    {
        if (!FenceFiles.TryResolveUnlinked(directory, IdentityFile, out var path))
            throw new IOException("The console window location is invalid.");
        ReturnRecovery.SaveAtomic(path, identity);
    }

    public static (int Pid, long Ticks) StartHostWatch(string executable, string directory, int pid, long ticks)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { Argument, directory, pid.ToString(CultureInfo.InvariantCulture), ticks.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(value);
        using var helper = Process.Start(start) ?? throw new IOException("The session window watcher did not start.");
        return (helper.Id, helper.StartTime.ToUniversalTime().Ticks);
    }

    public static void Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsRequest(args) || !int.TryParse(args[2], out var pid)
            || !long.TryParse(args[3], out var ticks) || !MatchesProcess(pid, ticks)) return;
        // This isolated helper never detaches the desktop process or its parent console.
        var attached = false;
        try
        {
            for (var attempt = 0; attempt < 40 && MatchesProcess(pid, ticks); attempt++)
            {
                if (AttachConsole(pid)) { attached = true; break; }
                Thread.Sleep(100);
            }
            if (!attached || !MatchesProcess(pid, ticks)) return;
            SetConsoleCtrlHandler(0, true);
            Watch(args[1], pid, ticks);
        }
        finally { if (attached) FreeConsole(); }
    }

    public static void Watch(string directory, int clientPid, long clientTicks)
    {
        if (!OperatingSystem.IsWindows()) return;
        SessionWindowIdentity? published = null;
        string? paintedColor = null;
        while (MatchesProcess(clientPid, clientTicks))
        {
            try
            {
                if (Capture(clientPid, clientTicks) is { } identity && identity != published)
                { Publish(directory, identity); published = identity; paintedColor = null; }
                if (published is not null && FenceFiles.TryResolveUnlinked(directory, "display-color.json", out var colorPath)
                    && File.Exists(colorPath) && new FileInfo(colorPath).Length < 1024)
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(colorPath));
                    var color = document.RootElement.GetProperty("color").GetString();
                    var exclusive = document.RootElement.TryGetProperty("exclusiveWindow", out var flag) && flag.ValueKind == JsonValueKind.True;
                    if (color != paintedColor && ProjectIdentity.IsColorHex(color) && PaintProject(published, color!, exclusive)) paintedColor = color;
                }
                if (FenceFiles.TryResolveUnlinked(directory, "display-title.json", out var path)
                    && File.Exists(path) && new FileInfo(path).Length < 8192)
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var title = document.RootElement.GetProperty("title").GetString();
                    if (title is { Length: <= 512 } && !title.Any(char.IsControl) && Console.Title != title) Console.Title = title;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or Win32Exception or ArgumentException) { }
            Thread.Sleep(300);
        }
    }

    public static bool MatchesProcess(int pid, long ticks)
    {
        if (pid <= 0 || ticks <= 0) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private static bool PaintProject(SessionWindowIdentity identity, string color, bool exclusive)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !IsCurrent(identity)) return false;
        using var host = Process.GetProcessById(identity.WindowPid);
        // Classic consoles have their own window. Windows Terminal can later move
        // a dedicated tab into a shared window; its per-tab color is applied at launch.
        if (!host.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase)) return false;
        var rgb = Convert.ToUInt32(color[1..], 16);
        var nativeColor = ((rgb & 0xFF0000) >> 16) | (rgb & 0x00FF00) | ((rgb & 0x0000FF) << 16);
        if (!IsCurrent(identity)) return false;
        var border = DwmSetWindowAttribute((nint)identity.WindowHandle, 34, ref nativeColor, sizeof(uint));
        var caption = DwmSetWindowAttribute((nint)identity.WindowHandle, 35, ref nativeColor, sizeof(uint));
        uint text = 0x00FFFFFF;
        DwmSetWindowAttribute((nint)identity.WindowHandle, 36, ref text, sizeof(uint));
        return border == 0 || caption == 0;
    }

    private static nint RelatedWindow(nint console) => console == 0 ? 0 : GetWindow(console, 4) is var owner && owner != 0 ? owner : console;
    private static bool MatchesWindow(nint handle, int pid, long ticks) => handle != 0 && IsWindow(handle)
        && GetWindowThreadProcessId(handle, out var actual) != 0 && actual == pid && MatchesProcess(pid, ticks);
    private static bool TryWindowProcess(nint handle, out int pid, out long ticks)
    {
        pid = 0; ticks = 0;
        if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out var actual) == 0 || actual == 0 || actual > int.MaxValue) return false;
        try { using var process = Process.GetProcessById((int)actual); pid = process.Id; ticks = process.StartTime.ToUniversalTime().Ticks; return !process.HasExited; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    [DllImport("kernel32.dll")] private static extern nint GetConsoleWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref uint value, int size);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(nint handler, bool add);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
