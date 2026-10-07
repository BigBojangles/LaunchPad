using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LaunchPad.Services.Fence;

// A child environment belongs to the test account, rather than the launcher.
public sealed class WindowsTestEnvironment : IDisposable
{
    public nint Block { get; private set; }
    public string UserName { get; }
    public string UserProfile { get; }

    private WindowsTestEnvironment(string block)
    {
        var entries = Parse(block.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        UserName = entries.GetValueOrDefault("USERNAME", "");
        UserProfile = entries.GetValueOrDefault("USERPROFILE", "");
        Block = Marshal.StringToHGlobalUni(block);
    }

    [SupportedOSPlatform("windows")]
    public static WindowsTestEnvironment ForUser(nint token, string temporaryDirectory, string startupLogDirectory)
    {
        if (!CreateEnvironmentBlock(out var native, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entries = new List<string>();
            var cursor = native;
            var length = 0;
            while (Marshal.ReadInt16(cursor) != 0)
            {
                var entry = Marshal.PtrToStringUni(cursor)!;
                length = checked(length + entry.Length + 1);
                if (length > 1024 * 1024) throw new InvalidDataException("The test-account environment is too large.");
                entries.Add(entry);
                cursor += checked((entry.Length + 1) * 2);
            }
            return new WindowsTestEnvironment(BuildBlock(entries, temporaryDirectory, startupLogDirectory));
        }
        finally { DestroyEnvironmentBlock(native); }
    }

    public static string BuildBlock(IEnumerable<string> accountEntries, string temporaryDirectory, string startupLogDirectory)
    {
        var entries = Parse(accountEntries);
        entries["TEMP"] = Path.GetFullPath(temporaryDirectory);
        entries["TMP"] = entries["TEMP"];
        entries["COMPLUS_CLRLoadLogDir"] = Path.GetFullPath(startupLogDirectory);
        return string.Join('\0', entries.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key + "=" + item.Value)) + "\0\0";
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> entries)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            // Windows also supplies hidden drive entries such as =C:=C:\dir.
            var split = entry.IndexOf('=', entry.StartsWith('=') ? 1 : 0);
            if (split <= 0 || entry.Contains('\0')) throw new InvalidDataException("Invalid test-account environment entry.");
            values[entry[..split]] = entry[(split + 1)..];
        }
        return values;
    }

    public void Dispose()
    {
        if (Block == 0) return;
        Marshal.FreeHGlobal(Block);
        Block = 0;
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(nint environment);
}
