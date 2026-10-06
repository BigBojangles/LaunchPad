using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LaunchPad.Services;

public static class GrokProcesses
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    public static bool IsGrokProcessName(string? name)
    {
        return !string.IsNullOrEmpty(name) &&
               name.StartsWith("grok", StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<int> SnapshotIds()
    {
        var ids = new HashSet<int>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return ids;
        }

        foreach (var process in processes)
        {
            try
            {
                if (IsGrokProcessName(process.ProcessName))
                    ids.Add(process.Id);
            }
            catch
            {
                // A process can exit between the snapshot and the name read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids;
    }

    public static List<int> FindNewInDirectory(IReadOnlySet<int> before, string projectPath)
    {
        var matches = new List<int>();
        string wanted;
        try
        {
            wanted = NormalizePath(projectPath);
        }
        catch
        {
            return matches;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return matches;
        }

        foreach (var process in processes)
        {
            try
            {
                if (!IsGrokProcessName(process.ProcessName) || before.Contains(process.Id))
                    continue;

                var cwd = TryGetWorkingDirectory(process.Id);
                if (cwd is null)
                    continue;

                if (string.Equals(NormalizePath(cwd), wanted, StringComparison.OrdinalIgnoreCase))
                    matches.Add(process.Id);
            }
            catch
            {
                // Skip processes that disappear while we are looking.
            }
            finally
            {
                process.Dispose();
            }
        }

        return matches;
    }

    public static bool IsRunning(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryGetWorkingDirectory(int pid)
    {
        if (pid <= 0 || IntPtr.Size != 8)
            return null;

        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var info = new ProcessBasicInformation();
            var size = Marshal.SizeOf<ProcessBasicInformation>();
            var status = NtQueryInformationProcess(handle, 0, ref info, size, out _);
            if (status != 0 || info.PebBaseAddress == IntPtr.Zero)
                return null;

            // x64 PEB.ProcessParameters is at 0x20.
            var parameters = ReadPointer(handle, info.PebBaseAddress + 0x20);
            if (parameters == IntPtr.Zero)
                return null;

            // x64 RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath is at 0x38.
            var length = ReadUInt16(handle, parameters + 0x38);
            var buffer = ReadPointer(handle, parameters + 0x40);
            if (length < 2 || length > 32768 || buffer == IntPtr.Zero)
                return null;

            var bytes = new byte[length];
            if (!ReadProcessMemory(handle, buffer, bytes, bytes.Length, out var read) || read.ToInt64() != length)
                return null;

            var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0', '\\', '/');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var bytes = new byte[8];
        if (!ReadProcessMemory(process, address, bytes, bytes.Length, out var read) || read.ToInt64() != 8)
            return IntPtr.Zero;

        return (IntPtr)BitConverter.ToInt64(bytes, 0);
    }

    private static ushort ReadUInt16(IntPtr process, IntPtr address)
    {
        var bytes = new byte[2];
        if (!ReadProcessMemory(process, address, bytes, bytes.Length, out var read) || read.ToInt64() != 2)
            return 0;

        return BitConverter.ToUInt16(bytes, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr address,
        byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
