using System.Runtime.InteropServices;

namespace B3.Exchange.EntryPoint;

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
