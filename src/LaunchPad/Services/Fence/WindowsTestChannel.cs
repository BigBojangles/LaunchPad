using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LaunchPad.Services.Fence;

// One request per connection/key. MACs bind metadata (including every payload
// hash) to this generation and direction; reconnect creates another fresh key.
public sealed class WindowsTestChannel : IDisposable
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly string _generation;
    public WindowsTestChannel(string generation)
    {
        if (!Guid.TryParseExact(generation, "N", out _)) throw new ArgumentException("Invalid bridge generation.");
        _generation = generation;
    }

    public async Task<WindowsTestResponse> ServeAsync(Stream channel, WindowsTestBridge bridge, CancellationToken token, Action? readyCallback = null)
    {
        using var transfer = new IdleTransferStream(channel, TimeSpan.FromSeconds(30));
        await WriteLineAsync(transfer, "LP-WINDOWS-HOST 1 " + _generation + " " + Convert.ToHexString(_key).ToLowerInvariant(), token).ConfigureAwait(false);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(60));
            var ready = await ReadLineAsync(channel, handshake.Token).ConfigureAwait(false);
            if (ready != "LP-WINDOWS-GUEST 1 " + Sign("ready", [])) throw new InvalidDataException("Windows test broker authentication failed.");
        }
        readyCallback?.Invoke();
        string proof;
        // Older guests do not have a broker. Readiness timeout is diagnostic;
        // it neither blocks the agent nor causes another launch/install.
        // Idle agents may run for hours before requesting a Windows test. Only
        // owner shutdown cancels this idle wait; actual payload reads are bounded.
        proof = await ReadProofAsync(channel, transfer, token).ConfigureAwait(false);
        const string prefix = "LP-WINDOWS-PROOF 1 ";
        if (!proof.StartsWith(prefix, StringComparison.Ordinal) || proof.Length != prefix.Length + 64)
            throw new InvalidDataException("Invalid Windows test request proof.");
        var expected = proof[prefix.Length..];
        return await bridge.ProcessAsync(transfer, transfer, token,
            metadata => Verify("request", metadata.Span, expected),
            (metadata, stop) => WriteLineAsync(transfer, "LP-WINDOWS-RESULT-PROOF 1 " + Sign("response", metadata.Span), stop)).ConfigureAwait(false);
    }

    private string Sign(string direction, ReadOnlySpan<byte> metadata)
    {
        var prefix = Encoding.ASCII.GetBytes("LaunchPad.WindowsTest.v1\n" + _generation + "\n" + direction + "\n");
        var value = new byte[prefix.Length + 32];
        prefix.CopyTo(value, 0);
        SHA256.HashData(metadata).CopyTo(value.AsSpan(prefix.Length));
        return Convert.ToHexString(HMACSHA256.HashData(_key, value)).ToLowerInvariant();
    }

    private bool Verify(string direction, ReadOnlySpan<byte> metadata, string expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(direction, metadata)), Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private static async Task<string> ReadProofAsync(Stream channel, Stream transfer, CancellationToken token)
    {
        var first = new byte[1];
        await channel.ReadExactlyAsync(first, token).ConfigureAwait(false);
        if (first[0] == '\n') return "";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return Encoding.ASCII.GetString(first) + await ReadLineAsync(transfer, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException("Windows test request proof timed out."); }
    }

    internal static async Task<string> ReadLineAsync(Stream channel, CancellationToken token)
    {
        var data = new byte[192];
        for (var count = 0; count < data.Length; count++)
        {
            await channel.ReadExactlyAsync(data.AsMemory(count, 1), token).ConfigureAwait(false);
            if (data[count] == '\n') return Encoding.ASCII.GetString(data, 0, count);
        }
        throw new InvalidDataException("Bridge handshake line too large.");
    }

    internal static async Task WriteLineAsync(Stream channel, string line, CancellationToken token)
    {
        await channel.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), token).ConfigureAwait(false);
        await channel.FlushAsync(token).ConfigureAwait(false);
    }

    internal sealed class IdleTransferStream(Stream source, TimeSpan idle) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(idle);
            try { return await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Windows test transfer read timed out."); }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(idle);
            try { await source.WriteAsync(buffer, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Windows test result transfer timed out."); }
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(idle);
            try { await source.FlushAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Windows test result flush timed out."); }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
