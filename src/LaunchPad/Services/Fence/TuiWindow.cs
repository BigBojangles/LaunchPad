using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using LaunchPad.Services;

namespace LaunchPad.Services.Fence;

public static class TuiWindow
{
    public const string Arg = "--tui";

    public static bool IsRequest(string[] args) =>
        args.Length >= 2 && string.Equals(args[0], Arg, StringComparison.Ordinal);

    public static string Arguments(int port, string title, string? pidFile = null)
    {
        var text = Arg + " " + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Quote(title);
        if (!string.IsNullOrWhiteSpace(pidFile))
            text += " " + Quote(pidFile);
        return text;
    }

    public static string TerminalArguments(string exe, int port, string title, string pidFile)
    {
        // Windows Terminal splits this line into words and joins them again.
        // cmd.exe then deletes one pair of quotes when the command starts with
        // a quote, so a quoted "C:\Users\Big Bojangles\..." becomes the folder
        // C:\Users\Big and the tab says Access is denied. The program path has
        // to reach cmd without quotes. The short path has no spaces.
        var program = SpaceFree(exe);
        var pid = SpaceFree(pidFile);
        var launched = Quote(program) + " " + Arguments(port, title, pid);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // -w new is a separate window. -w 0 attaches a tab to the terminal
        // the user already has open, and resizing that window resizes their tabs.
        return "-w new new-tab --useApplicationTitle --title " + Quote(title) + " -- " + cmd + " /c " + launched;
    }

