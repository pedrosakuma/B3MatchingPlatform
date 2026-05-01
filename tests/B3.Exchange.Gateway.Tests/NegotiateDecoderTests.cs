using System.Buffers.Binary;
using System.Text;
using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class NegotiateDecoderTests
{
    private static byte[] BuildFixedBlock(uint sessionId = 10101, ulong sessionVerId = 7,
        ulong timestamp = 1000, uint enteringFirm = 42, uint onBehalf = 0)
    {
        var b = new byte[NegotiateDecoder.BlockLength];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12, 8), timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20, 4), enteringFirm);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24, 4), onBehalf);
        return b;
    }

    private static byte[] Segment(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var buf = new byte[1 + bytes.Length];
        buf[0] = (byte)bytes.Length;
        bytes.CopyTo(buf, 1);
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = 0;
        foreach (var p in parts) len += p.Length;
        var result = new byte[len];
        var i = 0;
        foreach (var p in parts) { p.CopyTo(result, i); i += p.Length; }
        return result;
    }

    [Fact]
    public void Decodes_complete_negotiate()
    {
        var fixedBlock = BuildFixedBlock();
        var varData = Concat(
            Segment("{\"auth_type\":\"basic\",\"username\":\"10101\",\"access_key\":\"k\"}"),
            Segment("127.0.0.1"),
            Segment("test-app"),
            Segment("1.0"));
        Assert.True(NegotiateDecoder.TryDecode(fixedBlock, varData, out var req, out var creds, out var err));
        Assert.Null(err);
        Assert.Equal(10101u, req.SessionId);
        Assert.Equal(7UL, req.SessionVerId);
        Assert.Equal(1000UL, req.TimestampNanos);
        Assert.Equal(42u, req.EnteringFirm);
        Assert.Equal("10101", System.Text.Json.JsonDocument.Parse(creds.ToArray()).RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public void Truncated_block_rejected()
    {
        var fixedBlock = new byte[10];
        Assert.False(NegotiateDecoder.TryDecode(fixedBlock, default, out _, out _, out var err));
        Assert.Contains("block length", err);
    }

    [Fact]
    public void Oversize_credentials_segment_rejected()
    {
        var fixedBlock = BuildFixedBlock();
        // declared length 200 — exceeds max 128.
        var varData = new byte[1 + 200];
        varData[0] = 200;
        Assert.False(NegotiateDecoder.TryDecode(fixedBlock, varData, out _, out _, out var err));
        Assert.Contains("credentials", err);
        Assert.Contains("max", err);
    }

    [Fact]
    public void Truncated_segment_rejected()
    {
        var fixedBlock = BuildFixedBlock();
        // declared length 10, only 3 bytes available.
        var varData = new byte[] { 10, 1, 2, 3 };
        Assert.False(NegotiateDecoder.TryDecode(fixedBlock, varData, out _, out _, out var err));
        Assert.Contains("truncated", err);
    }

    [Fact]
    public void Trailing_garbage_rejected()
    {
        var fixedBlock = BuildFixedBlock();
        var varData = Concat(
            Segment("{\"auth_type\":\"basic\",\"username\":\"u\",\"access_key\":\"k\"}"),
            Segment(""), Segment(""), Segment(""),
            new byte[] { 0xFF, 0xFF });
        Assert.False(NegotiateDecoder.TryDecode(fixedBlock, varData, out _, out _, out var err));
        Assert.Contains("trailing", err);
    }

    [Fact]
    public void Allows_omitted_trailing_segments()
    {
        var fixedBlock = BuildFixedBlock();
        var varData = Segment("{\"auth_type\":\"basic\",\"username\":\"u\",\"access_key\":\"k\"}");
        Assert.True(NegotiateDecoder.TryDecode(fixedBlock, varData, out _, out var creds, out _));
        Assert.NotEqual(0, creds.Length);
    }
}
