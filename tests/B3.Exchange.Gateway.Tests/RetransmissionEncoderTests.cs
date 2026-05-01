using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Wire-layout tests for <see cref="RetransmissionEncoder"/> (issue #46
/// session-layer responses to <c>RetransmitRequest</c>).
/// </summary>
public class RetransmissionEncoderTests
{
    [Fact]
    public void EncodeRetransmission_layout_is_stable()
    {
        Span<byte> buf = stackalloc byte[RetransmissionEncoder.RetransmissionTotal];
        int n = RetransmissionEncoder.EncodeRetransmission(buf,
            sessionId: 0xDEADBEEFu, requestTimestampNanos: 0x0102030405060708UL,
            firstRetransmittedSeqNo: 42u, count: 3u);
        Assert.Equal(RetransmissionEncoder.RetransmissionTotal, n);

        // SOFH (0..3): messageLength = 32 LE; encodingType = 0xEB50 LE.
        Assert.Equal((ushort)RetransmissionEncoder.RetransmissionTotal,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(0, 2)));

        // SBE header (4..11): blockLength=20, templateId=13, schemaId, version=0.
        Assert.Equal((ushort)20, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4, 2)));
        Assert.Equal((ushort)EntryPointFrameReader.TidRetransmission,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(6, 2)));

        // Body (12..31): SessionID(4) | Timestamp(8) | NextSeqNo(4) | Count(4)
        Assert.Equal(0xDEADBEEFu, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4)));
        Assert.Equal(0x0102030405060708UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(16, 8)));
        Assert.Equal(42u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(24, 4)));
        Assert.Equal(3u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(28, 4)));
    }

    [Fact]
    public void EncodeRetransmitReject_layout_is_stable()
    {
        Span<byte> buf = stackalloc byte[RetransmissionEncoder.RetransmitRejectTotal];
        int n = RetransmissionEncoder.EncodeRetransmitReject(buf,
            sessionId: 7u, requestTimestampNanos: 99UL,
            B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE);
        Assert.Equal(RetransmissionEncoder.RetransmitRejectTotal, n);

        Assert.Equal((ushort)13,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4, 2))); // blockLength
        Assert.Equal((ushort)EntryPointFrameReader.TidRetransmitReject,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(6, 2)));
        Assert.Equal(7u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4)));
        Assert.Equal(99UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(16, 8)));
        Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE, buf[24]);
    }
}
