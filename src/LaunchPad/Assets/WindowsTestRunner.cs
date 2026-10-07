using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

// Compiled standalone: this runs inside the restricted test job, never as the
// host user. Descendants inherit that job; the host owns cancellation/deadline.
internal static class WindowsTestRunner
{
    private const int LogLimit = 1024 * 1024;
    private static int _truncated;
    private static int Main(string[] args)
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            if (args.Length != 1) return 125;
            string executable, directory;
            string[] arguments;
            using (var input = new BinaryReader(File.OpenRead(args[0]), new UTF8Encoding(false)))
            {
                if (input.ReadString() != "LaunchPad.WindowsTest.v1") return 125;
                executable = input.ReadString();
                directory = input.ReadString();
                int count = input.ReadInt32();
                if (count < 0 || count > 128) return 125;
                arguments = new string[count];
                for (int index = 0; index < count; index++) arguments[index] = input.ReadString();
                if (input.BaseStream.Position != input.BaseStream.Length) return 125;
            }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Arguments = string.Join(" ", Array.ConvertAll(arguments, Quote));
            using (var child = Process.Start(start))
            {
                if (child == null) return 125;
                child.StandardInput.Close();
                var stdout = Task.Factory.StartNew(() => Capture(child.StandardOutput.BaseStream, Path.Combine(root, "stdout.txt")));
                var stderr = Task.Factory.StartNew(() => Capture(child.StandardError.BaseStream, Path.Combine(root, "stderr.txt")));
                child.WaitForExit();
                // A background descendant can retain the child's pipe handles.
                // Do not wait until the outer test deadline after the command
                // already exited. Captured blocks are flushed below; the host
                // kills remaining job members when this runner exits.
                var drained = Task.WaitAll(new Task[] { stdout, stderr }, 1500);
                if (!drained || stdout.Result || stderr.Result) File.WriteAllText(Path.Combine(root, "logs-truncated"), "true");
                return child.ExitCode;
            }
        }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(root, "runner-error.txt"), error.Message); } catch { }
            return 125;
        }
    }

    private static bool Capture(Stream input, string destination)
    {
        var buffer = new byte[16384];
        int written = 0, count;
        bool truncated = false;
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                int keep = Math.Min(count, LogLimit - written);
                output.Write(buffer, 0, keep);
                if (keep > 0) output.Flush(true);
                written += keep;
                truncated |= count > keep;
                if (count > keep && Interlocked.Exchange(ref _truncated, 1) == 0)
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs-truncated"), "true");
                // Continue draining after the limit so the child cannot block
                // on a full stdout/stderr pipe.
            }
            output.Flush(true);
        }
        return truncated;
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
