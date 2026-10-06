using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace LaunchPad.Services.Fence;

public sealed record ReturnedContent(string Path, long Size, string Sha256);

public sealed record ProjectReturnReceipt(IReadOnlyList<string> Files, bool Complete, string? Error,
    IReadOnlyList<ReturnedContent>? Contents = null)
{
    public bool HasContentIdentities => Files is not null && Contents is not null && Files.Count == Contents.Count
        && Contents.All(item => item is not null && item.Size >= 0 && FenceFiles.IsSafe(item.Path)
            && item.Sha256 is { Length: 64 } && item.Sha256.All(Uri.IsHexDigit))
        && Contents.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Contents.Count
        && Contents.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Files);
}

public static class ProjectPull
{
    public static ProjectReturnReceipt Receive(int port, string destDir, TimeSpan timeout)
    {
        using var client = new TcpClient();
        try
        {
            if (!client.ConnectAsync("127.0.0.1", port).Wait(timeout))
                return new(Array.Empty<string>(), false, "The return connection timed out.");
            using var stream = client.GetStream();
            return Receive(stream, destDir, timeout);
        }
        catch (Exception error) when (error is IOException or SocketException or AggregateException)
        {
            return new(Array.Empty<string>(), false, "The return connection failed.");
        }
    }

    // Same parser for the actual channel and deterministic truncated/unsafe fixtures.
    public static ProjectReturnReceipt Receive(Stream stream, string destDir, TimeSpan timeout)
    {
        var saved = new List<string>();
        var contents = new List<ReturnedContent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = Stopwatch.StartNew();
        string? rejection = null;
        try
        {
            if (!FenceFiles.TryResolveUnlinked(destDir, "receipt-probe", out _))
                throw new InvalidDataException("The return directory contains a link.");
            Directory.CreateDirectory(destDir);
            var first = ReadLine();
            if (!first.StartsWith("PROJECT ", StringComparison.Ordinal)
                || !int.TryParse(first[8..], out var count) || count < 0 || count > 1_000_000)
                throw new InvalidDataException("The return header is invalid.");
            for (var index = 0; index < count; index++)
            {
                var line = ReadLine();
                if (!line.StartsWith("FILE ", StringComparison.Ordinal))
                    throw new InvalidDataException("The declared return file is missing.");
                var space = line.IndexOf(' ', 5);
                if (space <= 5 || !long.TryParse(line.AsSpan(5, space - 5),
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0)
                    throw new InvalidDataException("The return file length is invalid.");
                var relative = line[(space + 1)..];
                var safe = FenceFiles.TryResolveUnlinked(destDir, relative, out var target) && seen.Add(relative);
                if (!safe) rejection ??= "The return contains an unsafe or duplicate path.";
                FileStream? output = null;
                using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                try
                {
                    if (safe)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (!FenceFiles.TryResolveUnlinked(destDir, relative, out target))
                            throw new InvalidDataException("The return path changed to a link.");
                        output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    }
                    var piece = new byte[65536];
                    for (long left = size; left > 0;)
                    {
                        SetTimeout();
                        var read = stream.Read(piece, 0, (int)Math.Min(piece.Length, left));
                        if (read <= 0) throw new EndOfStreamException("The return file was truncated.");
                        output?.Write(piece, 0, read);
                        digest.AppendData(piece, 0, read);
                        left -= read;
                    }
                    output?.Flush(flushToDisk: true);
                    if (safe)
                    {
                        saved.Add(relative);
                        contents.Add(new ReturnedContent(relative, size, Convert.ToHexString(digest.GetHashAndReset())));
                    }
                }
                finally { output?.Dispose(); }
            }
            if (ReadLine() != "PROJECT-END")
                throw new InvalidDataException("The return did not finish with its completion marker.");
            return new(saved, rejection is null, rejection, contents);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new(saved, false, error.Message, contents);
        }

        void SetTimeout()
        {
            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) throw new IOException("The return transfer timed out.");
            if (stream.CanTimeout) stream.ReadTimeout = (int)Math.Clamp(left.TotalMilliseconds, 1, int.MaxValue);
        }
        string ReadLine()
        {
            var bytes = new List<byte>();
            while (bytes.Count <= 8192)
            {
                SetTimeout();
                var next = stream.ReadByte();
                if (next < 0) throw new EndOfStreamException("The return header was truncated.");
                if (next == '\n') return new UTF8Encoding(false, true).GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add((byte)next);
            }
            throw new InvalidDataException("The return header is too long.");
        }
    }
}
