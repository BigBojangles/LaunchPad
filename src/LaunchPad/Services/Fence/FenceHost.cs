using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed class HandOff
{
    public HandOff(Process? tui, StatusLink status)
    {
        Tui = tui;
        Status = status;
    }

    public Process? Tui { get; }
    public StatusLink Status { get; }
}

public static class FenceHost
{
    public static async Task<HandOff> HandOffAsync(string project, int qmpPort, string title, string pidFile, CancellationToken cancellationToken, AgentLaunch agent = default,
        bool existingSession = false, bool resumeOnly = false)
    {
        var tui = TuiWindow.Show(title, QemuCommand.TuiPort(qmpPort), pidFile);
        var sessionDir = Path.GetDirectoryName(pidFile);
        var directory = sessionDir ?? throw new IOException("The session directory is missing.");
        StatusLink? status = null;
        try
        {
            // Supply the console handshake before waiting for guest readiness;
            // the ongoing resize relay takes over after this handoff returns.
            using var sizing = await ConsoleSizeLink.ConnectAsync(qmpPort, cancellationToken).ConfigureAwait(false);
            sizing?.Send(80, 30);
            var manifestPath = Path.Combine(directory, "sent.manifest");
            SentManifest manifest;
            using (var client = await ConnectAsync(QemuCommand.FencePort(qmpPort), cancellationToken).ConfigureAwait(false))
            {
                if (existingSession)
                    await InitialImport.WaitForMarkerAsync(Path.Combine(directory, "serial.log"), "IMPORT-READY safe-merge", cancellationToken).ConfigureAwait(false);
                manifest = await SendProjectAsync(client.GetStream(), project, Path.Combine(directory, "copy.progress"), manifestPath,
                    cancellationToken, agent, restoreGuestState: true, restoreHostHome: !existingSession,
                    preserveRepositoryMetadata: existingSession, sendProjectFiles: !resumeOnly).ConfigureAwait(false);
            }
            // The initial status write must drain before the guest announces readiness.
            status = new StatusLink(await ConnectAsync(QemuCommand.StatusPort(qmpPort), cancellationToken).ConfigureAwait(false));
            await InitialImport.WaitForMarkerAsync(Path.Combine(directory, "serial.log"),
                existingSession ? "DOOR-READY safe-import safe-merge" : "DOOR-READY", cancellationToken,
                prefix: !existingSession, timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            InitialImport.Commit(directory, manifest);
            return new HandOff(tui, status);
        }
        catch { status?.Dispose(); tui?.Dispose(); throw; }
    }

    public static Task SendProjectAsync(int port, string project, CancellationToken cancellationToken) =>
        SendProjectAsync(port, project, null, null, cancellationToken);

    public static async Task SendProjectAsync(int port, string project, string? progressPath, string? manifestPath, CancellationToken cancellationToken, AgentLaunch agent = default)
    {
        using var client = await ConnectAsync(port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var manifest = await SendProjectAsync(stream, project, progressPath, manifestPath, cancellationToken, agent,
            restoreGuestState: true, preserveRepositoryMetadata: false).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(manifestPath))
            manifest.Save(manifestPath);
    }

    // Resends reuse the connected channel and the guest's existing agent/home.
    // The caller commits the baseline only after a fresh guest completion marker.
    public static async Task<SentManifest> SendProjectAsync(Stream stream, string project, string? progressPath,
        string? manifestPath, CancellationToken cancellationToken, AgentLaunch agent = default, bool restoreGuestState = false,
        bool restoreHostHome = true, bool preserveRepositoryMetadata = true, bool sendProjectFiles = true)
    {
        if (restoreGuestState)
        {
            var chosen = string.IsNullOrEmpty(agent.Id) ? AgentLaunch.Grok : agent;
            var agentHeader = Encoding.ASCII.GetBytes(AgentChoice.Header(chosen.Id));
            await stream.WriteAsync(agentHeader, cancellationToken).ConfigureAwait(false);
            var programHeader = AgentChoice.ProgramHeader(chosen.Program);
            if (chosen.Id == AgentChoice.Custom && programHeader is not null)
                await stream.WriteAsync(Encoding.ASCII.GetBytes(programHeader), cancellationToken).ConfigureAwait(false);
            if (chosen.Id == AgentChoice.Custom && programHeader is not null
                && !string.IsNullOrWhiteSpace(chosen.SourceFile) && File.Exists(chosen.SourceFile))
            {
                var program = await File.ReadAllBytesAsync(chosen.SourceFile, cancellationToken).ConfigureAwait(false);
                var fileHeader = Encoding.ASCII.GetBytes("PROGRAM " + program.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + chosen.Program + "\n");
                await WriteChunksAsync(stream, fileHeader, program, cancellationToken).ConfigureAwait(false);
            }

            if (restoreHostHome)
            {
                var auth = GuestAuth.ReadHost();
                if (auth is not null)
                    await WriteChunksAsync(stream, Encoding.ASCII.GetBytes(GuestAuth.Header(auth.Length)), auth, cancellationToken).ConfigureAwait(false);
                var home = GuestHome.Read(QemuLayout.ProjectKey(project));
                if (home is not null)
                    await WriteChunksAsync(stream, Encoding.ASCII.GetBytes(GuestHome.Header(home.Length)), home, cancellationToken).ConfigureAwait(false);
            }
        }

        var manifest = SentManifest.Load(manifestPath);
        if (!sendProjectFiles) return manifest;
        var firstCopy = !manifest.Any;
        var pending = new List<(string Full, string Relative, long Length, long Ticks)>();
        long total = 0;
        foreach (var file in SendList.Collect(project, CopyScope.SendAll.Value))
        {
            if (preserveRepositoryMetadata && file.Relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            long ticks;
            try
            {
                ticks = new FileInfo(file.Full).LastWriteTimeUtc.Ticks;
            }
            catch
            {
                continue;
            }

            if (manifest.Unchanged(file.Relative, file.Length, ticks))
            {
                // A host edit can preserve both length and mtime. Verify content
                // before omitting it from an import used as a return baseline.
                await using var current = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(current, cancellationToken).ConfigureAwait(false));
                if (string.Equals(hash, manifest.ContentHash(file.Relative), StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            pending.Add((file.Full, file.Relative, file.Length, ticks));
            total += file.Length;
        }

        long files = 0;
        long bytes = 0;
        WriteProgress(progressPath, bytes, total, files, "", firstCopy);
        var piece = new byte[65536];
        foreach (var file in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            {
                await using var input = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != file.Length)
                    throw new IOException("A project file changed before it could be sent. Try sending again.");
                var header = Encoding.UTF8.GetBytes(FenceFiles.Header(file.Length, file.Relative));
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                long left = file.Length;
                while (left > 0)
                {
                    var want = (int)Math.Min(piece.Length, left);
                    var read = await input.ReadAsync(piece.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new EndOfStreamException("A project file could not be sent completely.");
                    await stream.WriteAsync(piece.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    digest.AppendData(piece, 0, read);
                    left -= read;
                    bytes += read;
                    WriteProgress(progressPath, bytes, total, files, file.Relative, firstCopy);
                }
            }
            manifest.Note(file.Relative, file.Length, file.Ticks, Convert.ToHexString(digest.GetHashAndReset()));
            files++;
            WriteProgress(progressPath, bytes, total, files, file.Relative, firstCopy);
        }

        return manifest;
    }

    private static async Task WriteChunksAsync(Stream stream, byte[] header, byte[] body, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        var offset = 0;
        while (offset < body.Length)
        {
            var count = Math.Min(65536, body.Length - offset);
            await stream.WriteAsync(body.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            offset += count;
        }
    }

    private static void WriteProgress(string? path, long sent, long total, long files, string name, bool firstCopy)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var clean = name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            var line = sent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + total.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + files.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + clean
                + "\t" + (firstCopy ? "1" : "0");
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(line);
        }
        catch
        {
            // The tab still opens if the progress file cannot be written.
        }
    }

    internal static async Task<TcpClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (SocketException) when (attempt < 99)
            {
                client.Dispose();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw new IOException("The fenced session did not open its port.");
    }
}
