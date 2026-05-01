using System.Buffers.Binary;
using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway;

/// <summary>
/// Wire-format constants and parsers for the B3 EntryPoint protocol.
///
/// Per spec §4.4, every TCP frame starts with a 12-byte composite header:
///   • 4-byte SOFH (Simple Open Framing Header):
///       <c>messageLength</c> (uint16 LE) — total frame length including SOFH itself
///       <c>encodingType</c> (uint16 LE)  — must be <see cref="SofhEncodingType"/> (0xEB50)
///   • 8-byte SBE <see cref="MessageHeader"/>: BlockLength / TemplateId / SchemaId / Version.
/// The SBE body (and any trailing variable-length data) follows immediately after.
///
/// For the templates this gateway currently understands (no varData yet), the
/// frame size is exactly <c>SOFH(4) + SBE(8) + BlockLength</c>; once GAP-02
/// lands, additional length-prefixed varData segments follow the fixed block
/// and SOFH <c>messageLength</c> is the only authoritative source of total
/// frame length.
/// </summary>
public static class EntryPointFrameReader
{
    public const ushort SchemaId = 1;

    /// <summary>SOFH framing header size in bytes (uint16 length + uint16 encoding type).</summary>
    public const int SofhSize = 4;

    /// <summary>SBE message header size in bytes.</summary>
    public const int SbeHeaderSize = 8;

    /// <summary>Combined wire-header size: SOFH (4) + SBE header (8).</summary>
    public const int WireHeaderSize = SofhSize + SbeHeaderSize;

    /// <summary>SOFH <c>encodingType</c> for SBE 1.0 little-endian (per spec §4.4).</summary>
    public const ushort SofhEncodingType = 0xEB50;

    /// <summary>Spec §4.4 cap on inbound <c>messageLength</c> (bytes). Frames
    /// claiming a larger length are rejected with <c>INVALID_SOFH</c>.</summary>
    public const int MaxInboundMessageLength = 512;

    /// <summary>Outbound (session-level): Terminate (V0). Used as a
    /// "SessionReject" — see <see cref="SessionRejectEncoder"/>.</summary>
    public const ushort TidTerminate = 7;

    /// <summary>Outbound (business-level): BusinessMessageReject (V0). See
    /// <see cref="BusinessMessageRejectEncoder"/>.</summary>
    public const ushort TidBusinessMessageReject = 206;

    /// <summary>Inbound: SimpleNewOrder (V2).</summary>
    public const ushort TidSimpleNewOrder = 100;
    /// <summary>Inbound: SimpleModifyOrder (V2).</summary>
    public const ushort TidSimpleModifyOrder = 101;
    /// <summary>Inbound: OrderCancelRequest (V6 base, no version variants).</summary>
    public const ushort TidOrderCancelRequest = 105;
    /// <summary>Session: Sequence (FIXP, also used as heartbeat). Bidirectional.</summary>
    public const ushort TidSequence = 9;
    /// <summary>Session: Negotiate (FIXP, inbound only). See spec §4.5.2.</summary>
    public const ushort TidNegotiate = 1;
    /// <summary>Session: Establish (FIXP, inbound only). See spec §4.5.3.</summary>
    public const ushort TidEstablish = 4;
    /// <summary>Session: RetransmitRequest (FIXP, inbound). See spec §4.5.6.</summary>
    public const ushort TidRetransmitRequest = 12;
    /// <summary>Session: Retransmission (FIXP, outbound). See spec §4.5.6.</summary>
    public const ushort TidRetransmission = 13;
    /// <summary>Session: RetransmitReject (FIXP, outbound). See spec §4.5.6.</summary>
    public const ushort TidRetransmitReject = 14;
    /// <summary>Session: NotApplied (FIXP, outbound). See spec §4.5.5 / §4.6.2.</summary>
    public const ushort TidNotApplied = 8;
    /// <summary>Outbound only: NegotiateResponse (template id 2).</summary>
    public const ushort TidNegotiateResponse = 2;
    /// <summary>Outbound only: NegotiateReject (template id 3).</summary>
    public const ushort TidNegotiateReject = 3;
    /// <summary>Outbound only: EstablishAck (template id 5).</summary>
    public const ushort TidEstablishAck = 5;
    /// <summary>Outbound only: EstablishReject (template id 6).</summary>
    public const ushort TidEstablishReject = 6;

