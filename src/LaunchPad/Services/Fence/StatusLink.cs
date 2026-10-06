using System.Net.Sockets;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed class StatusLink : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StatusBuffer _buffer = new();
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private bool _closed;

    public StatusLink(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _ = Task.Run(ReadLoop);
    }

    public bool SizeAccepted
    {
        get
        {
            lock (_gate)
                return _buffer.SizeAccepted;
        }
    }

    public string? Activity
    {
        get { lock (_gate) return _buffer.Activity; }
    }

    public (bool Connected, string? Activity) Snapshot
    {
        get { lock (_gate) return (!_closed, _closed ? null : _buffer.Activity); }
    }

    public void SendPause()
    {
        Write(Encoding.ASCII.GetBytes("PAUSE\n"));
    }

    public void SendWinsize(int rows, int cols)
    {
        var line = Encoding.ASCII.GetBytes(
            "WINSIZE "
            + rows.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " "
            + cols.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n");
        Write(line);
    }

    public async Task<byte[]?> RequestAuthAsync(TimeSpan timeout)
    {
        Write(Encoding.ASCII.GetBytes("AUTH-OUT\n"));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_buffer.AuthFinished)
                    return _buffer.AuthBody;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return null;
    }

    public bool HomeTooBig
    {
        get
        {
            lock (_gate)
                return _buffer.HomeTooBig;
        }
    }

    public async Task<byte[]?> RequestHomeAsync(TimeSpan timeout)
    {
        Write(Encoding.ASCII.GetBytes("HOME-OUT\n"));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_buffer.HomeFinished)
                    return _buffer.HomeBody;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        try
        {
            _client.Dispose();
        }
        catch
        {
            // The status port is already closed.
        }
    }

    private void Write(byte[] line)
    {
        // The read loop must keep draining the guest while this write waits.
        // Holding one lock across both sides stalls the window-size exchange.
        lock (_writeGate)
        {
            try
            {
                _stream.Write(line, 0, line.Length);
            }
            catch
            {
                // The guest has already closed the status port.
            }
        }
    }

    private async Task ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                    break;

                lock (_gate)
                    _buffer.Push(buffer, read);
            }
        }
        catch
        {
            // The session closed the status port.
        }
        finally { lock (_gate) _closed = true; }
    }
}
