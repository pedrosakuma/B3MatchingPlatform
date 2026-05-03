using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway.Tests;

public class NegotiateHandshakeEncodersTests
{
    [Fact]
    public void Response_layout_matches_spec()
    {
        var buf = new byte[NegotiateResponseEncoder.Total];
        int n = NegotiateResponseEncoder.Encode(buf, sessionId: 10101, sessionVerId: 7,
            requestTimestampNanos: 0x1122334455667788UL, enteringFirm: 42,
            semVerMajor: 8, semVerMinor: 4, semVerPatch: 2, semVerBuild: 0);
        Assert.Equal(NegotiateResponseEncoder.Total, n);

        // SOFH (4) + SBE header (8): messageLength @0, encodingType @2,
        // blockLength @4, templateId @6, schemaId @8, version @10.
        Assert.Equal(NegotiateResponseEncoder.Total, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2)));
        Assert.Equal(EntryPointFrameReader.SofhEncodingType, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2, 2)));
        Assert.Equal(NegotiateResponseEncoder.Block, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(4, 2)));
        Assert.Equal(NegotiateResponseEncoder.TemplateId, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6, 2)));
        Assert.Equal(EntryPointFrameReader.SchemaId, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(8, 2)));
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(10, 2)));

        // Body @12: sessionID(4) | sessionVerID(8) | requestTimestamp(8)
        //         | enteringFirm(4) | semVer(maj,min,patch,build)
        var body = buf.AsSpan(12);
        Assert.Equal(10101u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        Assert.Equal(7UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)));
        Assert.Equal(0x1122334455667788UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)));
        Assert.Equal(8, body[24]);
        Assert.Equal(4, body[25]);
        Assert.Equal(2, body[26]);
        Assert.Equal(0, body[27]);
    }

    [Fact]
    public void Reject_layout_matches_spec_with_optional_fields()
    {
        var buf = new byte[NegotiateRejectEncoder.Total];
        int n = NegotiateRejectEncoder.Encode(buf, sessionId: 10101, sessionVerId: 3,
            requestTimestampNanos: 0xDEADBEEFCAFEBABEUL, enteringFirm: 42,
            FixpSbe.NegotiationRejectCode.CREDENTIALS, currentSessionVerId: 99);
        Assert.Equal(NegotiateRejectEncoder.Total, n);

        Assert.Equal(NegotiateRejectEncoder.TemplateId, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(10, 2))); // version 0

        var body = buf.AsSpan(12);
        Assert.Equal(10101u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)));
        Assert.Equal(0xDEADBEEFCAFEBABEUL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)));
        Assert.Equal((byte)FixpSbe.NegotiationRejectCode.CREDENTIALS, body[24]);
        Assert.Equal(99UL, BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(28, 8)));
    }

    [Fact]
    public void Reject_writes_null_sentinels_when_optionals_omitted()
    {
        var buf = new byte[NegotiateRejectEncoder.Total];
        NegotiateRejectEncoder.Encode(buf, sessionId: 10101, sessionVerId: 3,
            requestTimestampNanos: 0,
            enteringFirm: null,
            FixpSbe.NegotiationRejectCode.UNSPECIFIED,
            currentSessionVerId: null);
        var body = buf.AsSpan(12);
        Assert.Equal(FixpSbe.NegotiateRejectData.EnteringFirmNullValue,
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)));
        Assert.Equal(FixpSbe.NegotiateRejectData.CurrentSessionVerIDNullValue,
            BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(28, 8)));
    }

    [Fact]
    public void Throws_when_buffer_too_small()
    {
        Assert.Throws<ArgumentException>(() =>
            NegotiateResponseEncoder.Encode(new byte[NegotiateResponseEncoder.Total - 1],
                1, 1, 0, 1, 1, 1, 1));
        Assert.Throws<ArgumentException>(() =>
            NegotiateRejectEncoder.Encode(new byte[NegotiateRejectEncoder.Total - 1],
                1, 1, 0, null, FixpSbe.NegotiationRejectCode.UNSPECIFIED, null));
    }
}
