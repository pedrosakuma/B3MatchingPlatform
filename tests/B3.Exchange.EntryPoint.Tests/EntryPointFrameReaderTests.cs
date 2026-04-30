using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;

namespace B3.Exchange.EntryPoint.Tests;

public class EntryPointFrameReaderTests
{
    [Fact]
    public void TryParse_AcceptsValidSimpleNewOrderHeader()
    {
        Span<byte> buf = stackalloc byte[8];
        EntryPointFrameReader.WriteHeader(buf, blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        var ok = EntryPointFrameReader.TryParseInboundHeader(buf, out var info, out var err);

        Assert.True(ok, err);
        Assert.Equal(EntryPointFrameReader.TidSimpleNewOrder, info.TemplateId);
        Assert.Equal(82, info.BodyLength);
    }

    [Fact]
    public void TryParse_RejectsMismatchedBlockLength()
    {
        Span<byte> buf = stackalloc byte[8];
        EntryPointFrameReader.WriteHeader(buf, blockLength: 81, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsUnknownTemplate()
    {
        Span<byte> buf = stackalloc byte[8];
        EntryPointFrameReader.WriteHeader(buf, blockLength: 82, templateId: 9999, version: 2);
        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsWrongSchema()
    {
        Span<byte> buf = stackalloc byte[8];
        EntryPointFrameReader.WriteHeader(buf, blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        // Patch SchemaId to wrong value
        ushort badSchema = 999;
        MemoryMarshal.Write(buf.Slice(4, 2), in badSchema);
        Assert.False(EntryPointFrameReader.TryParseInboundHeader(buf, out _, out _));
    }
}
