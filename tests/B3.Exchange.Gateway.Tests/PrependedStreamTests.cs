using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class PrependedStreamTests
{
    [Fact]
    public async Task ReadAsync_returns_prefix_then_inner()
    {
        var prefix = new byte[] { 1, 2, 3 };
        var inner = new MemoryStream(new byte[] { 4, 5, 6 });
        var s = new PrependedStream(prefix, inner);

        var buf = new byte[8];
        int n1 = await s.ReadAsync(buf.AsMemory(0, 8));
        // ReadAsync may return prefix in one call; subsequent reads
        // pull from inner. Concatenate until we observe EOF.
        var read = new List<byte>();
        read.AddRange(buf.AsSpan(0, n1).ToArray());
        while (true)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, 8));
            if (n == 0) break;
            read.AddRange(buf.AsSpan(0, n).ToArray());
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, read.ToArray());
    }

    [Fact]
    public void Read_partial_prefix_then_remainder()
    {
        var prefix = new byte[] { 10, 20, 30, 40 };
        var inner = new MemoryStream(new byte[] { 50 });
        var s = new PrependedStream(prefix, inner);

        var buf = new byte[2];
        Assert.Equal(2, s.Read(buf, 0, 2));
        Assert.Equal(new byte[] { 10, 20 }, buf);
        Assert.Equal(2, s.Read(buf, 0, 2));
        Assert.Equal(new byte[] { 30, 40 }, buf);
        Assert.Equal(1, s.Read(buf, 0, 2));
        Assert.Equal((byte)50, buf[0]);
        Assert.Equal(0, s.Read(buf, 0, 2));
    }

    [Fact]
    public void Write_forwards_to_inner()
    {
        var prefix = new byte[] { 1 };
        var inner = new MemoryStream();
        var s = new PrependedStream(prefix, inner);
        s.Write(new byte[] { 99, 100 }, 0, 2);
        Assert.Equal(new byte[] { 99, 100 }, inner.ToArray());
    }

    [Fact]
    public void Empty_prefix_passes_through()
    {
        var inner = new MemoryStream(new byte[] { 7, 8 });
        var s = new PrependedStream(Array.Empty<byte>(), inner);
        var buf = new byte[4];
        int n = s.Read(buf, 0, 4);
        Assert.Equal(2, n);
        Assert.Equal(new byte[] { 7, 8 }, buf.AsSpan(0, 2).ToArray());
    }
}