    /// <summary>Outbound: ExecutionReport_New (V2).</summary>
    public const ushort TidExecutionReportNew = 200;
    /// <summary>Outbound: ExecutionReport_Modify (V2).</summary>
    public const ushort TidExecutionReportModify = 201;
    /// <summary>Outbound: ExecutionReport_Cancel (V2).</summary>
    public const ushort TidExecutionReportCancel = 202;
    /// <summary>Outbound: ExecutionReport_Trade (V2).</summary>
    public const ushort TidExecutionReportTrade = 203;
    /// <summary>Outbound: ExecutionReport_Reject (V2).</summary>
    public const ushort TidExecutionReportReject = 204;

    /// <summary>BlockLength expected for each supported inbound template.</summary>
    public static int ExpectedInboundBlockLength(ushort templateId, ushort version) => (templateId, version) switch
    {
        (TidSimpleNewOrder, 2) => 82,            // SimpleNewOrderV2.MESSAGE_SIZE
        (TidSimpleModifyOrder, 2) => 98,         // SimpleModifyOrderV2.MESSAGE_SIZE
        (TidOrderCancelRequest, 0) => 76,        // OrderCancelRequest.MESSAGE_SIZE (V6 base)
        (TidSequence, 0) => 4,                   // Sequence: nextSeqNo (uint32). messageType is constant.
        (TidNegotiate, 0) => 28,                 // NegotiateData.BLOCK_LENGTH (V6 schema, root version 0)
        (TidEstablish, 0) => 42,                 // EstablishData.BLOCK_LENGTH
        (TidRetransmitRequest, 0) => 20,         // RetransmitRequestData.BLOCK_LENGTH
        _ => -1
    };

    /// <summary>
    /// Diagnostic error categories surfaced by header parsing. Mapped by
    /// callers to the corresponding <c>TerminationCode</c> on the wire.
    /// </summary>
    public enum HeaderError
    {
        None = 0,
        ShortHeader,
        InvalidSofhEncodingType,
        InvalidSofhMessageLength,   // 0, &lt; 12, or &gt; <see cref="MaxInboundMessageLength"/>
        UnsupportedSchema,
        UnsupportedTemplate,
        BlockLengthMismatch,        // BlockLength != expected for known template
        MessageLengthMismatch,      // SOFH messageLength inconsistent with SBE header + body
    }

    public readonly record struct FrameInfo(
        ushort TemplateId,
        ushort SchemaId,
        ushort Version,
        int BodyLength,
        int MessageLength,
        int BlockLength)
    {
        /// <summary>Bytes of trailing variable-length data after the SBE
        /// fixed root block (= <see cref="BodyLength"/> -
        /// <see cref="BlockLength"/>). Always ≥ 0.</summary>
        public int VarDataLength => BodyLength - BlockLength;
    }

