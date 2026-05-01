using System.Buffers.Binary;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway.Tests;

public class EstablishDecoderTests
{
    private static byte[] BuildBlock(uint sid, ulong ver, ulong ts, ulong keepAlive,
        uint nextSeq, ushort cod, ulong codTimeout)
    {
        var buf = new byte[EstablishDecoder.BlockLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), sid);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(4, 8), ver);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(12, 8), ts);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(20, 8), keepAlive);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), nextSeq);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), cod);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(34, 8), codTimeout);
        return buf;
    }

    [Fact]
    public void Decode_roundtrips_all_fields()
    {
        var block = BuildBlock(0xDEAD_BEEFu, 0x1122_3344_5566_7788UL,
            0xAABB_CCDD_EEFF_0011UL, keepAlive: 5_000UL, nextSeq: 1, cod: 1,
            codTimeout: 30_000UL);
        Assert.True(EstablishDecoder.TryDecode(block, out var req, out var err));
        Assert.Null(err);
        Assert.Equal(0xDEAD_BEEFu, req.SessionId);
        Assert.Equal(0x1122_3344_5566_7788UL, req.SessionVerId);
        Assert.Equal(0xAABB_CCDD_EEFF_0011UL, req.TimestampNanos);
        Assert.Equal(5_000UL, req.KeepAliveIntervalMillis);
        Assert.Equal(1u, req.NextSeqNo);
        Assert.Equal((ushort)1, req.CancelOnDisconnectType);
        Assert.Equal(30_000UL, req.CodTimeoutWindowMillis);
    }

    [Fact]
    public void Decode_truncated_block_rejects()
    {
        var block = new byte[EstablishDecoder.BlockLength - 1];
        Assert.False(EstablishDecoder.TryDecode(block, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("Establish", err);
    }
}
