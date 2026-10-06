using System.Runtime.InteropServices;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class AmsiStreamTests
{
    [Fact]
    public void OrdinaryShortReadsFillOneProviderRequestAcrossTheFormerSplit()
    {
        var expected = Encoding.UTF8.GetBytes("Complete contiguous content despite short reads.");
        using var input = new ShortReads(expected);
        var content = new AmsiContentStream(input, "owned.txt");
        var target = Marshal.AllocHGlobal(expected.Length);
        try
        {
            Assert.Equal(0, content.Read(0, (uint)expected.Length, target, out var copied));
            Assert.Equal((uint)expected.Length, copied);
            var received = new byte[copied]; Marshal.Copy(target, received, 0, received.Length);
            Assert.Equal(expected, received);
            Assert.False(content.ReadFailed);
            Assert.True(input.Calls > 1);
            Assert.True(input.CanRead); // The adapter never takes ownership of the source lease.
        }
        finally { Marshal.FreeHGlobal(target); }
    }

    [Fact]
    public void PositionalRequestsCanCrossAnInternalReadBufferAndRevisitEarlierBytes()
    {
        var expected = new byte[140000]; new Random(42).NextBytes(expected);
        using var input = new MemoryStream(expected);
        var content = new AmsiContentStream(input, "owned.txt");
        var target = Marshal.AllocHGlobal(100000);
        try
        {
            foreach (var position in new[] { 20000, 0 })
            {
                Assert.Equal(0, content.Read((ulong)position, 100000, target, out var copied));
                Assert.Equal(100000u, copied);
                var received = new byte[copied]; Marshal.Copy(target, received, 0, received.Length);
                Assert.Equal(expected.AsSpan(position, received.Length).ToArray(), received);
            }
            Assert.Equal(200000, content.BytesRead);
        }
        finally { Marshal.FreeHGlobal(target); }
    }

    [Fact]
    public void NativeMetadataPreservesUnicodeAndReportsRequiredBufferSize()
    {
        using var input = new MemoryStream(new byte[123]);
        var content = new AmsiContentStream(input, "owned-Δ.txt");
        Assert.True(content.GetAttribute(1, 0, IntPtr.Zero, out var needed) < 0);
        Assert.Equal(Encoding.Unicode.GetByteCount("owned-Δ.txt\0"), (int)needed);
        var target = Marshal.AllocHGlobal((int)needed);
        try
        {
            Assert.Equal(0, content.GetAttribute(1, needed, target, out var written));
            Assert.Equal(needed, written);
            Assert.Equal("owned-Δ.txt", Marshal.PtrToStringUni(target));
            Assert.True(content.GetAttribute(3, needed, target, out _) < 0); // Provider must read the held stream.
        }
        finally { Marshal.FreeHGlobal(target); }
    }

    [Fact]
    public void ContentLengthMetadataIsNotTruncatedAtFourGiB()
    {
        using var input = new LargeLogicalStream();
        var content = new AmsiContentStream(input, "owned-logical.txt");
        var target = Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(0, content.GetAttribute(2, 8, target, out var written));
            Assert.Equal(8u, written);
            Assert.Equal(input.Length, Marshal.ReadInt64(target));
        }
        finally { Marshal.FreeHGlobal(target); }
    }

    [Fact]
    public void TruncatedAdvertisedContentIsAnErrorRatherThanAnAllowedShortFile()
    {
        using var input = new TruncatedStream();
        var content = new AmsiContentStream(input, "owned-truncated.txt");
        var target = Marshal.AllocHGlobal(20);
        try
        {
            Assert.True(content.Read(0, 20, target, out _) < 0);
            Assert.True(content.ReadFailed);
        }
        finally { Marshal.FreeHGlobal(target); }
    }

    [Fact]
    public void ContentLengthChangeCannotReceiveASuccessfulRead()
    {
        using var input = new MemoryStream(); input.SetLength(20);
        var content = new AmsiContentStream(input, "owned-changed.txt"); input.SetLength(19);
        var target = Marshal.AllocHGlobal(20);
        try { Assert.True(content.Read(0, 20, target, out _) < 0); Assert.True(content.ReadFailed); }
        finally { Marshal.FreeHGlobal(target); }
    }

    [Fact]
    public void EndOfFileAndZeroLengthRequestsHaveDistinctResults()
    {
        using var input = new MemoryStream(new byte[1]);
        var content = new AmsiContentStream(input, "owned.txt");
        Assert.Equal(0, content.Read(0, 0, IntPtr.Zero, out var empty)); Assert.Equal(0u, empty);
        Assert.True(content.Read(1, 1, IntPtr.Zero, out _) < 0);
        Assert.False(content.ReadFailed);
    }

    private sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    {
        public int Calls;
        public override int Read(byte[] buffer, int offset, int count) { Calls++; return base.Read(buffer, offset, Math.Min(count, 7)); }
    }
    private sealed class TruncatedStream() : MemoryStream(new byte[2])
    { public override long Length => 20; }
    private sealed class LargeLogicalStream() : MemoryStream()
    { public override long Length => 4L * 1024 * 1024 * 1024 + 123; }
}
