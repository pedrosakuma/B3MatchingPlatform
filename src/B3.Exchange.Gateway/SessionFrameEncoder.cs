using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

/// <summary>
/// Byte-level encoders for FIXP session-layer frames (currently just
/// <c>Sequence</c> / heartbeat). Kept separate from
/// <see cref="ExecutionReportEncoder"/> so the application-layer encoder
/// stays focused on ExecutionReports.
/// </summary>
internal static class SessionFrameEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int SequenceBlock = 4;
    public const int SequenceTotal = HeaderSize + SequenceBlock;

    /// <summary>
    /// NotApplied (templateId=8) carries 8 bytes of body: FromSeqNo
    /// (uint32 @0) + Count (uint32 @4). Per spec §4.5.5 the gateway
    /// emits one of these whenever it detects an inbound MsgSeqNum gap.
    /// NotApplied is a session-layer message and does NOT consume an
    /// outbound MsgSeqNum.
    /// </summary>
    public const int NotAppliedBlock = 8;
    public const int NotAppliedTotal = HeaderSize + NotAppliedBlock;

    /// <summary>
    /// Writes a <c>NotApplied</c> (templateId=8) frame announcing a gap
    /// of <paramref name="count"/> messages starting at
    /// <paramref name="fromSeqNo"/> (the first MsgSeqNum the gateway
    /// EXPECTED but did NOT receive). Per spec §4.5.5 / §4.6.2 this is
    /// idempotent: the gateway accepts the new (higher) message and
    /// advances its expected counter past the gap; the client may then
    /// retransmit, send something else, or do nothing.
    /// </summary>
    public static int EncodeNotApplied(Span<byte> dst, uint fromSeqNo, uint count)
    {
        if (dst.Length < NotAppliedTotal)
            throw new ArgumentException("buffer too small for NotApplied frame", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)NotAppliedTotal,
            blockLength: NotAppliedBlock,
            templateId: EntryPointFrameReader.TidNotApplied, version: 0);
        MemoryMarshal.Write(dst.Slice(HeaderSize, 4), in fromSeqNo);
        MemoryMarshal.Write(dst.Slice(HeaderSize + 4, 4), in count);
        return NotAppliedTotal;
    }

    /// <summary>
    /// Writes a <c>Sequence</c> (templateId=9) frame. In FIXP this message
    /// doubles as the heartbeat: the server emits it when no other outbound
    /// traffic has been sent within the heartbeat interval, and as a probe
    /// after the inbound idle timeout elapses.
    /// </summary>
    public static int EncodeSequence(Span<byte> dst, uint nextSeqNo)
    {
        if (dst.Length < SequenceTotal)
            throw new ArgumentException("buffer too small for Sequence frame", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)SequenceTotal,
            blockLength: SequenceBlock,
            templateId: EntryPointFrameReader.TidSequence, version: 0);
        MemoryMarshal.Write(dst.Slice(HeaderSize, 4), in nextSeqNo);
        return SequenceTotal;
    }
}
