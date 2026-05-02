using System.Runtime.InteropServices;
using System.Text;

namespace B3.Exchange.Gateway;

/// <summary>
/// Byte-level encoder for the EntryPoint <c>OrderMassActionReport</c>
/// (templateId=702, schema V6). Used to acknowledge — or reject — an
/// inbound <c>OrderMassActionRequest</c> (template 701, spec §4.8 /
/// #GAP-19) on the same TCP session, ahead of the per-order
/// <c>ExecutionReport_Cancel</c> messages that the engine emits for the
/// matching resting orders.
///
/// Layout (V6, BlockLength=70):
///   header(12) | OutboundBusinessHeader(18 @0)
///            | MassActionType(uint8 @18)
///            | MassActionScope(uint8 nullable @19, null=255)
///            | ClOrdID(ulong @20)
///            | MassActionReportID(ulong @28)
///            | TransactTime(ulong @36)
///            | MassActionResponse(uint8 @44)
///            | MassActionRejectReason(uint8 nullable @45, null=255)
///            | ExecRestatementReason(uint8 nullable @46, null=255)
///            | OrdTagID(uint8 nullable @47, null=0)
///            | Side(char nullable @48, null=0)
///            | (pad @49)
///            | Asset(6 bytes @50, all-zero == null)
///            | SecurityID(ulong nullable @56, null=0)
///            | SecurityExchange(4 bytes constant "BVMF" @64) — overlaps
///              InvestorID@64 (V2 Prefix(2)@64 + Document(4)@66)
///   varData: text (uint8 length + bytes)
/// </summary>
internal static class OrderMassActionReportEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int BlockLength = 70;

    /// <summary>Maximum text varData length (schema cap).</summary>
    public const int MaxTextLength = 250;

    public const byte MassActionTypeCancelOrders = 3;
    public const byte MassActionResponseRejected = (byte)'0';
    public const byte MassActionResponseAccepted = (byte)'1';
    public const byte RejectReasonMassActionNotSupported = 0;
    public const byte RejectReasonInvalidOrUnknownMarketSegment = 8;
    public const byte RejectReasonOther = 99;

    /// <summary>Total wire size with <paramref name="textLen"/> bytes of text varData.</summary>
    public static int TotalSize(int textLen) => HeaderSize + BlockLength + 1 /*text len*/ + textLen;

    public static int EncodeOrderMassActionReport(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        ulong clOrdIdValue, ulong massActionReportId, ulong transactTimeNanos,
        byte massActionResponse, byte? massActionRejectReason,
        byte? side, long securityId,
        ReadOnlySpan<byte> textAscii)
    {
        if (textAscii.Length > MaxTextLength)
            throw new ArgumentException($"text exceeds {MaxTextLength} bytes", nameof(textAscii));

        int total = TotalSize(textAscii.Length);
        if (dst.Length < total)
            throw new ArgumentException("buffer too small for OrderMassActionReport", nameof(dst));

        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)total,
            blockLength: BlockLength,
            EntryPointFrameReader.TidOrderMassActionReport, version: 6);

        var body = dst.Slice(HeaderSize, BlockLength);
        body.Clear();

        // OutboundBusinessHeader
        MemoryMarshal.Write(body.Slice(0, 4), in sessionId);
        MemoryMarshal.Write(body.Slice(4, 4), in msgSeqNum);
        MemoryMarshal.Write(body.Slice(8, 8), in sendingTimeNanos);
        body[16] = 0;     // EventIndicator
        body[17] = 255;   // MarketSegmentID null

        body[18] = MassActionTypeCancelOrders;
        body[19] = 255;   // MassActionScope null
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdIdValue);
        MemoryMarshal.Write(body.Slice(28, 8), in massActionReportId);
        MemoryMarshal.Write(body.Slice(36, 8), in transactTimeNanos);
        body[44] = massActionResponse;
        body[45] = massActionRejectReason ?? (byte)255;
        body[46] = 255;   // ExecRestatementReason null
        body[47] = 0;     // OrdTagID null
        body[48] = side ?? (byte)0;
        // body[49] padding
        // body[50..56] Asset null (zeros)
        ulong securityIdRaw = securityId == 0 ? 0UL : (ulong)securityId;
        MemoryMarshal.Write(body.Slice(56, 8), in securityIdRaw);
        // SecurityExchange "BVMF" at offset 64 (V1 base) — overlaps
        // InvestorID@64 in V2; we always write the constant.
        body[64] = (byte)'B';
        body[65] = (byte)'V';
        body[66] = (byte)'M';
        body[67] = (byte)'F';
        // body[68..70] padding (overlaps remainder of InvestorID.Document)

        // varData: text
        var trailer = dst.Slice(HeaderSize + BlockLength, total - HeaderSize - BlockLength);
        trailer[0] = (byte)textAscii.Length;
        if (textAscii.Length > 0) textAscii.CopyTo(trailer.Slice(1));
        return total;
    }

    public static int EncodeOrderMassActionReportWithText(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        ulong clOrdIdValue, ulong massActionReportId, ulong transactTimeNanos,
        byte massActionResponse, byte? massActionRejectReason,
        byte? side, long securityId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EncodeOrderMassActionReport(dst, sessionId, msgSeqNum, sendingTimeNanos,
                clOrdIdValue, massActionReportId, transactTimeNanos,
                massActionResponse, massActionRejectReason, side, securityId, ReadOnlySpan<byte>.Empty);
        }
        Span<byte> tmp = stackalloc byte[MaxTextLength];
        int len = Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(text.Length, MaxTextLength)), tmp);
        return EncodeOrderMassActionReport(dst, sessionId, msgSeqNum, sendingTimeNanos,
            clOrdIdValue, massActionReportId, transactTimeNanos,
            massActionResponse, massActionRejectReason, side, securityId, tmp.Slice(0, len));
    }
}
