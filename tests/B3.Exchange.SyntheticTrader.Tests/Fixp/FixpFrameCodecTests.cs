using System.Buffers.Binary;
using B3.Exchange.Gateway;
using B3.Exchange.SyntheticTrader.Fixp;

namespace B3.Exchange.SyntheticTrader.Tests.Fixp;

/// <summary>
/// Round-trip sanity checks for <see cref="FixpFrameCodec"/>: every
/// encoder lays down bytes that the matching decoder (or, where the
/// codec only ships an encoder, the gateway's own decoder) reads back
/// to the same logical values.
/// </summary>
public class FixpFrameCodecTests
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;

    [Fact]
    public void EncodeNegotiate_WriteHeaderAndBody()
    {
        var buf = new byte[256];
        int len = FixpFrameCodec.EncodeNegotiate(buf,
            sessionId: 42, sessionVerId: 12345, timestampNanos: 999_888_777,
            enteringFirm: 7, onBehalfFirm: null,
            credentials: new byte[] { 1, 2, 3 },
            clientIp: System.Text.Encoding.ASCII.GetBytes("127.0.0.1"),
            clientAppName: System.Text.Encoding.ASCII.GetBytes("synth"),
            clientAppVersion: System.Text.Encoding.ASCII.GetBytes("1.0"));

        Assert.True(len > HeaderSize + FixpFrameCodec.NegotiateBlock);
        // SOFH messageLength
        Assert.Equal((ushort)len, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2)));
        // SBE templateId is at SOFH+SbeHeader: blockLength(2)+templateId(2)
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        Assert.Equal(EntryPointFrameReader.TidNegotiate, tid);
        // sessionId @ HeaderSize+0
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(HeaderSize + 0, 4)));
        // enteringFirm @ HeaderSize+20
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(HeaderSize + 20, 4)));
    }

    [Fact]
    public void EncodeEstablish_WriteHeaderAndBody()
    {
        var buf = new byte[128];
        int len = FixpFrameCodec.EncodeEstablish(buf,
            sessionId: 42, sessionVerId: 12345, timestampNanos: 999,
            keepAliveIntervalMillis: 5000, nextSeqNo: 1,
            cancelOnDisconnectType: 4, codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        Assert.Equal(HeaderSize + FixpFrameCodec.EstablishBlock + 1, len); // varData length byte = 0
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        Assert.Equal(EntryPointFrameReader.TidEstablish, tid);
        Assert.Equal(5000ul, BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(HeaderSize + 20, 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(HeaderSize + 28, 4)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(HeaderSize + 32, 2)));
    }

    [Fact]
    public void EncodeSequence_RoundTrip()
    {
        var buf = new byte[32];
        int len = FixpFrameCodec.EncodeSequence(buf, nextSeqNo: 17);
        Assert.Equal(HeaderSize + FixpFrameCodec.SequenceBlock, len);
        Assert.True(FixpFrameCodec.TryDecodeSequence(buf.AsSpan(HeaderSize, FixpFrameCodec.SequenceBlock), out var seq));
        Assert.Equal(17u, seq.NextSeqNo);
    }

    [Fact]
    public void EncodeRetransmitRequest_RoundTrip()
    {
        var buf = new byte[64];
        int len = FixpFrameCodec.EncodeRetransmitRequest(buf, sessionId: 9,
            timestampNanos: 0xdeadbeef, fromSeqNo: 100, count: 5);
        Assert.Equal(HeaderSize + FixpFrameCodec.RetransmitRequestBlock, len);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(HeaderSize + 0, 4)));
        Assert.Equal(0xdeadbeefUL, BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(HeaderSize + 4, 8)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(HeaderSize + 12, 4)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(HeaderSize + 16, 4)));
    }

    [Fact]
    public void EncodeTerminate_RoundTrip()
    {
        var buf = new byte[32];
        int len = FixpFrameCodec.EncodeTerminate(buf, sessionId: 11, sessionVerId: 222, terminationCode: 1);
        Assert.Equal(HeaderSize + FixpFrameCodec.TerminateBlock, len);
        Assert.True(FixpFrameCodec.TryDecodeTerminate(buf.AsSpan(HeaderSize, FixpFrameCodec.TerminateBlock), out var tm));
        Assert.Equal(11u, tm.SessionId);
        Assert.Equal(222ul, tm.SessionVerId);
        Assert.Equal((byte)1, tm.TerminationCode);
    }

    [Fact]
    public void DecodeNegotiateResponse_ParsesAllFields()
    {
        // Lay down the body manually: sessionId(4)+sessionVerId(8)+
        // requestTimestamp(8)+enteringFirm(4)+semVer(4)
        var body = new byte[FixpFrameCodec.NegotiateResponseBlock];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 42);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4, 8), 100);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(12, 8), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20, 4), 7);
        body[24] = 6; body[25] = 0; body[26] = 0;

        Assert.True(FixpFrameCodec.TryDecodeNegotiateResponse(body, out var nr));
        Assert.Equal(42u, nr.SessionId);
        Assert.Equal(100ul, nr.SessionVerId);
        Assert.Equal(200ul, nr.RequestTimestampNanos);
        Assert.Equal(7u, nr.EnteringFirm);
        Assert.Equal(6, nr.SemVerMajor);
    }

    [Fact]
    public void DecodeEstablishAck_ParsesAllFields()
    {
        var body = new byte[FixpFrameCodec.EstablishAckBlock];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 42);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4, 8), 100);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(12, 8), 200);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(20, 8), 5000);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32, 4), 0);
        body[36] = 6; body[37] = 0; body[38] = 0;

        Assert.True(FixpFrameCodec.TryDecodeEstablishAck(body, out var ack));
        Assert.Equal(42u, ack.SessionId);
        Assert.Equal(5000ul, ack.KeepAliveIntervalMillis);
        Assert.Equal(1u, ack.NextSeqNo);
    }

    [Fact]
    public void DecodeNotApplied_ParsesGap()
    {
        var body = new byte[FixpFrameCodec.NotAppliedBlock];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 50);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), 3);
        Assert.True(FixpFrameCodec.TryDecodeNotApplied(body, out var na));
        Assert.Equal(50u, na.FromSeqNo);
        Assert.Equal(3u, na.Count);
    }

    [Fact]
    public void DecodeRetransmission_ParsesHeader()
    {
        var body = new byte[FixpFrameCodec.RetransmissionBlock];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 42);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4, 8), 12345);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16, 4), 5);
        Assert.True(FixpFrameCodec.TryDecodeRetransmission(body, out var rt));
        Assert.Equal(42u, rt.SessionId);
        Assert.Equal(100u, rt.NextSeqNo);
        Assert.Equal(5u, rt.Count);
    }

    [Fact]
    public void EncoderRejectsTooShortBuffer()
    {
        var tiny = new byte[4];
        Assert.Throws<ArgumentException>(() => FixpFrameCodec.EncodeSequence(tiny, 0));
        Assert.Throws<ArgumentException>(() => FixpFrameCodec.EncodeTerminate(tiny, 0, 0, 0));
    }

    [Fact]
    public void EncoderRejectsOversizedVarSegments()
    {
        var huge = new byte[FixpFrameCodec.MaxCredentialsLength + 1];
        var buf = new byte[1024];
        Assert.Throws<ArgumentException>(() => FixpFrameCodec.EncodeNegotiate(buf,
            1, 1, 1, 1, null, huge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
    }
}
