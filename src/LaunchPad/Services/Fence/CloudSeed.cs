using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed class CloudSeed : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancel = new();
    private readonly byte[] _userData;
    private readonly byte[] _metaData;

    public CloudSeed(string userData, string metaData)
    {
        _userData = Encoding.UTF8.GetBytes(userData);
        _metaData = Encoding.UTF8.GetBytes(metaData);
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(Serve);
    }

    public int Port { get; }

    public void Dispose()
    {
        _cancel.Cancel();
        _listener.Stop();
    }

    private async Task Serve()
    {
        while (!_cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancel.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Reply(client));
        }
    }

    private void Reply(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 5000;
            var request = new byte[1024];
            int read;
            try
            {
                read = stream.Read(request, 0, request.Length);
            }
            catch
            {
                return;
            }

            var text = Encoding.ASCII.GetString(request, 0, read);
            var path = text.Split(' ').Skip(1).FirstOrDefault() ?? "/";
            var query = path.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0)
                path = path[..query];

            byte[]? body = path switch
            {
                "/user-data" => _userData,
                "/meta-data" => _metaData,
                _ => null
            };

            var head = body is null
                ? "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes);
            if (body is not null)
                stream.Write(body);
        }
    }
}
