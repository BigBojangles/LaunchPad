using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LaunchPad.Services.Fence;

public static class SessionSweep
{
    public static bool ShouldStop(string? commandLine, string sessionsRoot, bool tuiAlive)
    {
        if (tuiAlive || string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(sessionsRoot))
            return false;

        var root = sessionsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return commandLine.Contains(root + "\\", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static void StopAbandoned(string sessionsRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot))
            return;

        foreach (var process in Process.GetProcessesByName("qemu-system-x86_64"))
        {
            try
            {
                var line = CommandLine(process.Id);
                if (!ShouldStop(line, sessionsRoot, TuiAlive(line, sessionsRoot)))
                    continue;

                StopPid(process.Id);
            }
            catch
            {
                // This process is not one of our sessions, or it already exited.
            }
            finally
            {
                process.Dispose();
            }
        }

        StopLockedSessions(sessionsRoot);
    }

    private static void StopLockedSessions(string sessionsRoot)
    {
        if (!Directory.Exists(sessionsRoot))
            return;

        foreach (var dir in Directory.GetDirectories(sessionsRoot))
        {
            if (TuiAliveIn(dir))
                continue;

            var disk = Path.Combine(ProjectSessionStore.Current(dir), "session.qcow2");
            if (!File.Exists(disk))
                continue;

            foreach (var pid in LockingPids(disk))
            {
                if (!IsQemu(pid))
                    continue;

                StopPid(pid);
            }
        }
    }

    public static int[] LockingPids(string path)
    {
        var key = new StringBuilder(64);
        var started = RmStartSession(out var session, 0, key);
        if (started != 0)
            return Array.Empty<int>();

        try
        {
            var names = new[] { path };
            if (RmRegisterResources(session, 1, names, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                return Array.Empty<int>();

            uint needed = 0;
            uint count = 0;
            var listed = RmGetListEmpty(session, out needed, ref count, IntPtr.Zero, out _);
            if (listed != 0 && listed != 234)
                return Array.Empty<int>();
            if (needed == 0)
                return Array.Empty<int>();

            var info = new RmProcessInfo[needed];
            count = needed;
            listed = RmGetListFull(session, out needed, ref count, info, out _);
            if (listed != 0)
                return Array.Empty<int>();

            var ids = new List<int>();
            var take = Math.Min(count, (uint)info.Length);
            for (var i = 0; i < take; i++)
            {
                if (info[i].Process.ProcessId > 0)
                    ids.Add(info[i].Process.ProcessId);
            }

            return ids.ToArray();
        }
        catch
        {
            return Array.Empty<int>();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static bool TuiAliveIn(string dir)
    {
        if (ProjectSessionStore.IsPreparing(dir)) return true;
        try { dir = ProjectSessionStore.Current(dir); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { return true; }
        if (SessionGuardian.TryReadLiveOwner(dir) is not null) return true;
        var pidFile = Path.Combine(dir, "tui.pid");
        if (!File.Exists(pidFile))
            return false;

        try
        {
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) || pid <= 0)
                return false;

            using var terminal = Process.GetProcessById(pid);
            return !terminal.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static bool IsQemu(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void StopPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
                return;

            process.Kill(entireProcessTree: true);
            return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The machine runs as BuildLaunchTest. Ask that account to stop it.
        }

        StopAsLaunchAccount(pid);
    }

    public static void StopAsLaunchAccount(int pid)
    {
        var taskkill = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
        if (!File.Exists(taskkill))
            return;

        if (!TestUserRunner.TryStart(taskkill, Environment.SystemDirectory, new[] { "/F", "/PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, out var killer) || killer is null)
            return;

        try
        {
            killer.WaitForExit(8000);
        }
        finally
        {
            TestUserRunner.ReleaseMachine(killer.Id);
            killer.Dispose();
        }
    }

    private static bool TuiAlive(string? commandLine, string sessionsRoot)
    {
        var dir = SessionDirectory(commandLine, sessionsRoot);
        if (dir is null)
            return false;
        return TuiAliveIn(dir);
    }

    private static string? SessionDirectory(string? commandLine, string sessionsRoot)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var root = sessionsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var at = commandLine.IndexOf(root, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        var rest = commandLine[(at + root.Length)..].TrimStart('\\', '/');
        var end = rest.IndexOfAny(['\\', '/', '"', ' ', '\t']);
        var name = end < 0 ? rest : rest[..end];
        if (name.Length == 0)
            return null;

        return Path.Combine(root, name);
    }

    private static string? CommandLine(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var length);
            if (length < 16)
                return null;

            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, length, out _) != 0)
                    return null;

                var bytes = Marshal.ReadInt16(buffer);
                if (bytes <= 0)
                    return null;

                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                if (text == IntPtr.Zero)
                    return null;

                return Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process,
        int infoClass,
        IntPtr information,
        int length,
        out int returnLength);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint session,
        uint fileCount,
        string[] fileNames,
        uint appCount,
        IntPtr apps,
        uint serviceCount,
        IntPtr services);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, EntryPoint = "RmGetList")]
    private static extern int RmGetListEmpty(uint session, out uint needed, ref uint count, IntPtr info, out uint rebootReasons);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, EntryPoint = "RmGetList")]
    private static extern int RmGetListFull(uint session, out uint needed, ref uint count, [Out] RmProcessInfo[] info, out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public int StartLow;
        public int StartHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceName;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        public int Restartable;
    }
}
