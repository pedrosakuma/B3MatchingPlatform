using System.Buffers.Binary;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.SyntheticTrader.Fixp;

/// <summary>
/// Byte-level encoders and decoders for the FIXP session-layer frames the
/// synthetic trader's <see cref="FixpClient"/> needs to speak. Pinned to
/// the V6 / version-0 SBE schema as documented in
/// <c>schemas/b3-entrypoint-messages-8.4.2.xml</c>.
///
/// <para>This is the client-side mirror of the gateway's internal
/// encoders (<c>NegotiateHandshakeEncoders</c>, <c>EstablishHandshakeEncoders</c>,
/// <c>SessionFrameEncoder</c>): we encode what the gateway decodes
/// (<c>Negotiate</c>, <c>Establish</c>, <c>Sequence</c>,
/// <c>RetransmitRequest</c>, <c>Terminate</c>) and decode what the
/// gateway encodes (<c>NegotiateResponse</c>, <c>NegotiateReject</c>,
/// <c>EstablishAck</c>, <c>EstablishReject</c>, <c>Sequence</c>,
/// <c>NotApplied</c>, <c>Retransmission</c>, <c>RetransmitReject</c>,
/// <c>Terminate</c>). All values are little-endian; field offsets follow
/// the generated <see cref="FixpSbe.NegotiateData"/> / etc. structs and
/// are commented inline so a future schema bump can be audited cheaply.</para>
/// </summary>
internal static class FixpFrameCodec
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;

    // ----- Block lengths (mirror FixpSbe.*Data.BLOCK_LENGTH) ----------------
    public const int NegotiateBlock = FixpSbe.NegotiateData.BLOCK_LENGTH;             // 28
    public const int EstablishBlock = FixpSbe.EstablishData.BLOCK_LENGTH;             // 42
    public const int SequenceBlock = FixpSbe.SequenceData.BLOCK_LENGTH;               // 4
    public const int RetransmitRequestBlock = FixpSbe.RetransmitRequestData.BLOCK_LENGTH; // 20
    public const int TerminateBlock = FixpSbe.TerminateData.BLOCK_LENGTH;             // 13
    public const int NegotiateResponseBlock = FixpSbe.NegotiateResponseData.BLOCK_LENGTH; // 28
    public const int NegotiateRejectBlock = FixpSbe.NegotiateRejectData.BLOCK_LENGTH; // 36
    public const int EstablishAckBlock = FixpSbe.EstablishAckData.BLOCK_LENGTH;       // 40
    public const int EstablishRejectBlock = FixpSbe.EstablishRejectData.BLOCK_LENGTH; // 26
    public const int NotAppliedBlock = FixpSbe.NotAppliedData.BLOCK_LENGTH;           // 8
    public const int RetransmissionBlock = FixpSbe.RetransmissionData.BLOCK_LENGTH;   // 20
    public const int RetransmitRejectBlock = FixpSbe.RetransmitRejectData.BLOCK_LENGTH;

    // varData maximums per spec §4.5.2.
    public const int MaxCredentialsLength = 128;
    public const int MaxClientIpLength = 30;
    public const int MaxClientAppNameLength = 30;
    public const int MaxClientAppVersionLength = 30;

    // ===== Encoders (client → server) =======================================

    /// <summary>
    /// Encodes a <c>Negotiate</c> frame (templateId=1, version=0, BLOCK_LENGTH=28)
    /// followed by 4 varData segments (credentials, clientIP, clientAppName,
    /// clientAppVersion). Each varData segment is length(uint8) + payload bytes.
    /// Returns the number of bytes written.
    /// </summary>
    public static int EncodeNegotiate(Span<byte> dst, uint sessionId, ulong sessionVerId,
        ulong timestampNanos, uint enteringFirm, uint? onBehalfFirm,
        ReadOnlySpan<byte> credentials, ReadOnlySpan<byte> clientIp,
        ReadOnlySpan<byte> clientAppName, ReadOnlySpan<byte> clientAppVersion)
    {
        if (credentials.Length > MaxCredentialsLength) throw new ArgumentException("credentials too long", nameof(credentials));
        if (clientIp.Length > MaxClientIpLength) throw new ArgumentException("clientIp too long", nameof(clientIp));
        if (clientAppName.Length > MaxClientAppNameLength) throw new ArgumentException("clientAppName too long", nameof(clientAppName));
        if (clientAppVersion.Length > MaxClientAppVersionLength) throw new ArgumentException("clientAppVersion too long", nameof(clientAppVersion));

        int varSize = 4 + credentials.Length + clientIp.Length + clientAppName.Length + clientAppVersion.Length;
        int total = HeaderSize + NegotiateBlock + varSize;
        if (dst.Length < total) throw new ArgumentException("buffer too small for Negotiate", nameof(dst));

        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: NegotiateBlock,
            templateId: EntryPointFrameReader.TidNegotiate, version: 0);
        var body = dst.Slice(HeaderSize, NegotiateBlock);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(12, 8), timestampNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(20, 4), enteringFirm);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(24, 4),
            onBehalfFirm ?? FixpSbe.NegotiateData.OnbehalfFirmNullValue);

        int pos = HeaderSize + NegotiateBlock;
        pos += WriteVarSegment(dst.Slice(pos), credentials);
        pos += WriteVarSegment(dst.Slice(pos), clientIp);
        pos += WriteVarSegment(dst.Slice(pos), clientAppName);
        pos += WriteVarSegment(dst.Slice(pos), clientAppVersion);
        return pos;
    }

    /// <summary>
    /// Encodes an <c>Establish</c> frame (templateId=4, version=0, BLOCK_LENGTH=42)
    /// followed by an optional <c>credentials</c> varData segment (per spec the
    /// segment is required but may be empty). Returns the bytes written.
    /// </summary>
    public static int EncodeEstablish(Span<byte> dst, uint sessionId, ulong sessionVerId,
        ulong timestampNanos, ulong keepAliveIntervalMillis, uint nextSeqNo,
        ushort cancelOnDisconnectType, ulong codTimeoutWindowMillis,
        ReadOnlySpan<byte> credentials)
    {
        if (credentials.Length > MaxCredentialsLength) throw new ArgumentException("credentials too long", nameof(credentials));
        int varSize = 1 + credentials.Length;
        int total = HeaderSize + EstablishBlock + varSize;
        if (dst.Length < total) throw new ArgumentException("buffer too small for Establish", nameof(dst));

        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: EstablishBlock,
            templateId: EntryPointFrameReader.TidEstablish, version: 0);
        var body = dst.Slice(HeaderSize, EstablishBlock);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(12, 8), timestampNanos);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), keepAliveIntervalMillis);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(28, 4), nextSeqNo);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(32, 2), cancelOnDisconnectType);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(34, 8), codTimeoutWindowMillis);

        int pos = HeaderSize + EstablishBlock;
        pos += WriteVarSegment(dst.Slice(pos), credentials);
        return pos;
    }

    /// <summary>
    /// Encodes a <c>Sequence</c> frame (templateId=9, version=0, BLOCK_LENGTH=4).
    /// Doubles as the heartbeat in idempotent flow.
    /// </summary>
    public static int EncodeSequence(Span<byte> dst, uint nextSeqNo)
    {
        int total = HeaderSize + SequenceBlock;
        if (dst.Length < total) throw new ArgumentException("buffer too small for Sequence", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: SequenceBlock,
            templateId: EntryPointFrameReader.TidSequence, version: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(HeaderSize, 4), nextSeqNo);
        return total;
    }

    /// <summary>
    /// Encodes a <c>RetransmitRequest</c> frame (templateId=12, version=0,
    /// BLOCK_LENGTH=20). Sent by the client when it detects a gap in
    /// inbound application sequence numbers.
    /// </summary>
    public static int EncodeRetransmitRequest(Span<byte> dst, uint sessionId,
        ulong timestampNanos, uint fromSeqNo, uint count)
    {
        int total = HeaderSize + RetransmitRequestBlock;
        if (dst.Length < total) throw new ArgumentException("buffer too small for RetransmitRequest", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: RetransmitRequestBlock,
            templateId: EntryPointFrameReader.TidRetransmitRequest, version: 0);
        var body = dst.Slice(HeaderSize, RetransmitRequestBlock);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), timestampNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(12, 4), fromSeqNo);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(16, 4), count);
        return total;
    }

    /// <summary>
    /// Encodes a <c>Terminate</c> frame (templateId=7, version=0, BLOCK_LENGTH=13).
    /// </summary>
    public static int EncodeTerminate(Span<byte> dst, uint sessionId, ulong sessionVerId,
        byte terminationCode)
    {
        int total = HeaderSize + TerminateBlock;
        if (dst.Length < total) throw new ArgumentException("buffer too small for Terminate", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: TerminateBlock,
            templateId: EntryPointFrameReader.TidTerminate, version: 0);
        var body = dst.Slice(HeaderSize, TerminateBlock);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        body[12] = terminationCode;
        return total;
    }

    // ===== Decoders (server → client) =======================================

    public readonly record struct NegotiateResponseFrame(
        uint SessionId, ulong SessionVerId, ulong RequestTimestampNanos,
        uint EnteringFirm, byte SemVerMajor, byte SemVerMinor, byte SemVerPatch);

    public static bool TryDecodeNegotiateResponse(ReadOnlySpan<byte> body, out NegotiateResponseFrame frame)
    {
        if (body.Length < NegotiateResponseBlock) { frame = default; return false; }
        frame = new NegotiateResponseFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            SessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            RequestTimestampNanos: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)),
            EnteringFirm: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)),
            SemVerMajor: body[24], SemVerMinor: body[25], SemVerPatch: body[26]);
        return true;
    }

    public readonly record struct NegotiateRejectFrame(
        uint SessionId, ulong SessionVerId, ulong RequestTimestampNanos,
        uint EnteringFirm, byte RejectCode, ulong CurrentSessionVerId);

    public static bool TryDecodeNegotiateReject(ReadOnlySpan<byte> body, out NegotiateRejectFrame frame)
    {
        if (body.Length < NegotiateRejectBlock) { frame = default; return false; }
        frame = new NegotiateRejectFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            SessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            RequestTimestampNanos: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)),
            EnteringFirm: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)),
            RejectCode: body[24],
            CurrentSessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(28, 8)));
        return true;
    }

    public readonly record struct EstablishAckFrame(
        uint SessionId, ulong SessionVerId, ulong RequestTimestampNanos,
        ulong KeepAliveIntervalMillis, uint NextSeqNo, uint LastIncomingSeqNo,
        byte SemVerMajor, byte SemVerMinor, byte SemVerPatch);

    public static bool TryDecodeEstablishAck(ReadOnlySpan<byte> body, out EstablishAckFrame frame)
    {
        if (body.Length < EstablishAckBlock) { frame = default; return false; }
        frame = new EstablishAckFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            SessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            RequestTimestampNanos: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)),
            KeepAliveIntervalMillis: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(20, 8)),
            NextSeqNo: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(28, 4)),
            LastIncomingSeqNo: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(32, 4)),
            SemVerMajor: body[36], SemVerMinor: body[37], SemVerPatch: body[38]);
        return true;
    }

    public readonly record struct EstablishRejectFrame(
        uint SessionId, ulong SessionVerId, ulong RequestTimestampNanos,
        byte RejectCode, uint LastIncomingSeqNo);

    public static bool TryDecodeEstablishReject(ReadOnlySpan<byte> body, out EstablishRejectFrame frame)
    {
        if (body.Length < EstablishRejectBlock) { frame = default; return false; }
        frame = new EstablishRejectFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            SessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            RequestTimestampNanos: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(12, 8)),
            RejectCode: body[20],
            LastIncomingSeqNo: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(22, 4)));
        return true;
    }

    public readonly record struct SequenceFrame(uint NextSeqNo);

    public static bool TryDecodeSequence(ReadOnlySpan<byte> body, out SequenceFrame frame)
    {
        if (body.Length < SequenceBlock) { frame = default; return false; }
        frame = new SequenceFrame(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)));
        return true;
    }

    public readonly record struct NotAppliedFrame(uint FromSeqNo, uint Count);

    public static bool TryDecodeNotApplied(ReadOnlySpan<byte> body, out NotAppliedFrame frame)
    {
        if (body.Length < NotAppliedBlock) { frame = default; return false; }
        frame = new NotAppliedFrame(
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)));
        return true;
    }

    public readonly record struct RetransmissionFrame(
        uint SessionId, ulong RequestTimestampNanos, uint NextSeqNo, uint Count);

    public static bool TryDecodeRetransmission(ReadOnlySpan<byte> body, out RetransmissionFrame frame)
    {
        if (body.Length < RetransmissionBlock) { frame = default; return false; }
        frame = new RetransmissionFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            RequestTimestampNanos: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            NextSeqNo: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12, 4)),
            Count: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16, 4)));
        return true;
    }

    public readonly record struct TerminateFrame(uint SessionId, ulong SessionVerId, byte TerminationCode);

    public static bool TryDecodeTerminate(ReadOnlySpan<byte> body, out TerminateFrame frame)
    {
        if (body.Length < TerminateBlock) { frame = default; return false; }
        frame = new TerminateFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            SessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            TerminationCode: body[12]);
        return true;
    }

    public readonly record struct RetransmitRejectFrame(
        uint SessionId, ulong RequestTimestampNanos, byte RejectCode);

    public static bool TryDecodeRetransmitReject(ReadOnlySpan<byte> body, out RetransmitRejectFrame frame)
    {
        if (body.Length < RetransmitRejectBlock) { frame = default; return false; }
        // RetransmitReject: sessionId(4) + requestTimestamp(8) + rejectCode(1) + (padding)
        frame = new RetransmitRejectFrame(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            RequestTimestampNanos: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(4, 8)),
            RejectCode: body[12]);
        return true;
    }

    // ===== varData helpers ==================================================

    private static int WriteVarSegment(Span<byte> dst, ReadOnlySpan<byte> payload)
    {
        if (dst.Length < 1 + payload.Length) throw new ArgumentException("buffer too small for var segment");
        dst[0] = (byte)payload.Length;
        payload.CopyTo(dst.Slice(1, payload.Length));
        return 1 + payload.Length;
    }
}
