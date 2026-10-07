using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LaunchPad.Services;

public interface INotificationSecretProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}

// Current-user DPAPI only; never machine-wide or the guest/test-account identity.
public sealed class WindowsNotificationSecretProtector(string description = "LaunchPad notification setup") : INotificationSecretProtector
{
    public byte[] Protect(byte[] value) => Transform(value, true, description);
    public byte[] Unprotect(byte[] value) => Transform(value, false, description);

    private static byte[] Transform(byte[] value, bool protect, string description)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Notification credentials require a platform credential adapter.");
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var ok = protect
                ? CryptProtectData(ref input, description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!ok) throw new CryptographicException("Notification credentials could not be opened for this Windows user.", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Clear(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { Clear(output.Data, output.Length); LocalFree(output.Data); }
        }
    }

    private static void Clear(IntPtr memory, int length)
    {
        for (var index = 0; index < length; index++) Marshal.WriteByte(memory, index, 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
