using System.Runtime.InteropServices;
using System.Text;
using B3.Umdf.Mbo.Sbe.V16;

namespace B3.Umdf.WireEncoder;

/// <summary>
/// Stateless byte-level encoders for the B3 UMDF wire protocol the consumer
/// in this repository understands. Methods write into a caller-supplied
/// <see cref="Span{T}"/> and return the number of bytes written.
///
/// Wire layout assumed by every <c>Write*Frame</c> helper (per-message):
/// <code>
/// [FramingHeader 4][SBE MessageHeader 8][SBE body N]
/// </code>
/// Use <see cref="WritePacketHeader"/> once per UDP datagram.
///
/// All encoders write the V16 SBE schema for messages where layout differs
/// across versions (notably DeleteOrder_MBO_51 and Trade_53). For
/// Order_MBO_50, the V6 body is used (56 bytes) since the V16 reader
/// honours the BlockLength field in the SBE MessageHeader.
/// </summary>
public static class UmdfWireEncoder
{
    /// <summary>Sentinel used by <c>PriceOptional</c> when the value is absent.</summary>
    public const long PriceNull = long.MinValue;

    public const byte MdEntryTypeBid = (byte)MDEntryType.BID;
    public const byte MdEntryTypeOffer = (byte)MDEntryType.OFFER;
    public const byte MdEntryTypeTrade = (byte)MDEntryType.TRADE;

    /// <summary>
    /// Writes the 16-byte UMDF PacketHeader at the start of <paramref name="dst"/>.
    /// Returns 16.
    /// </summary>
    public static int WritePacketHeader(
        Span<byte> dst,
        byte channelNumber,
        ushort sequenceVersion,
        uint sequenceNumber,
        ulong sendingTimeNanos)
    {
        if (dst.Length < WireOffsets.PacketHeaderSize)
            ThrowTooSmall(nameof(dst), WireOffsets.PacketHeaderSize);

        ref var hdr = ref MemoryMarshal.AsRef<PacketHeader>(dst.Slice(0, WireOffsets.PacketHeaderSize));
        hdr.ChannelNumber = channelNumber;
        hdr.Reserved = 0;
        hdr.SequenceVersion = sequenceVersion;
        hdr.SequenceNumber = sequenceNumber;
        hdr.SendingTime = sendingTimeNanos;
        return WireOffsets.PacketHeaderSize;
    }

    /// <summary>
    /// Updates only the <c>SequenceNumber</c> + <c>SendingTime</c> fields of
    /// an already-initialised PacketHeader. Useful for hot loops that
    /// rewrite the same buffer.
    /// </summary>
    public static void PatchPacketHeader(Span<byte> dst, uint sequenceNumber, ulong sendingTimeNanos)
    {
        if (dst.Length < WireOffsets.PacketHeaderSize)
            ThrowTooSmall(nameof(dst), WireOffsets.PacketHeaderSize);
        MemoryMarshal.Write(dst.Slice(WireOffsets.PacketHeaderSequenceNumberOffset, 4), in sequenceNumber);
        MemoryMarshal.Write(dst.Slice(WireOffsets.PacketHeaderSendingTimeOffset, 8), in sendingTimeNanos);
    }

