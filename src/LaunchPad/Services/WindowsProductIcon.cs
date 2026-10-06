using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LaunchPad.Services;

/// <summary>Windows executable icon adapter. Shared session records never hold HICONs.</summary>
internal static class WindowsProductIcon
{
    public static IImage? FromExe(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var icons = new IntPtr[1];
        var ids = new int[1];
        IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (PrivateExtractIcons(path, 0, 64, 64, icons, ids, 1, 0) is 0 or uint.MaxValue || icons[0] == IntPtr.Zero) return null;
            dc = CreateCompatibleDC(IntPtr.Zero);
            var info = new BitmapInfo { Size = 40, Width = 64, Height = -64, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            Marshal.Copy(new byte[64 * 64 * 4], 0, bits, 64 * 64 * 4);
            previous = SelectObject(dc, dib);
            if (!DrawIconEx(dc, 0, 0, icons[0], 64, 64, 0, IntPtr.Zero, 3)) return null;
            var image = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var target = image.Lock();
            var pixels = new byte[64 * 64 * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var row = 0; row < 64; row++) Marshal.Copy(pixels, row * 256, target.Address + row * target.RowBytes, 256);
            return image;
        }
        catch { return null; }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
