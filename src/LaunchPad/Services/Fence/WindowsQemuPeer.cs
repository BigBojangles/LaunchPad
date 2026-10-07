using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LaunchPad.Services.Fence;

// Confirm the accepted server-side socket belongs to the pinned VM process,
// rather than trusting a port number or the executable's display name.
[SupportedOSPlatform("windows")]
public static class WindowsQemuPeer
{
    public static void Require(TcpClient client, int processId)
    {
        if (client.Client.LocalEndPoint is not IPEndPoint local || client.Client.RemoteEndPoint is not IPEndPoint remote
            || !IPAddress.IsLoopback(local.Address) || !IPAddress.IsLoopback(remote.Address))
            throw new InvalidDataException("Windows testing requires the owned loopback QEMU socket.");
        // TcpClient's dual-mode socket reports IPv4 loopback as mapped IPv6;
        // the AF_INET ownership table contains the underlying IPv4 addresses.
        var localAddress = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        var remoteAddress = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || remoteAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("Windows testing requires the owned IPv4 QEMU socket.");
        var length = 0;
        var result = GetExtendedTcpTable(0, ref length, false, 2, 5, 0);
        if (result != 122 || length is < 4 or > 16 * 1024 * 1024) throw new Win32Exception((int)result);
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            result = GetExtendedTcpTable(pointer, ref length, false, 2, 5, 0);
            if (result != 0) throw new Win32Exception((int)result);
            var count = Marshal.ReadInt32(pointer);
            var rowSize = Marshal.SizeOf<Row>();
            if (count < 0 || count > (length - 4) / rowSize) throw new InvalidDataException("Invalid Windows TCP owner table.");
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<Row>(pointer + 4 + index * rowSize);
                if (row.State == 5 && row.Owner == processId && Port(row.LocalPort) == remote.Port && Port(row.RemotePort) == local.Port
                    && new IPAddress(row.LocalAddress).Equals(remoteAddress) && new IPAddress(row.RemoteAddress).Equals(localAddress)) return;
            }
            throw new InvalidDataException("The Windows test socket does not belong to this VM.");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static int Port(uint port) => (int)(((port & 255) << 8) | ((port >> 8) & 255));
    [StructLayout(LayoutKind.Sequential)]
    private struct Row { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort; public int Owner; }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, uint family, uint tableClass, uint reserved);
}