    /// <summary>
    /// Parses the 12-byte composite header (SOFH + SBE) and validates schema /
    /// template / blockLength / messageLength. Returns false on any
    /// well-formedness or compatibility error; <paramref name="error"/>
    /// carries a category caller can map to a <c>TerminationCode</c>.
    /// </summary>
    public static bool TryParseInboundHeader(ReadOnlySpan<byte> header,
        out FrameInfo info, out HeaderError error, out string? message)
    {
        info = default;
        error = HeaderError.None;
        message = null;
        if (header.Length < WireHeaderSize)
        {
            error = HeaderError.ShortHeader;
            message = $"header too short: {header.Length} < {WireHeaderSize}";
            return false;
        }

        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(0, 2));
        ushort encodingType = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(2, 2));

        if (encodingType != SofhEncodingType)
        {
            error = HeaderError.InvalidSofhEncodingType;
            message = $"unexpected SOFH encodingType=0x{encodingType:X4} (expected 0x{SofhEncodingType:X4})";
            return false;
        }

        if (messageLength < WireHeaderSize || messageLength > MaxInboundMessageLength)
        {
            error = HeaderError.InvalidSofhMessageLength;
            message = $"SOFH messageLength={messageLength} out of range [{WireHeaderSize}..{MaxInboundMessageLength}]";
            return false;
        }

        if (!MessageHeader.TryReadHeader(header.Slice(SofhSize, SbeHeaderSize),
                out var blockLength, out var templateId, out var schemaId, out var version))
        {
            error = HeaderError.ShortHeader;
            message = "SBE header parse failed";
            return false;
        }

        if (schemaId != SchemaId)
        {
            error = HeaderError.UnsupportedSchema;
            message = $"unexpected SchemaId={schemaId}";
            return false;
        }

        int expected = ExpectedInboundBlockLength(templateId, version);
        if (expected < 0)
        {
            error = HeaderError.UnsupportedTemplate;
            message = $"unsupported template={templateId} version={version}";
            return false;
        }

        if (blockLength != expected)
        {
            error = HeaderError.BlockLengthMismatch;
            message = $"BlockLength={blockLength} mismatch (expected {expected}) for template={templateId}";
            return false;
        }

        // Per spec §3.5, application messages may carry length-prefixed
        // variable-length data segments after the SBE fixed root block.
        // SOFH messageLength is the source of truth for the total frame
        // size, so we accept anything ≥ WireHeaderSize + BlockLength and
        // hand the trailing bytes to varData decoding (GAP-02).
        int minMessageLength = WireHeaderSize + blockLength;
        if (messageLength < minMessageLength)
        {
            error = HeaderError.MessageLengthMismatch;
            message = $"SOFH messageLength={messageLength} < WireHeader({WireHeaderSize})+BlockLength({blockLength})={minMessageLength}";
            return false;
        }

        info = new FrameInfo(templateId, schemaId, version,
            BodyLength: messageLength - WireHeaderSize,
            MessageLength: messageLength,
            BlockLength: blockLength);
        return true;
    }

    /// <summary>
    /// Backwards-compatible parsing overload that hides the structured
    /// <see cref="HeaderError"/> and surfaces only a free-form message
    /// (matches the pre-SOFH signature; useful for callers that do not
    /// yet route diagnostics back through the FIXP <c>Terminate</c> path).
    /// </summary>
    public static bool TryParseInboundHeader(ReadOnlySpan<byte> header, out FrameInfo info, out string? error)
    {
        bool ok = TryParseInboundHeader(header, out info, out _, out var msg);
        error = msg;
        return ok;
    }

    /// <summary>
    /// Writes the full 12-byte composite header (SOFH + SBE) into
    /// <paramref name="dst"/> and returns <see cref="WireHeaderSize"/>.
    /// <paramref name="messageLength"/> is the total frame length the SOFH
    /// advertises (header + body + any trailing varData).
    /// </summary>
    public static int WriteHeader(Span<byte> dst, ushort messageLength,
        ushort blockLength, ushort templateId, ushort version)
    {
        if (dst.Length < WireHeaderSize)
            throw new ArgumentException("buffer too small for SOFH+SBE header", nameof(dst));
        if (messageLength < WireHeaderSize)
            throw new ArgumentOutOfRangeException(nameof(messageLength),
                $"messageLength must be >= {WireHeaderSize} (got {messageLength})");

        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(0, 2), messageLength);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(2, 2), SofhEncodingType);

        ref var hdr = ref MemoryMarshal.AsRef<MessageHeader>(dst.Slice(SofhSize, SbeHeaderSize));
        hdr.BlockLength = blockLength;
        hdr.TemplateId = templateId;
        hdr.SchemaId = SchemaId;
        hdr.Version = version;
        return WireHeaderSize;
    }
}
