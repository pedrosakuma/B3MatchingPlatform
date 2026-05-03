using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class EntryPointFrameReaderTests
{
    private const int Wire = EntryPointFrameReader.WireHeaderSize;

    [Fact]
    public void TryParse_AcceptsValidSimpleNewOrderHeader()
    {
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 82),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        var ok = EntryPointFrameReader.TryParseInboundHeader(buf, out var info, out var err);

        Assert.True(ok, err);
        Assert.Equal(EntryPointFrameReader.TidSimpleNewOrder, info.TemplateId);
        Assert.Equal(82, info.BodyLength);
        Assert.Equal(82, info.BlockLength);
        Assert.Equal(0, info.VarDataLength);
        Assert.Equal(Wire + 82, info.MessageLength);
    }

    [Fact]
    public void TryParse_AcceptsTrailingVarDataLength()
    {
        // GAP-02: messageLength may exceed WireHeader + BlockLength to carry varData.
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 82 + 5),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        Assert.True(EntryPointFrameReader.TryParseInboundHeader(buf, out var info, out _));
        Assert.Equal(82, info.BlockLength);
        Assert.Equal(82 + 5, info.BodyLength);
        Assert.Equal(5, info.VarDataLength);
    }

    [Fact]
    public void TryParse_RejectsMismatchedBlockLength()
    {
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 81),
            blockLength: 81, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsUnknownTemplate()
    {
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 82),
            blockLength: 82, templateId: 9999, version: 2);
        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsWrongSchema()
    {
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 82),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        // Patch SchemaId (offset = SOFH(4) + 4) to wrong value.
        ushort badSchema = 999;
        MemoryMarshal.Write(buf.Slice(EntryPointFrameReader.SofhSize + 4, 2), in badSchema);
        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsBadEncodingType()
    {
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 82),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        // Corrupt encodingType.
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(2, 2), 0x1234);

        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out var err, out var msg));
        Assert.Equal(EntryPointFrameReader.HeaderError.InvalidSofhEncodingType, err);
        Assert.NotNull(msg);
    }

    [Fact]
    public void TryParse_RejectsMessageLengthOverCap()
    {
        Span<byte> buf = stackalloc byte[Wire];
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(EntryPointFrameReader.MaxInboundMessageLength + 1),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out var err, out _));
        Assert.Equal(EntryPointFrameReader.HeaderError.InvalidSofhMessageLength, err);
    }

    [Fact]
    public void TryParse_RejectsMessageLengthBelowMinimum()
    {
        Span<byte> buf = stackalloc byte[Wire];
        // Claim one byte LESS than header+block (under GAP-02 we accept >=).
        EntryPointFrameReader.WriteHeader(buf,
            messageLength: (ushort)(Wire + 82 - 1),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out var err, out _));
        Assert.Equal(EntryPointFrameReader.HeaderError.MessageLengthMismatch, err);
    }

    [Fact]
    public void WriteHeader_WritesSofhAndSbeBytes()
    {
        Span<byte> buf = stackalloc byte[Wire];
        int n = EntryPointFrameReader.WriteHeader(buf,
            messageLength: 0x1234,
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        Assert.Equal(Wire, n);
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(0, 2)));
        Assert.Equal(EntryPointFrameReader.SofhEncodingType, BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(2, 2)));
        Assert.Equal((ushort)82, BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(4, 2)));
        Assert.Equal(EntryPointFrameReader.TidSimpleNewOrder, BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(6, 2)));
        Assert.Equal(EntryPointFrameReader.SchemaId, BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(8, 2)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(10, 2)));
    }

    [Fact]
    public void WriteHeader_RoundTripsThroughTryParse()
    {
        // Acceptance #4: bytes written by WriteHeader must be parsed back to identical fields.
        Span<byte> buf = stackalloc byte[Wire];
        ushort msgLen = (ushort)(Wire + 82);
        int n = EntryPointFrameReader.WriteHeader(buf,
            messageLength: msgLen,
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        Assert.Equal(Wire, n);
        Assert.True(EntryPointFrameReader.TryParseInboundHeader(buf, out var info, out var err));
        Assert.Null(err);
        Assert.Equal(msgLen, info.MessageLength);
        Assert.Equal(82, info.BodyLength);
        Assert.Equal(EntryPointFrameReader.TidSimpleNewOrder, info.TemplateId);
        Assert.Equal((ushort)2, info.Version);
    }
}
