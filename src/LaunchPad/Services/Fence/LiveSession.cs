using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

public static class LiveSession
{
    private static readonly object Gate = new();
    private sealed record Entry(string Directory, int Qmp, StatusLink Status, int? ProcessId, string AgentId)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
    private static readonly Dictionary<string, Entry> Sessions = new(StringComparer.OrdinalIgnoreCase);

    public static void Begin(string project, string directory, int qmpPort, StatusLink status, int? processId = null, string agentId = AgentChoice.Grok)
    {
        lock (Gate)
        {
            Sessions[Normalize(project)] = new Entry(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), qmpPort, status, processId, agentId);
        }
    }

    public static void End(string project, StatusLink? expectedStatus = null)
    {
        lock (Gate)
        {
            var key = Normalize(project);
            if (!Sessions.TryGetValue(key, out var entry) || (expectedStatus is not null && !ReferenceEquals(entry.Status, expectedStatus)))
                return;
            Sessions.Remove(key);
        }
    }

    public static async Task<string?> TrySendAsync(string project, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(Normalize(project), out entry!))
                return "Open the project first. The agent stops while the files move.";
        }
        await entry.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var paused = false;
        var failurePath = Path.Combine(entry.Directory, "resend.failed");
        try
        {
            lock (Gate)
            {
                if (!Sessions.TryGetValue(Normalize(project), out var current) || !ReferenceEquals(current, entry))
                    return "The session changed before files could be sent. Open the project again.";
            }
            if (ProjectSessionStore.HasUnconfirmedImport(entry.Directory))
                return "The previous send was not confirmed. The VM and Windows files are preserved; recovery is required before another send.";
            var serial = Path.Combine(entry.Directory, "serial.log");
            if (!ReadShared(serial).Split('\n').Any(line => line.TrimEnd('\r') is "DOOR-READY safe-import" or "DOOR-READY safe-import safe-merge"))
                return "This VM needs the safe file-import runtime update before files can be resent. Its current work is preserved.";
            // An absent host peer makes the guest's receive loop see immediate EOF.
            using var client = await FenceHost.ConnectAsync(QemuCommand.FencePort(entry.Qmp), cancellationToken).ConfigureAwait(false);
            using var log = new FileStream(serial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            log.Seek(0, SeekOrigin.End);
            using var reader = new StreamReader(log);
            var markers = new MarkerReader(reader);
            using (var journal = new FileStream(failurePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var message = System.Text.Encoding.UTF8.GetBytes("Send in progress; guest completion and baseline update are not yet confirmed.");
                journal.Write(message);
                journal.Flush(flushToDisk: true);
            }
            entry.Status.SendPause();
            paused = true;
            await markers.WaitAsync("DOOR-OPEN safe-import", cancellationToken).ConfigureAwait(false);
            var manifestPath = Path.Combine(entry.Directory, "sent.manifest");
            var manifest = await FenceHost.SendProjectAsync(client.GetStream(), project,
                Path.Combine(entry.Directory, "copy.progress"), manifestPath, cancellationToken).ConfigureAwait(false);
            // EOF completes the guest import; wait for that import, not old log text.
            client.Dispose();
            await markers.WaitAsync("DOOR-CLOSED", cancellationToken).ConfigureAwait(false);
            manifest.Save(manifestPath);
            File.Delete(failurePath);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            if (paused)
            {
                // Keep the last confirmed baseline and prevent a blind second import.
                try { File.WriteAllText(failurePath, "Send completion was not confirmed. " + error.Message); }
                catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException) { }
            }
            if (error is OperationCanceledException) throw;
            return "Files were not confirmed sent: " + error.Message + " The Windows project and VM disk are preserved.";
        }
        finally { entry.SendGate.Release(); }
    }

    private sealed class MarkerReader(StreamReader reader)
    {
        private string _pending = "";
        public async Task WaitAsync(string marker, CancellationToken cancellationToken)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var buffer = new char[4096];
            while (timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (!_pending.Contains('\n'))
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _pending += new string(buffer, 0, count);
                }
                int newline;
                while ((newline = _pending.IndexOf('\n')) >= 0)
                {
                    var line = _pending[..newline].TrimEnd('\r');
                    _pending = _pending[(newline + 1)..];
                    if (line == "AGENT-TERMINAL-FAILED") throw new IOException("The VM could not attach the replacement agent to its terminal.");
                    if (line == "AGENT-POLICY-FAILED") throw new IOException("The VM could not enforce the replacement agent policy.");
                    if (line == "DOOR-FAILED") throw new IOException("The guest could not complete the file import.");
                    if (line == marker) return;
                }
                if (_pending.Length > 8192) _pending = _pending[^8192..];
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new IOException("The guest did not confirm " + marker + ".");
            }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    public static SessionRecord? Describe(string project)
    {
        Entry? entry;
        var key = Normalize(project);
        lock (Gate) Sessions.TryGetValue(key, out entry);
        if (entry is null) return null;
        int? terminal = int.TryParse(ReadShared(Path.Combine(entry.Directory, "tui.pid")).Trim(), out var pid) && pid > 0 ? pid : null;
        var status = entry.Status.Snapshot;
        var ready = File.Exists(Path.Combine(entry.Directory, "console.ready"));
        if (ProjectSessionStore.HasUnconfirmedImport(entry.Directory))
            return new SessionRecord("vm:" + QemuLayout.ProjectKey(key), key, entry.AgentId,
                SessionKind.VirtualMachine, entry.ProcessId, terminal, null, SessionLifecycle.Failed,
                "The last file send or startup was not confirmed. VM work is preserved; review Saved work.");
        var state = status switch
        {
            (false, _) => SessionLifecycle.Unknown,
            (true, "busy") => SessionLifecycle.Busy,
            (true, "needs-an-answer") => SessionLifecycle.NeedsAnswer,
            (true, "idle") => SessionLifecycle.Running,
            _ => ready ? SessionLifecycle.Unknown : SessionLifecycle.Starting
        };
        return new SessionRecord("vm:" + QemuLayout.ProjectKey(key), key, entry.AgentId,
            SessionKind.VirtualMachine, entry.ProcessId, terminal, null, state,
            !status.Connected ? "The status connection closed. VM work is preserved; activity is unavailable." : null);
    }

    private static string Normalize(string project) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
}