    /// <summary>
    /// Writes <c>Order_MBO_50</c> for a NEW (or UPDATED) order: framing header
    /// + SBE header (V6) + body. Returns total bytes written.
    /// </summary>
    public static int WriteOrderAddedFrame(
        Span<byte> dst,
        long securityId,
        long secondaryOrderId,
        byte mdEntryType,            // BID = 0x30, OFFER = 0x31
        long priceMantissa,
        long size,
        uint rptSeq,
        ulong insertTimestampNanos)
    {
        const int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.OrderBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        var msgHeaderSpan = dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize);
        B3.Umdf.Mbo.Sbe.V16.V6.Order_MBO_50Data.WriteHeader(msgHeaderSpan);

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.OrderBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.OrderBodySecurityIdOffset, 8), in securityId);
        body[WireOffsets.OrderBodyMdUpdateActionOffset] = (byte)MDUpdateAction.NEW;
        body[WireOffsets.OrderBodyMdEntryTypeOffset] = mdEntryType;
        MemoryMarshal.Write(body.Slice(WireOffsets.OrderBodyMdEntryPxOffset, 8), in priceMantissa);
        MemoryMarshal.Write(body.Slice(WireOffsets.OrderBodyMdEntrySizeOffset, 8), in size);
        MemoryMarshal.Write(body.Slice(WireOffsets.OrderBodyMdInsertTimestampOffset, 8), in insertTimestampNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.OrderBodySecondaryOrderIdOffset, 8), in secondaryOrderId);
        MemoryMarshal.Write(body.Slice(WireOffsets.OrderBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>DeleteOrder_MBO_51</c> (V16 layout). Returns total bytes.
    /// </summary>
    public static int WriteOrderDeletedFrame(
        Span<byte> dst,
        long securityId,
        long secondaryOrderId,
        byte mdEntryType,
        long size,
        uint rptSeq,
        ulong transactTimeNanos,
        long? priceMantissa = null)
    {
        const int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.DeleteOrderBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.DeleteOrder_MBO_51Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.DeleteOrderBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.DeleteOrderBodySecurityIdOffset, 8), in securityId);
        body[WireOffsets.DeleteOrderBodyMdEntryTypeOffset] = mdEntryType;
        MemoryMarshal.Write(body.Slice(WireOffsets.DeleteOrderBodyMdEntrySizeOffset, 8), in size);
        MemoryMarshal.Write(body.Slice(WireOffsets.DeleteOrderBodySecondaryOrderIdOffset, 8), in secondaryOrderId);
        MemoryMarshal.Write(body.Slice(WireOffsets.DeleteOrderBodyTransactTimeOffset, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.DeleteOrderBodyRptSeqOffset, 4), in rptSeq);
        long px = priceMantissa ?? PriceNull;
        MemoryMarshal.Write(body.Slice(WireOffsets.DeleteOrderBodyMdEntryPxOffset, 8), in px);

        return total;
    }

    /// <summary>
    /// Writes <c>Trade_53</c> (V16). Returns total bytes.
    /// </summary>
    public static int WriteTradeFrame(
        Span<byte> dst,
        long securityId,
        long priceMantissa,
        long size,
        uint tradeId,
        ushort tradeDate,
        ulong transactTimeNanos,
        uint rptSeq,
        uint? buyerFirm = null,
        uint? sellerFirm = null)
    {
        const int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.TradeBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.Trade_53Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.TradeBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodySecurityIdOffset, 8), in securityId);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyMdEntryPxOffset, 8), in priceMantissa);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyMdEntrySizeOffset, 8), in size);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyTradeIdOffset, 4), in tradeId);
        uint buyer = buyerFirm ?? 0u;
        uint seller = sellerFirm ?? 0u;
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyMdEntryBuyerOffset, 4), in buyer);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyMdEntrySellerOffset, 4), in seller);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyTradeDateOffset, 2), in tradeDate);
        body[WireOffsets.TradeBodyTrdSubTypeOffset] = 255; // NULL sentinel
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyTransactTimeOffset, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>SnapshotFullRefresh_Header_30</c> (V6 — accepted by V16 reader).
    /// </summary>
    public static int WriteSnapshotHeaderFrame(
        Span<byte> dst,
        long securityId,
        uint totNumReports,
        uint totNumBids,
        uint totNumOffers,
        ushort totNumStats,
        uint? lastRptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SnapHeaderBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.SnapHeaderBlockLength);
        body.Clear();
        // SecurityExchange "BVMF" at body offset 8
        Encoding.ASCII.GetBytes("BVMF", body.Slice(8, 4));
        MemoryMarshal.Write(body.Slice(WireOffsets.SnapHeaderBodySecurityIdOffset, 8), in securityId);
        MemoryMarshal.Write(body.Slice(WireOffsets.SnapHeaderBodyTotNumReportsOffset, 4), in totNumReports);
        MemoryMarshal.Write(body.Slice(WireOffsets.SnapHeaderBodyTotNumBidsOffset, 4), in totNumBids);
        MemoryMarshal.Write(body.Slice(WireOffsets.SnapHeaderBodyTotNumOffersOffset, 4), in totNumOffers);
        MemoryMarshal.Write(body.Slice(WireOffsets.SnapHeaderBodyTotNumStatsOffset, 2), in totNumStats);
        uint rpt = lastRptSeq ?? 0u;
        MemoryMarshal.Write(body.Slice(WireOffsets.SnapHeaderBodyLastRptSeqOffset, 4), in rpt);

        return total;
    }

    public readonly record struct SnapshotEntry(
        long PriceMantissa,
        long Size,
        ulong InsertTimestampNanos,
        long SecondaryOrderId,
        byte MdEntryType,
        uint? EnteringFirm = null);

    /// <summary>
    /// Writes <c>SnapshotFullRefresh_Orders_MBO_71</c> with N group entries.
    /// </summary>
    public static int WriteSnapshotOrdersFrame(
        Span<byte> dst,
        long securityId,
        ReadOnlySpan<SnapshotEntry> entries)
    {
        if (entries.Length > 255)
            throw new ArgumentException(
                $"Orders_71 supports at most 255 entries per group (NumInGroup is byte). Got {entries.Length}. Caller must chunk.",
                nameof(entries));

        int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
                  + WireOffsets.SnapOrdersHeaderBlockLength
                  + WireOffsets.SnapOrdersGroupSizeEncodingSize
                  + WireOffsets.SnapOrdersEntrySize * entries.Length;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.SnapshotFullRefresh_Orders_MBO_71Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        int p = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        // Block-level body: SecurityID @0
        MemoryMarshal.Write(dst.Slice(p, 8), in securityId);
        p += WireOffsets.SnapOrdersHeaderBlockLength;

        // Group size encoding: ushort BlockLength + byte NumInGroup
        ushort entryBlockLen = WireOffsets.SnapOrdersEntrySize;
        byte numInGroup = (byte)entries.Length;
        MemoryMarshal.Write(dst.Slice(p, 2), in entryBlockLen);
        dst[p + 2] = numInGroup;
        p += WireOffsets.SnapOrdersGroupSizeEncodingSize;

        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            long px = e.PriceMantissa;
            long sz = e.Size;
            ulong ts = e.InsertTimestampNanos;
            long ordId = e.SecondaryOrderId;
            uint firm = e.EnteringFirm ?? 0u;

            var entry = dst.Slice(p, WireOffsets.SnapOrdersEntrySize);
            entry.Clear();
            MemoryMarshal.Write(entry.Slice(0, 8), in px);
            MemoryMarshal.Write(entry.Slice(8, 8), in sz);
            MemoryMarshal.Write(entry.Slice(20, 4), in firm);
            MemoryMarshal.Write(entry.Slice(24, 8), in ts);
            MemoryMarshal.Write(entry.Slice(32, 8), in ordId);
            entry[40] = e.MdEntryType;
            entry[41] = 0; // matchEventIndicator
            p += WireOffsets.SnapOrdersEntrySize;
        }

        return total;
    }

    /// <summary>
    /// Writes <c>SecurityDefinition_12</c> with the minimum fields needed
    /// for the consumer's instrument pipeline (SecurityID, SecurityExchange,
    /// Symbol, SecurityType, TotNoRelatedSym, ISIN, optional ValidityTimestamp,
    /// optional MaturityDate). Trailing 9 bytes encode three empty groups
    /// (NoUnderlyings, NoLegs, NoInstrAttribs).
    /// </summary>
    public static int WriteSecurityDefinitionFrame(
        Span<byte> dst,
        long securityId,
        string symbol,
        string isin,
        byte securityTypeByte,
        uint totNoRelatedSym,
        long securityValidityTimestamp = 0L,
        int maturityDate = 0)
    {
        int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SecDefBodyTotal;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.V6.SecurityDefinition_12Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.SecDefBodyTotal);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefSecurityIdOffset, 8), in securityId);
        Encoding.ASCII.GetBytes("BVMF", body.Slice(WireOffsets.SecDefSecurityExchangeOffset, 4));

        WriteFixedAscii(body.Slice(WireOffsets.SecDefSymbolOffset, 20), symbol);
        body[WireOffsets.SecDefSecurityTypeOffset] = securityTypeByte;
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefTotNoRelatedSymOffset, 4), in totNoRelatedSym);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefSecurityValidityTimestampOffset, 8), in securityValidityTimestamp);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefMaturityDateOffset, 4), in maturityDate);
        WriteFixedAscii(body.Slice(WireOffsets.SecDefIsinNumberOffset, 12), isin);
        // Trailing GroupSizeEncodings already zero from body.Clear().
        return total;
    }

    private static void WriteFramingHeader(Span<byte> dst, int totalFrameLength)
    {
        ushort messageLength = checked((ushort)totalFrameLength);
        ushort encodingType = 0;
        MemoryMarshal.Write(dst.Slice(WireOffsets.FramingHeaderMessageLengthOffset, 2), in messageLength);
        MemoryMarshal.Write(dst.Slice(WireOffsets.FramingHeaderEncodingTypeOffset, 2), in encodingType);
    }

    private static void WriteFixedAscii(Span<byte> dst, string value)
    {
        dst.Clear();
        if (string.IsNullOrEmpty(value)) return;
        int n = Math.Min(value.Length, dst.Length);
        Encoding.ASCII.GetBytes(value.AsSpan(0, n), dst);
    }

    private static void ThrowTooSmall(string paramName, int requiredBytes)
        => throw new ArgumentException($"Buffer too small (need {requiredBytes} bytes).", paramName);
}
