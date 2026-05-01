using System.Buffers.Binary;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway.Tests;

public class EstablishHandshakeEncodersTests
{
    [Fact]
    public void Ack_layout_matches_spec()
    {
        var buf = new byte[EstablishAckEncoder.Total];
        int n = EstablishAckEncoder.Encode(buf,
            sessionId: 12345, sessionVerId: 9,
            requestTimestampNanos: 0x1122334455667788UL,
            keepAliveIntervalMillis: 5_000UL,
            nextSeqNo: 1, lastIncomingSeqNo: 0,
            semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
        Assert.Equal(EstablishAckEncoder.Total, n);

        Assert.Equal(EstablishAckEncoder.Total, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2)));
        Assert.Equal(EntryPointFrameReader.SofhEncodingType, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2, 2)));
        Assert.Equal(EstablishAckEncoder.Block, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(4, 2)));
        Assert.Equal(EstablishAckEncoder.TemplateId, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6, 2)));
        Assert.Equal(EntryPointFrameReader.SchemaId, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(8, 2)));
        Assert.Equal(EstablishAckEncoder.SchemaVersion, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(10, 2)));

        var body = buf.AsSpan(12); // header is 8 bytes (SOFH + SBE header)
        Assert.Equal(12345u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        Assert.Equal(9UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)));
        Assert.Equal(0x1122334455667788UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)));
        Assert.Equal(5_000UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(20, 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(28, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(32, 4)));
        Assert.Equal(8, body[36]);
        Assert.Equal(4, body[37]);
        Assert.Equal(2, body[38]);
        Assert.Equal(0, body[39]);
    }

    [Fact]
    public void Reject_layout_with_lastIncomingSeqNo()
    {
        var buf = new byte[EstablishRejectEncoder.Total];
        int n = EstablishRejectEncoder.Encode(buf,
            sessionId: 7, sessionVerId: 1,
            requestTimestampNanos: 0xCAFEBABE_DEADBEEFUL,
            rejectCode: FixpSbe.EstablishRejectCode.INVALID_NEXTSEQNO,
            lastIncomingSeqNo: 42u);
        Assert.Equal(EstablishRejectEncoder.Total, n);

        Assert.Equal(EstablishRejectEncoder.TemplateId, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6, 2)));
        Assert.Equal(EstablishRejectEncoder.SchemaVersion, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(10, 2)));

        var body = buf.AsSpan(12);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)));
        Assert.Equal(0xCAFEBABE_DEADBEEFUL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)));
        Assert.Equal((byte)FixpSbe.EstablishRejectCode.INVALID_NEXTSEQNO, body[20]);
        Assert.Equal(0, body[21]); // padding
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(22, 4)));
    }

    [Fact]
    public void Reject_omits_lastIncomingSeqNo_via_null_sentinel()
    {
        var buf = new byte[EstablishRejectEncoder.Total];
        EstablishRejectEncoder.Encode(buf,
            sessionId: 7, sessionVerId: 1, requestTimestampNanos: 0,
            rejectCode: FixpSbe.EstablishRejectCode.UNNEGOTIATED,
            lastIncomingSeqNo: null);
        var body = buf.AsSpan(12);
        Assert.Equal(FixpSbe.EstablishRejectData.LastIncomingSeqNoNullValue,
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(22, 4)));
    }
}
