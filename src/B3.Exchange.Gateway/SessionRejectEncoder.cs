using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

/// <summary>
/// Byte-level encoder for the B3 EntryPoint <c>Terminate</c> message
/// (templateId=7, BlockLength=13). Used as the protocol-level
/// "SessionReject" — i.e. the sender announces it is about to disconnect
/// the TCP connection because of a framing / decoding error or any other
/// session-level fault from which recovery is impossible.
///
/// Layout (V0):
///   header(8) | sessionID(uint32 @0) | sessionVerID(uint64 @4)
///           | terminationCode(uint8 @12)
/// Total wire size = 8 + 13 = 21 bytes.
/// Field offsets are pinned to the V6 schema generated under
/// <c>B3.Entrypoint.Fixp.Sbe.V6.TerminateData</c>.
/// </summary>
internal static class SessionRejectEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int TerminateBlock = 13;
    public const int TerminateTotal = HeaderSize + TerminateBlock;

    /// <summary>
    /// TerminationCode wire values (subset, see schema enum
    /// <c>TerminationCode</c>). All values are uint8.
    /// </summary>
    public static class TerminationCode
    {
        public const byte Unspecified = 0;
        /// <summary>Peer sent an application message before completing
        /// FIXP <c>Negotiate</c>. Spec §4.5 strict-gating rule.</summary>
        public const byte Unnegotiated = 2;
        /// <summary>Peer sent an application message after Negotiate but
        /// before <c>Establish</c>. Spec §4.5 strict-gating rule.</summary>
        public const byte NotEstablished = 3;
        public const byte UnrecognizedMessage = 15;
        public const byte InvalidSofh = 16;
        public const byte DecodingError = 17;
    }

    public static int EncodeTerminate(Span<byte> dst, uint sessionId, ulong sessionVerId, byte terminationCode)
    {
        if (dst.Length < TerminateTotal)
            throw new ArgumentException("buffer too small for Terminate", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)TerminateTotal,
            blockLength: TerminateBlock, EntryPointFrameReader.TidTerminate, version: 0);
        var body = dst.Slice(HeaderSize, TerminateBlock);
        body.Clear();
        MemoryMarshal.Write(body.Slice(0, 4), in sessionId);
        MemoryMarshal.Write(body.Slice(4, 8), in sessionVerId);
        body[12] = terminationCode;
        return TerminateTotal;
    }
}
