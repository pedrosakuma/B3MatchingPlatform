using System.Buffers.Binary;
using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;

namespace B3.Exchange.EntryPoint.Tests;

public class RejectEncoderTests
{
    [Fact]
    public void EncodeTerminate_WritesHeaderAndCoreFields()
    {
        var buf = new byte[SessionRejectEncoder.TerminateTotal];
        int n = SessionRejectEncoder.EncodeTerminate(buf,
            sessionId: 0xDEADBEEFu,
            sessionVerId: 0x0102030405060708UL,
            terminationCode: SessionRejectEncoder.TerminationCode.DecodingError);

        Assert.Equal(SessionRejectEncoder.TerminateTotal, n);
        Assert.Equal(21, n);

        // SBE header
        var hdr = buf.AsSpan(0, 8);
        Assert.Equal((ushort)SessionRejectEncoder.TerminateBlock, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(0, 2)));
        Assert.Equal(EntryPointFrameReader.TidTerminate, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(2, 2)));
        Assert.Equal((ushort)EntryPointFrameReader.SchemaId, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4, 2)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(6, 2)));

        // Body
        var body = buf.AsSpan(8);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)));
        Assert.Equal((byte)SessionRejectEncoder.TerminationCode.DecodingError, body[12]);
    }

    [Fact]
    public void EncodeBusinessMessageReject_WritesHeaderBlockAndVarData()
    {
        var buf = new byte[BusinessMessageRejectEncoder.TotalSize(12)];
        int n = BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(buf,
            sessionId: 7u, msgSeqNum: 99u, sendingTimeNanos: 1_700_000_000_000_000_000UL,
            refMsgType: BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(EntryPointFrameReader.TidSimpleNewOrder),
            refSeqNum: 42u,
            businessRejectRefId: 12345UL,
            businessRejectReason: BusinessMessageRejectEncoder.Reason.InvalidField,
            text: "invalid Side");

        Assert.Equal(BusinessMessageRejectEncoder.TotalSize(12), n);

        // Header: BlockLength=36, TemplateId=206, SchemaId=1, Version=0
        var hdr = buf.AsSpan(0, 8);
        Assert.Equal((ushort)BusinessMessageRejectEncoder.BusinessRejectBlock, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(0, 2)));
        Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(2, 2)));
        Assert.Equal((ushort)EntryPointFrameReader.SchemaId, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4, 2)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(6, 2)));

        // OutboundBusinessHeader
        var body = buf.AsSpan(8);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)));
        Assert.Equal(1_700_000_000_000_000_000UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8)));
        Assert.Equal((byte)0, body[16]);            // EventIndicator
        Assert.Equal((byte)255, body[17]);          // MarketSegmentID null

        // Block fields
        Assert.Equal((byte)15, body[18]);           // RefMsgType = SimpleNewOrder(15)
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)));     // RefSeqNum
        Assert.Equal(12345UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(24, 8))); // BusinessRejectRefID
        Assert.Equal(BusinessMessageRejectEncoder.Reason.InvalidField,
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(32, 4)));                   // BusinessRejectReason

        // VarData: memo (length 0) then text ("invalid Side", 11 ASCII bytes)
        var trailer = buf.AsSpan(8 + BusinessMessageRejectEncoder.BusinessRejectBlock);
        Assert.Equal((byte)0, trailer[0]);          // memo length
        Assert.Equal((byte)12, trailer[1]);         // text length
        var text = System.Text.Encoding.ASCII.GetString(trailer.Slice(2, 12));
        Assert.Equal("invalid Side", text);
    }

    [Fact]
    public void EncodeBusinessMessageReject_NoText_OmitsTextBytes()
    {
        var buf = new byte[BusinessMessageRejectEncoder.TotalSize(0)];
        int n = BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(buf,
            sessionId: 1u, msgSeqNum: 1u, sendingTimeNanos: 0UL,
            refMsgType: 0, refSeqNum: 0u, businessRejectRefId: 0UL,
            businessRejectReason: BusinessMessageRejectEncoder.Reason.Other,
            text: null);
        Assert.Equal(8 + BusinessMessageRejectEncoder.BusinessRejectBlock + 2, n);
        var trailer = buf.AsSpan(8 + BusinessMessageRejectEncoder.BusinessRejectBlock);
        Assert.Equal((byte)0, trailer[0]);
        Assert.Equal((byte)0, trailer[1]);
    }

    [Fact]
    public void EncodeBusinessMessageReject_TruncatesOverlongText()
    {
        var longText = new string('x', BusinessMessageRejectEncoder.MaxTextLength + 50);
        var buf = new byte[BusinessMessageRejectEncoder.TotalSize(BusinessMessageRejectEncoder.MaxTextLength)];
        int n = BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(buf,
            sessionId: 1u, msgSeqNum: 1u, sendingTimeNanos: 0UL,
            refMsgType: 0, refSeqNum: 0u, businessRejectRefId: 0UL,
            businessRejectReason: BusinessMessageRejectEncoder.Reason.Other,
            text: longText);
        Assert.Equal(BusinessMessageRejectEncoder.TotalSize(BusinessMessageRejectEncoder.MaxTextLength), n);
        var trailer = buf.AsSpan(8 + BusinessMessageRejectEncoder.BusinessRejectBlock);
        Assert.Equal((byte)0, trailer[0]);
        Assert.Equal((byte)BusinessMessageRejectEncoder.MaxTextLength, trailer[1]);
    }
}
