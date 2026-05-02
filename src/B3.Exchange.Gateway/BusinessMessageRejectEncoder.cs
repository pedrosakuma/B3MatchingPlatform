using System.Runtime.InteropServices;
using System.Text;

namespace B3.Exchange.Gateway;

/// <summary>
/// Byte-level encoder for the B3 EntryPoint <c>BusinessMessageReject</c>
/// message (templateId=206, BlockLength=36, plus two trailing varData
/// segments — <c>memo</c> and <c>text</c>). Used to reject a well-framed
/// inbound message that fails business-level validation (unknown SecurityId,
/// invalid side, unknown orderId, etc.) without dropping the TCP session.
///
/// Layout (V0):
///   header(8) | OutboundBusinessHeader(18 @0)
///           | refMsgType(uint8 @18)
///           | refSeqNum(uint32 @20)
///           | businessRejectRefID(uint64 nullable @24, null=0)
///           | businessRejectReason(uint32 @32)
///           | memo varData (uint8 length + bytes)
///           | text varData (uint8 length + bytes)
/// Field offsets are pinned to the V6 schema generated under
/// <c>B3.Entrypoint.Fixp.Sbe.V6.BusinessMessageRejectData</c>.
/// </summary>
internal static class BusinessMessageRejectEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    private const int BusinessHeaderSize = 18;
    public const int BusinessRejectBlock = 36;
    private const ulong UTCTimestampNullValue = ulong.MaxValue;

    /// <summary>
    /// Maximum text varData length (schema cap). The memo segment is always
    /// emitted empty (we never echo a client-provided memo today).
    /// </summary>
    public const int MaxTextLength = 250;

    /// <summary>
    /// Maximum total wire size when <paramref name="textLen"/> bytes of
    /// text varData are present (memo is always 0 bytes).
    /// </summary>
    public static int TotalSize(int textLen)
        => HeaderSize + BusinessRejectBlock + 1 /*memo len*/ + 1 /*text len*/ + textLen;

    /// <summary>
    /// <c>RefMsgType</c> wire value derived from the inbound
    /// <see cref="EntryPointFrameReader"/> templateId. Returns the
    /// schema MessageType enum byte, or 0 if the template is not known
    /// (caller should typically have rejected the frame before this point).
    /// </summary>
    public static byte MapRefMsgTypeFromTemplateId(ushort templateId) => templateId switch
    {
        EntryPointFrameReader.TidSimpleNewOrder => 15,    // MessageType.SimpleNewOrder
        EntryPointFrameReader.TidSimpleModifyOrder => 16, // MessageType.SimpleModifyOrder
        EntryPointFrameReader.TidNewOrderSingle => 17,    // MessageType.NewOrderSingle
        EntryPointFrameReader.TidOrderCancelReplaceRequest => 18, // MessageType.OrderCancelReplaceRequest
        EntryPointFrameReader.TidOrderCancelRequest => 19,// MessageType.OrderCancelRequest
        EntryPointFrameReader.TidNewOrderCross => 20,     // MessageType.NewOrderCross
        _ => 0,
    };

    /// <summary>
    /// Common <c>BusinessRejectReason</c> codes used by this server. Values
    /// are application-defined under the schema's free <c>RejReason</c>
    /// (uint32) type.
    /// </summary>
    public static class Reason
    {
        public const uint Other = 0;
        public const uint UnknownSecurity = 2;
        public const uint UnsupportedMessageType = 3;
        public const uint InvalidField = 5;
        public const uint UnknownOrderId = 6;
    }

    public static int EncodeBusinessMessageReject(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        byte refMsgType, uint refSeqNum, ulong businessRejectRefId, uint businessRejectReason,
        ReadOnlySpan<byte> textAscii)
    {
        if (textAscii.Length > MaxTextLength)
            throw new ArgumentException($"text exceeds {MaxTextLength} bytes", nameof(textAscii));

        int total = TotalSize(textAscii.Length);
        if (dst.Length < total)
            throw new ArgumentException("buffer too small for BusinessMessageReject", nameof(dst));

        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: BusinessRejectBlock,
            EntryPointFrameReader.TidBusinessMessageReject, version: 0);

        var body = dst.Slice(HeaderSize, BusinessRejectBlock);
        body.Clear();
        // OutboundBusinessHeader
        MemoryMarshal.Write(body.Slice(0, 4), in sessionId);
        MemoryMarshal.Write(body.Slice(4, 4), in msgSeqNum);
        MemoryMarshal.Write(body.Slice(8, 8), in sendingTimeNanos);
        body[16] = 0;                                                       // EventIndicator
        body[17] = 255;                                                     // MarketSegmentID null
        body[18] = refMsgType;                                              // RefMsgType
        // body[19] padding
        MemoryMarshal.Write(body.Slice(20, 4), in refSeqNum);
        MemoryMarshal.Write(body.Slice(24, 8), in businessRejectRefId);     // 0 == null
        MemoryMarshal.Write(body.Slice(32, 4), in businessRejectReason);

        // varData: memo (always empty) + text
        var trailer = dst.Slice(HeaderSize + BusinessRejectBlock, total - HeaderSize - BusinessRejectBlock);
        trailer[0] = 0;                                                     // memo length
        trailer[1] = (byte)textAscii.Length;                                // text length
        if (textAscii.Length > 0) textAscii.CopyTo(trailer.Slice(2));
        return total;
    }

    /// <summary>
    /// String overload that ASCII-encodes <paramref name="text"/> (truncating
    /// to <see cref="MaxTextLength"/> if necessary so callers can pass raw
    /// diagnostic strings without pre-validating length).
    /// </summary>
    public static int EncodeBusinessMessageRejectWithText(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        byte refMsgType, uint refSeqNum, ulong businessRejectRefId, uint businessRejectReason,
        string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EncodeBusinessMessageReject(dst, sessionId, msgSeqNum, sendingTimeNanos,
                refMsgType, refSeqNum, businessRejectRefId, businessRejectReason, ReadOnlySpan<byte>.Empty);
        }
        var truncated = text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) : text;
        Span<byte> tmp = stackalloc byte[MaxTextLength];
        int n = Encoding.ASCII.GetBytes(truncated, tmp);
        return EncodeBusinessMessageReject(dst, sessionId, msgSeqNum, sendingTimeNanos,
            refMsgType, refSeqNum, businessRejectRefId, businessRejectReason, tmp.Slice(0, n));
    }
}
