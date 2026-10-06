using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed class ConsoleSizeLink : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _writeGate = new();

    private ConsoleSizeLink(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _ = Task.Run(Drain);
    }

    public static async Task<ConsoleSizeLink?> ConnectAsync(int qmpPort, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", qmpPort, cancellationToken).ConfigureAwait(false);
                var stream = client.GetStream();
                if (!await ReadUntilAsync(stream, "QMP", cancellationToken).ConfigureAwait(false))
                {
                    client.Dispose();
                    return null;
                }

                await WriteAsync(stream, "{\"execute\":\"qmp_capabilities\"}\n", cancellationToken).ConfigureAwait(false);
                if (!await ReadUntilAsync(stream, "return", cancellationToken).ConfigureAwait(false))
                {
                    client.Dispose();
                    return null;
                }

                return new ConsoleSizeLink(client);
            }
            catch (SocketException) when (attempt < 49)
            {
                client.Dispose();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch { client.Dispose(); throw; }
        }

        return null;
    }

    public void Send(int cols, int rows)
    {
        var json = "{\"execute\":\"chardev-window-size-changed\",\"arguments\":{\"id\":\"ttych\",\"cols\":"
            + cols.ToString(CultureInfo.InvariantCulture)
            + ",\"rows\":"
            + rows.ToString(CultureInfo.InvariantCulture)
            + "}}\n";
        var bytes = Encoding.ASCII.GetBytes(json);
        lock (_writeGate)
        {
            try
            {
                _stream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // QEMU closed the monitor.
            }
        }
    }

    public static void Quit(int qmpPort) => _ = Execute(qmpPort, "quit");

    // Request a guest ACPI shutdown; the caller must observe actual VM exit.
    public static bool RequestPowerDown(int qmpPort) => Execute(qmpPort, "system_powerdown");

    private static bool Execute(int qmpPort, string command)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            var wait = client.BeginConnect("127.0.0.1", qmpPort, null, null);
            if (!wait.AsyncWaitHandle.WaitOne(2000))
                return false;

            client.EndConnect(wait);
            using var stream = client.GetStream();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;
            var buffer = new byte[1024];
            try
            {
                stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                // The greeting can already be waiting on the next read.
            }

            var caps = Encoding.ASCII.GetBytes("{\"execute\":\"qmp_capabilities\"}\n");
            stream.Write(caps, 0, caps.Length);
            try
            {
                stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                // The command can still be sent after a missing response.
            }

            var request = Encoding.ASCII.GetBytes("{\"execute\":\"" + command + "\"}\n");
            stream.Write(request, 0, request.Length);
            return true;
        }
        catch
        {
            // The process kill is the backstop when the monitor is already gone.
            return false;
        }
        finally
        {
            try
            {
                client?.Dispose();
            }
            catch
            {
                // The monitor is already closed.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _client.Dispose();
        }
        catch
        {
            // The monitor is already closed.
        }
    }

    private async Task Drain()
    {
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                    break;
            }
        }
        catch
        {
            // The session closed the monitor.
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadUntilAsync(NetworkStream stream, string token, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var text = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stream.DataAvailable)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                return false;

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (text.ToString().Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