    public static Process? Show(string title, int port, string pidFile)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("LaunchPad could not find its own program.");
        SaveDisplayTitle(Path.GetDirectoryName(pidFile)!, title);
        // Display names can contain shell punctuation. Carry them through a
        // local record, never through cmd.exe's command text.
        var args = Arguments(port, "LaunchPad", pidFile);
        var wt = WindowsTerminal();
        if (wt is not null)
        {
            // wt.exe exits as soon as the terminal accepts the tab. The caller
            // watches the pid file written by --tui, not this process.
            using (Process.Start(new ProcessStartInfo
            {
                FileName = wt,
                Arguments = TerminalArguments(exe, port, "LaunchPad", pidFile),
                UseShellExecute = true
            }))
            {
            }

            return null;
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = true
        });
    }

    public static void Run(string[] args)
    {
        if (!int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port))
            return;

        var title = args.Length > 2 ? args[2] : "LaunchPad";
        var pidPath = args.Length > 3 ? args[3] : "";
        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            try
            {
                File.WriteAllText(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // The watcher stops the session if this file never appears.
            }
        }

        BindConsole(title);
        if (!string.IsNullOrWhiteSpace(pidPath))
        {
            var sizePath = pidPath;
            var sizeThread = new Thread(() => WatchWindowSize(sizePath)) { IsBackground = true };
            sizeThread.Start();
            var copyThread = new Thread(() => WatchCopyLine(sizePath)) { IsBackground = true };
            copyThread.Start();
            using var self = Process.GetCurrentProcess();
            var selfTicks = self.StartTime.ToUniversalTime().Ticks;
            var titleThread = new Thread(() => WindowsSessionWindow.Watch(Path.GetDirectoryName(pidPath)!, Environment.ProcessId, selfTicks)) { IsBackground = true };
            titleThread.Start();
        }

        try
        {
            Console.Write("Starting the agent…");
        }
        catch
        {
            // The tab is still the place the guest will draw.
        }

        TcpClient? client = null;
        var connected = false;
        for (var attempt = 0; attempt < 50 && !connected; attempt++)
        {
            var next = new TcpClient();
            try
            {
                var wait = next.BeginConnect("127.0.0.1", port, null, null);
                if (!wait.AsyncWaitHandle.WaitOne(300))
                {
                    next.Close();
                    continue;
                }

                next.EndConnect(wait);
                client = next;
                connected = true;
            }
            catch
            {
                next.Close();
                Thread.Sleep(100);
            }
        }

        if (client is null)
        {
            try
            {
                Console.WriteLine("The fenced session did not open its port.");
            }
            catch
            {
                // The window is already going away.
            }

            EndSession(pidPath);
            return;
        }

        using var held = client;
        using var stream = held.GetStream();
        var input = new Thread(() => CopyConsoleTo(stream)) { IsBackground = true };
        input.Start();
        var readyPath = !string.IsNullOrWhiteSpace(pidPath)
            ? Path.Combine(Path.GetDirectoryName(pidPath) ?? "", "console.ready")
            : "";
        CopyToConsole(stream, readyPath);
        EndSession(pidPath);
    }

    private static void EndSession(string pidPath)
    {
        if (string.IsNullOrWhiteSpace(pidPath))
            return;

        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(pidPath)!, "console.finished"), "1"); }
        catch (IOException) { }

        try
        {
            Console.WriteLine("Saving the project and closing the machine…");
        }
        catch
        {
            // The tab can already be gone. The session still has to stop.
        }

        SessionEnd.AfterConsole(pidPath);
    }

    private static string? WindowsTerminal()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");
        if (File.Exists(local))
            return local;

        var path = Environment.GetEnvironmentVariable("Path") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "wt.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal static void BindConsole(string title)
    {
        if (!AttachConsole(-1))
            AllocConsole();

        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(Writer(-11));
            Console.SetError(Writer(-12));
            Console.SetIn(new StreamReader(StreamFor(-10, FileAccess.Read), Encoding.UTF8));
            Console.Title = title;
        }
        catch
        {
            // The console title is optional.
        }

        EnableVt();
    }

    public static void SaveDisplayTitle(string directory, string title)
    {
        if (title.Length > 512 || title.Any(char.IsControl) || !FenceFiles.TryResolveUnlinked(directory, "display-title.json", out var path))
            throw new InvalidDataException("The window display name or session location is invalid.");
        ReturnRecovery.SaveAtomic(path, new { title });
    }

    private static StreamWriter Writer(int stdHandle)
    {
        return new StreamWriter(StreamFor(stdHandle, FileAccess.Write), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private static FileStream StreamFor(int stdHandle, FileAccess access)
    {
        return new FileStream(new SafeFileHandle(GetStdHandle(stdHandle), ownsHandle: false), access);
    }

    private static void EnableVt()
    {
        var handle = GetStdHandle(-11);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return;
        if (GetConsoleMode(handle, out var mode))
            SetConsoleMode(handle, mode | 0x0004 | 0x0008);

        var input = GetStdHandle(-10);
        if (!GetConsoleMode(input, out var inMode))
            return;

        // Raw keypresses, including control keys, go to the guest.
        const uint enableVirtualTerminalInput = 0x0200;
        const uint enableLineInput = 0x0002;
        const uint enableEchoInput = 0x0004;
        const uint enableProcessedInput = 0x0001;
        SetConsoleMode(input, (inMode | enableVirtualTerminalInput) & ~enableLineInput & ~enableEchoInput & ~enableProcessedInput);
    }

    private static void WatchCopyLine(string pidFile)
    {
        var directory = Path.GetDirectoryName(pidFile);
        if (string.IsNullOrEmpty(directory))
            return;

        var path = Path.Combine(directory, "copy.progress");
        var clock = Stopwatch.StartNew();
        var started = false;
        var opened = false;
        var last = "";
        while (!CopyProgressLine.GuestStarted)
        {
            try
            {
                if (File.Exists(path))
                {
                    string text;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        text = reader.ReadToEnd().Trim();

                    if (RocketView.TryRead(text, out var sent, out var total, out var files, out _, out var firstCopy))
                    {
                        if (!started && sent > 0)
                        {
                            clock.Restart();
                            started = true;
                        }

                        var size = ReadConsoleSize();
                        var columns = size?.Columns ?? 80;
                        var rows = size?.Rows ?? 30;
                        var elapsed = started ? clock.Elapsed : TimeSpan.Zero;
                        var frame = RocketView.Frame(sent, total, files, elapsed, columns, rows, firstCopy);
                        if (!string.Equals(frame, last, StringComparison.Ordinal))
                        {
                            if (!opened)
                            {
                                Console.Write(RocketView.OpenScreen());
                                opened = true;
                            }

                            Console.Write(frame);
                            CopyProgressLine.Shown = true;
                            last = frame;
                        }
                    }
                }
            }
            catch
            {
                // The next poll retries.
            }

            Thread.Sleep(200);
        }
    }

    private static void WatchWindowSize(string pidFile)
    {
        var directory = Path.GetDirectoryName(pidFile);
        if (string.IsNullOrEmpty(directory))
            return;

        var path = Path.Combine(directory, "winsize.txt");
        var last = "";
        while (true)
        {
            var size = ReadConsoleSize();
            if (size is not null)
            {
                var text = size.Value.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " "
                    + size.Value.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(text, last, StringComparison.Ordinal))
                {
                    try
                    {
                        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }
                    catch
                    {
                        // The session folder may already be gone.
                    }

                    last = text;
                }
            }

            Thread.Sleep(200);
        }
    }

    private static (int Rows, int Columns)? ReadConsoleSize()
    {
        var handle = GetStdHandle(-11);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return null;
        if (!GetConsoleScreenBufferInfo(handle, out var info))
            return null;

        var columns = info.Window.Right - info.Window.Left + 1;
        var rows = info.Window.Bottom - info.Window.Top + 1;
        if (rows < 2 || columns < 2)
            return null;

        return (rows, columns);
    }

    private static void CopyToConsole(NetworkStream stream, string readyPath)
    {
        var handle = GetStdHandle(-11);
        var buffer = new byte[4096];
        var link = new LoginLink();
        var ready = false;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                CopyProgressLine.Finish();
                if (!ready && !string.IsNullOrEmpty(readyPath))
                {
                    try
                    {
                        File.WriteAllText(readyPath, "1", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }
                    catch
                    {
                        // The size relay can still see a later resize.
                    }

                    ready = true;
                }

                if (!WriteFile(handle, buffer, read, out _, IntPtr.Zero))
                    break;

                var url = link.Push(buffer, read);
                if (url is null)
                    continue;

                try
                {
                    ExternalLinks.Open(url);
                }
                catch
                {
                    // The terminal keeps reading when the browser does not start.
                }
            }
        }
        catch
        {
            // The guest closed the console.
        }
    }

    private static void CopyConsoleTo(NetworkStream stream)
    {
        var handle = GetStdHandle(-10);
        var buffer = new byte[256];
        try
        {
            while (ReadFile(handle, buffer, buffer.Length, out var read, IntPtr.Zero) && read > 0)
                stream.Write(buffer, 0, read);
        }
        catch
        {
            // The window closed.
        }
    }

    private static string SpaceFree(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        var shortDirectory = ShortForm(directory);
        var shortened = string.IsNullOrEmpty(name) ? shortDirectory : Path.Combine(shortDirectory, name);
        if (shortened.IndexOf(' ') < 0)
            return shortened;

        var shortFile = ShortForm(full);
        return shortFile.IndexOf(' ') < 0 ? shortFile : full;
    }

    private static string ShortForm(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return path ?? "";

        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        if (length <= 0)
            return path;
        if (length > buffer.Capacity)
        {
            buffer.EnsureCapacity(length);
            length = GetShortPathName(path, buffer, buffer.Capacity);
            if (length <= 0 || length > buffer.Capacity)
                return path;
        }

        return buffer.ToString(0, length);
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr handle, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr handle, byte[] buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string path, StringBuilder buffer, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ConsoleScreenBufferInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord Cursor;
        public short Attributes;
        public SmallRect Window;
        public Coord Maximum;
    }
}
