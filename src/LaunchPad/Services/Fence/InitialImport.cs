using System.Diagnostics;
using System.Text;

namespace LaunchPad.Services.Fence;

public static class InitialImport
{
    public const string JournalName = "initial-import.failed";

    public static void Begin(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, JournalName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes("Startup import is not yet confirmed. The previous guest disk and host baseline must be preserved."));
        stream.Flush(flushToDisk: true);
        var serial = Path.Combine(directory, "serial.log");
        if (File.Exists(serial)) File.Move(serial, Path.Combine(directory, "serial-" + Guid.NewGuid().ToString("N") + ".log"));
    }

    public static void Commit(string directory, SentManifest manifest)
    {
        manifest.Save(Path.Combine(directory, "sent.manifest"));
        File.Delete(Path.Combine(directory, JournalName));
    }

    public static async Task WaitForMarkerAsync(string path, string marker, CancellationToken token, bool prefix = false,
        TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < (timeout ?? TimeSpan.FromSeconds(30)))
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = (await reader.ReadToEndAsync(token).ConfigureAwait(false)).Split('\n');
                foreach (var text in lines.Take(lines.Length - 1))
                {
                    var line = text.TrimEnd('\r');
                    if (line == "AGENT-POLICY-FAILED")
                        throw new IOException("The VM could not enforce the agent policy. Its disk and previous baseline were preserved.");
                    if (line == "AGENT-TERMINAL-FAILED")
                        throw new IOException("The VM could not attach the agent to its terminal. Its disk and previous baseline were preserved.");
                    if (line == "IMPORT-FAILED" || line == "DOOR-FAILED")
                        throw new IOException("The VM import stopped to preserve saved work. Review the saved VM before restarting.");
                    if (line == marker || (prefix && line.StartsWith(marker + " ", StringComparison.Ordinal))) return;
                }
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new IOException("The VM did not confirm a safe startup import. Its disk and previous baseline were preserved; a runtime update or recovery is required.");
    }
}
