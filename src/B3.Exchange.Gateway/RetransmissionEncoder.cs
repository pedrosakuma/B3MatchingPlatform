using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

/// <summary>
/// Byte-level encoders for FIXP retransmission session-layer responses
/// (template ids 13 / 14 — see spec §4.5.6 and EntryPoint schema V6).
///
/// <para>Both messages are session-layer and do NOT consume an outbound
/// business <c>MsgSeqNum</c> (same as <c>Sequence</c>). They share the
/// same SOFH+SBE 12-byte wire header as every other FIXP frame.</para>
/// </summary>
internal static class RetransmissionEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;

    public const int RetransmissionBlock = 20;
    public const int RetransmissionTotal = HeaderSize + RetransmissionBlock;

    public const int RetransmitRejectBlock = 13;
    public const int RetransmitRejectTotal = HeaderSize + RetransmitRejectBlock;

    /// <summary>
    /// Encodes a <c>Retransmission</c> frame announcing the start of a
    /// replay block. Per the SBE schema, <c>nextSeqNo</c> is the
    /// sequence number of the FIRST replayed message (i.e. the
    /// request's <c>fromSeqNo</c>), not the live next seq — the
    /// parameter is named <paramref name="firstRetransmittedSeqNo"/>
    /// to make this hard to misuse.
    /// </summary>
    public static int EncodeRetransmission(Span<byte> dst,
        uint sessionId, ulong requestTimestampNanos,
        uint firstRetransmittedSeqNo, uint count)
    {
        if (dst.Length < RetransmissionTotal)
            throw new ArgumentException("buffer too small for Retransmission", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)RetransmissionTotal,
            blockLength: RetransmissionBlock,
            templateId: EntryPointFrameReader.TidRetransmission, version: 0);
        var body = dst.Slice(HeaderSize, RetransmissionBlock);
        body.Clear();
        MemoryMarshal.Write(body.Slice(0, 4), in sessionId);
        MemoryMarshal.Write(body.Slice(4, 8), in requestTimestampNanos);
        MemoryMarshal.Write(body.Slice(12, 4), in firstRetransmittedSeqNo);
        MemoryMarshal.Write(body.Slice(16, 4), in count);
        return RetransmissionTotal;
    }

    /// <summary>
    /// Encodes a <c>RetransmitReject</c> frame.
    /// </summary>
    public static int EncodeRetransmitReject(Span<byte> dst,
        uint sessionId, ulong requestTimestampNanos,
        B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode code)
    {
        if (dst.Length < RetransmitRejectTotal)
            throw new ArgumentException("buffer too small for RetransmitReject", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)RetransmitRejectTotal,
            blockLength: RetransmitRejectBlock,
            templateId: EntryPointFrameReader.TidRetransmitReject, version: 0);
        var body = dst.Slice(HeaderSize, RetransmitRejectBlock);
        body.Clear();
        MemoryMarshal.Write(body.Slice(0, 4), in sessionId);
        MemoryMarshal.Write(body.Slice(4, 8), in requestTimestampNanos);
        body[12] = (byte)code;
        return RetransmitRejectTotal;
    }
}
