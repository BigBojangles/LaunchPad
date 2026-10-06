using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed record NativeFileScanResult(FileScanVerdict Verdict, int? HResult, int? NativeResult,
    bool ProviderReported, long ContentBytes, long ReadCalls, long BytesRead, bool ReadFailed);

public static class WindowsAmsiScanner
{
    private static readonly object Gate = new();
    private static IAntimalware? _scanner;

    public static NativeFileScanResult Scan(Stream input, string name)
    {
        if (!OperatingSystem.IsWindows() || !input.CanRead || !input.CanSeek)
            return new(FileScanVerdict.Unavailable, null, null, false, 0, 0, 0, false);
        lock (Gate)
        {
            AmsiContentStream? content = null;
            IntPtr provider = IntPtr.Zero;
            try
            {
                content = new AmsiContentStream(input, name);
                // Registered Windows AMSI dispatcher; providers/configuration remain OS-owned.
                _scanner ??= (IAntimalware)Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new Guid("fdb00e52-a214-4aa1-8fba-4357bb0072ec"), throwOnError: true)!)!;
                var status = _scanner.Scan(content, out var result, out provider);
                var verdict = status != 0 || content.ReadFailed || provider == IntPtr.Zero
                    ? FileScanVerdict.Unavailable
                    : result >= 32768 || result is >= 16384 and <= 20479
                        ? FileScanVerdict.Rejected : FileScanVerdict.Allowed;
                if (verdict == FileScanVerdict.Unavailable) Reset();
                return new(verdict, status, result, provider != IntPtr.Zero, content.ContentBytes,
                    content.ReadCalls, content.BytesRead, content.ReadFailed);
            }
            catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                Reset();
                return new(FileScanVerdict.Unavailable, error.HResult, null, false,
                    content?.ContentBytes ?? 0, content?.ReadCalls ?? 0, content?.BytesRead ?? 0, content?.ReadFailed ?? false);
            }
            finally
            {
                if (provider != IntPtr.Zero) Marshal.Release(provider);
                GC.KeepAlive(content); // Keep the managed COM callback and readonly input alive through native completion.
            }
        }

        [SupportedOSPlatform("windows")]
        static void Reset()
        {
            if (_scanner is not null && Marshal.IsComObject(_scanner)) Marshal.ReleaseComObject(_scanner);
            _scanner = null;
        }
    }

    [ComImport, Guid("82d29c2e-f062-44e6-b5c9-3d9a2f24a2df"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAntimalware
    {
        [PreserveSig] int Scan([MarshalAs(UnmanagedType.Interface)] IAmsiContentStream stream, out int result, out IntPtr provider);
        [PreserveSig] void CloseSession(ulong session);
    }
}

[ComVisible(true), Guid("3e47f2e5-81d4-4d3b-897f-545096770373"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAmsiContentStream
{
    [PreserveSig] int GetAttribute(uint attribute, uint bufferSize, IntPtr buffer, out uint actualSize);
    [PreserveSig] int Read(ulong position, uint requested, IntPtr buffer, out uint actualSize);
}

// One immutable seekable file is offered to one provider scan. Positional reads are bounded in memory;
// filling a provider request does not mistake an ordinary short Stream.Read for end-of-file.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class AmsiContentStream : IAmsiContentStream
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int InsufficientBuffer = unchecked((int)0x8007007a);
    private const int EndOfFile = unchecked((int)0x80070026);
    private const int ReadFault = unchecked((int)0x8007001e);
    private readonly Stream _input;
    private readonly byte[] _name;
    private readonly object _gate = new();
    public long ContentBytes { get; }
    public long ReadCalls { get; private set; }
    public long BytesRead { get; private set; }
    public bool ReadFailed { get; private set; }

    public AmsiContentStream(Stream input, string name)
    {
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("A readable seekable return is required.");
        _input = input;
        ContentBytes = input.Length;
        _name = Encoding.Unicode.GetBytes(name + '\0');
    }

    public int GetAttribute(uint attribute, uint bufferSize, IntPtr buffer, out uint actualSize)
    {
        var value = attribute switch
        {
            0 => Encoding.Unicode.GetBytes("LaunchPad\0"),
            1 => _name,
            2 => BitConverter.GetBytes((ulong)ContentBytes),
            4 => new byte[IntPtr.Size], // Each returned file is a separate scan, without cross-file sessions.
            _ => null
        };
        actualSize = (uint)(value?.Length ?? 0);
        if (value is null) return unchecked((int)0x80004001); // E_NOTIMPL; provider uses Read for file contents.
        if (bufferSize < actualSize) return InsufficientBuffer;
        if (buffer == IntPtr.Zero) return InvalidArgument;
        Marshal.Copy(value, 0, buffer, value.Length);
        return 0;
    }

    public int Read(ulong position, uint requested, IntPtr buffer, out uint actualSize)
    {
        actualSize = 0;
        if (requested == 0) return 0;
        if (buffer == IntPtr.Zero) return InvalidArgument;
        if (position >= (ulong)ContentBytes) return EndOfFile;
        lock (_gate)
        {
            ReadCalls++;
            byte[]? piece = null;
            try
            {
                if (_input.Length != ContentBytes) throw new IOException("Returned file length changed during scanning.");
                _input.Position = (long)position;
                var needed = (uint)Math.Min((ulong)requested, (ulong)ContentBytes - position);
                piece = ArrayPool<byte>.Shared.Rent((int)Math.Min(needed, 65536u));
                while (actualSize < needed)
                {
                    var count = _input.Read(piece, 0, (int)Math.Min((uint)piece.Length, needed - actualSize));
                    if (count == 0) throw new EndOfStreamException("Returned content ended before its declared length.");
                    Marshal.Copy(piece, 0, new IntPtr(checked(buffer.ToInt64() + actualSize)), count);
                    actualSize += (uint)count;
                    BytesRead = checked(BytesRead + count);
                }
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or InvalidOperationException or OverflowException)
            {
                ReadFailed = true;
                return ReadFault;
            }
            finally { if (piece is not null) ArrayPool<byte>.Shared.Return(piece, clearArray: true); }
        }
    }
}
