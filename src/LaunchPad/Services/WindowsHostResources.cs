using System.Runtime.InteropServices;

namespace LaunchPad.Services;

public sealed class WindowsHostResources : IHostResources
{
    public int LogicalProcessors => Math.Max(1, Environment.ProcessorCount);
    public int? InstalledMemoryMegabytes
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return null;
            var megabytes = status.TotalPhys / (1024UL * 1024UL);
            return megabytes == 0 ? null : (int)Math.Min(megabytes, int.MaxValue);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
}
