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
    /// Optional option-specific payload for <see cref="WriteSecurityDefinitionFrame"/>.
    /// When supplied, the encoder fills in the SBE option fields
    /// (strikePrice, contractMultiplier, maturityMonthYear, exerciseStyle,
    /// putOrCall, optPayoutType) and emits one entry in the NoUnderlyings
    /// repeating group. Equity / non-option instruments must pass
    /// <c>null</c>; the encoder then writes SBE NULL sentinels for every
    /// option field and a zero-entry NoUnderlyings group.
    /// </summary>
    public readonly struct OptionDefinitionFields
    {
        /// <summary>Human-units strike price (e.g. 28.50). Required.</summary>
        public required decimal StrikePrice { get; init; }

        /// <summary>Contract size multiplier (e.g. 100 shares per contract). Required.</summary>
        public required decimal ContractMultiplier { get; init; }

        /// <summary>Last trading / expiration date.</summary>
        public required DateOnly ExpirationDate { get; init; }

        /// <summary>0 = Put, 1 = Call (SBE PutOrCall enum).</summary>
        public required byte PutOrCallByte { get; init; }

        /// <summary>0 = European, 1 = American (SBE ExerciseStyle enum).</summary>
        public required byte ExerciseStyleByte { get; init; }

        /// <summary>SBE OptPayoutType enum byte (1=Vanilla, 2=Capped, 3=Binary). Null = SBE NULL sentinel (255).</summary>
        public byte? OptPayoutTypeByte { get; init; }

        /// <summary>SecurityID of the underlying instrument (e.g. the equity series the option references).</summary>
        public required long UnderlyingSecurityId { get; init; }

        /// <summary>Ticker symbol of the underlying instrument (≤ 20 ASCII chars, space-padded on the wire).</summary>
        public required string UnderlyingSymbol { get; init; }
    }

    // SBE NULL sentinels for the optional SecurityDefinition_12 fields.
    // Centralised here so encoder+tests share one source of truth.
    private const long SecDefPriceOptionalNull = long.MinValue;  // PriceOptional & Fixed8 mantissa NULL.
    private const byte SecDefByteEnumNull = 255;                 // ExerciseStyle / PutOrCall / OptPayoutType / ImpliedMarketIndicator NULL.
    private const long SecDefPriceMantissaScale = 10_000L;       // PriceOptional exponent = -4.
    private const long SecDefFixed8MantissaScale = 100_000_000L; // Fixed8 exponent = -8.
    private const ushort SecDefNoUnderlyingsEntryBlockLength = (ushort)WireOffsets.SecDefNoUnderlyingsEntrySize;

    /// <summary>
    /// Writes <c>SecurityDefinition_12</c> (V16) with the fields the
    /// consumer's instrument pipeline needs (SecurityID, SecurityExchange,
    /// Symbol, SecurityType, TotNoRelatedSym, ISIN, optional ValidityTimestamp,
    /// optional MaturityDate). When <paramref name="optionFields"/> is
    /// non-null the encoder additionally fills the SBE option fields
    /// (StrikePrice, ContractMultiplier, MaturityMonthYear, ExerciseStyle,
    /// PutOrCall, OptPayoutType) and emits one entry in the NoUnderlyings
    /// repeating group. Equity callers pass <c>null</c> and the corresponding
    /// optional fields are written as SBE NULL sentinels.
    /// <para>
    /// Trailing 9 bytes encode three empty group dimension headers
    /// (NoUnderlyings, NoLegs, NoInstrAttribs) plus the <c>securityDesc</c>
    /// TextEncoding length prefix (uint8, always 0 — empty description).
    /// The length prefix is mandatory even when no text is attached —
    /// without it the consumer's generated <c>SecurityDefinition_12DataReader</c>
    /// reads past the SBE message boundary and throws (issue #222).
    /// </para>
    /// Returns total bytes written; the value depends on whether
    /// <paramref name="optionFields"/> is null (no NoUnderlyings entry,
    /// <see cref="WireOffsets.SecDefBodyTotalNoUnderlyings"/>) or non-null
    /// (one NoUnderlyings entry, <see cref="WireOffsets.SecDefBodyTotalOneUnderlying"/>).
    /// </summary>
    public static int WriteSecurityDefinitionFrame(
        Span<byte> dst,
        long securityId,
        string symbol,
        string isin,
        byte securityTypeByte,
        uint totNoRelatedSym,
        long securityValidityTimestamp = 0L,
        int maturityDate = 0,
        OptionDefinitionFields? optionFields = null)
    {
        bool hasOption = optionFields.HasValue;
        int bodyTotal = hasOption
            ? WireOffsets.SecDefBodyTotalOneUnderlying
            : WireOffsets.SecDefBodyTotalNoUnderlyings;
        int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + bodyTotal;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        // V16 WriteHeader stamps BlockLength=232, TemplateId=12, SchemaId=2, Version=16.
        // The consumer's V16 reader dispatches on the explicit BlockLength
        // field, so equity and option frames share the same header shape
        // and only differ in the trailing NoUnderlyings group dimensions.
        B3.Umdf.Mbo.Sbe.V16.SecurityDefinition_12Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            bodyTotal);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefSecurityIdOffset, 8), in securityId);
        Encoding.ASCII.GetBytes("BVMF", body.Slice(WireOffsets.SecDefSecurityExchangeOffset, 4));

        WriteFixedAscii(body.Slice(WireOffsets.SecDefSymbolOffset, 20), symbol);
        body[WireOffsets.SecDefSecurityTypeOffset] = securityTypeByte;
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefTotNoRelatedSymOffset, 4), in totNoRelatedSym);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefSecurityValidityTimestampOffset, 8), in securityValidityTimestamp);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefMaturityDateOffset, 4), in maturityDate);
        WriteFixedAscii(body.Slice(WireOffsets.SecDefIsinNumberOffset, 12), isin);

        // Option-related optional fields: default to SBE NULL sentinels so
        // equity decoders observe null rather than a stray zero
        // (e.g. PriceOptional mantissa 0 == 0.0, not NULL). Overwritten
        // below when optionFields is supplied.
        long priceNull = SecDefPriceOptionalNull;
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefStrikePriceOffset, 8), in priceNull);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecDefContractMultiplierOffset, 8), in priceNull);
        body[WireOffsets.SecDefExerciseStyleOffset] = SecDefByteEnumNull;
        body[WireOffsets.SecDefPutOrCallOffset] = SecDefByteEnumNull;
        body[WireOffsets.SecDefImpliedMarketIndicatorOffset] = SecDefByteEnumNull;
        body[WireOffsets.SecDefOptPayoutTypeOffset] = SecDefByteEnumNull;
        // MaturityMonthYear: all-zero bytes are the SBE NULL representation
        // (year=0 sentinel), already written by body.Clear().

        // Trailing group dimensions + securityDesc length prefix layout:
        //   [NoUnderlyings dim 3][N×28 entries][NoLegs dim 3][NoInstrAttribs dim 3][descLen 1]
        int p = WireOffsets.SecDefBlockLength;

        if (hasOption)
        {
            var opt = optionFields!.Value;

            // Option scalar fields.
            long strikeMantissa = ToScaledMantissa(opt.StrikePrice, SecDefPriceMantissaScale, nameof(opt.StrikePrice));
            long multiplierMantissa = ToScaledMantissa(opt.ContractMultiplier, SecDefFixed8MantissaScale, nameof(opt.ContractMultiplier));
            MemoryMarshal.Write(body.Slice(WireOffsets.SecDefStrikePriceOffset, 8), in strikeMantissa);
            MemoryMarshal.Write(body.Slice(WireOffsets.SecDefContractMultiplierOffset, 8), in multiplierMantissa);

            // MaturityMonthYear: year (ushort)@0, month (byte)@2, day (byte)@3, week (byte)@4.
            var mmy = body.Slice(WireOffsets.SecDefMaturityMonthYearOffset, WireOffsets.SecDefMaturityMonthYearSize);
            ushort year = (ushort)opt.ExpirationDate.Year;
            byte month = (byte)opt.ExpirationDate.Month;
            byte day = (byte)opt.ExpirationDate.Day;
            MemoryMarshal.Write(mmy.Slice(0, 2), in year);
            mmy[2] = month;
            mmy[3] = day;
            mmy[4] = 0; // week: not modelled by the venue simulator.

            body[WireOffsets.SecDefExerciseStyleOffset] = opt.ExerciseStyleByte;
            body[WireOffsets.SecDefPutOrCallOffset] = opt.PutOrCallByte;
            body[WireOffsets.SecDefOptPayoutTypeOffset] = opt.OptPayoutTypeByte ?? SecDefByteEnumNull;

            // NoUnderlyings dimension header: BlockLength=28 (ushort), NumInGroup=1 (byte).
            ushort entryBlockLen = SecDefNoUnderlyingsEntryBlockLength;
            MemoryMarshal.Write(body.Slice(p, 2), in entryBlockLen);
            body[p + 2] = 1;
            p += WireOffsets.GroupSizeEncodingSize;

            // Single entry: underlyingSecurityID@0, underlyingSymbol@8 (20 ASCII).
            var entry = body.Slice(p, WireOffsets.SecDefNoUnderlyingsEntrySize);
            long underlyingSecId = opt.UnderlyingSecurityId;
            MemoryMarshal.Write(entry.Slice(WireOffsets.SecDefNoUnderlyingsEntrySecurityIdOffset, 8), in underlyingSecId);
            WriteFixedAscii(
                entry.Slice(
                    WireOffsets.SecDefNoUnderlyingsEntrySymbolOffset,
                    WireOffsets.SecDefNoUnderlyingsEntrySymbolSize),
                opt.UnderlyingSymbol);
            p += WireOffsets.SecDefNoUnderlyingsEntrySize;
        }
        else
        {
            // Empty NoUnderlyings dimension header — zeros from body.Clear().
            p += WireOffsets.GroupSizeEncodingSize;
        }

        // NoLegs + NoInstrAttribs dimension headers and securityDesc length
        // prefix are all zero (from body.Clear()). No further writes needed.
        return total;
    }

    private static long ToScaledMantissa(decimal value, long scale, string fieldName)
    {
        decimal scaled = decimal.Round(value * scale, 0, MidpointRounding.AwayFromZero);
        if (scaled < long.MinValue + 1 || scaled > long.MaxValue)
            throw new ArgumentOutOfRangeException(fieldName, value, "value does not fit in a SBE Fixed8/PriceOptional mantissa");
        return (long)scaled;
    }

    /// <summary>
    /// Writes <c>ChannelReset_11</c> (V16): framing header + SBE header + 12-byte body.
    /// MatchEventIndicator is set to bit 5 (RecoveryMsg) | bit 7 (EndOfEvent),
    /// per the schema's documented "bits applied to this message" annotation,
    /// signalling consumers that this is a hard reset / end-of-event boundary.
    /// Returns total bytes written.
    /// </summary>
    public static int WriteChannelResetFrame(
        Span<byte> dst,
        ulong mdEntryTimestampNanos)
    {
        const int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.ChannelResetBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.ChannelReset_11Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.ChannelResetBlockLength);
        body.Clear();

        // MatchEventIndicator: bit 5 (RecoveryMsg, value 0x20) | bit 7 (EndOfEvent, value 0x80) = 0xA0.
        body[WireOffsets.ChannelResetBodyMatchEventIndicatorOffset] = 0xA0;
        MemoryMarshal.Write(body.Slice(WireOffsets.ChannelResetBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);

        return total;
    }

    /// <summary>
    /// Writes <c>TradeBust_57</c> (V16). Used by the operator-triggered
    /// trade-bust replay path (issue #15) to surface a trade reversal on
    /// the incremental channel without going through the matching engine.
    /// </summary>
    public static int WriteTradeBustFrame(
        Span<byte> dst,
        long securityId,
        long priceMantissa,
        long size,
        uint tradeId,
        ushort tradeDate,
        ulong transactTimeNanos,
        uint rptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.TradeBustBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.TradeBust_57Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.TradeBustBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator: bit 7 (EndOfEvent, 0x80). Single-message bust
        // packets are self-contained events, so flag end-of-event here.
        body[WireOffsets.TradeBustBodyMatchEventIndicatorOffset] = 0x80;
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodyMdEntryPxOffset, 8), in priceMantissa);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodyMdEntrySizeOffset, 8), in size);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodyTradeIdOffset, 4), in tradeId);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodyTradeDateOffset, 2), in tradeDate);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodyTransactTimeOffset, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.TradeBustBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>Sequence_2</c> (V16). Returns total bytes. Used as an
    /// idle/heartbeat marker that carries the next expected incremental
    /// sequence number (gap-functional #22 / #200).
    /// </summary>
    public static int WriteSequenceFrame(Span<byte> dst, uint nextSeqNo)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SequenceBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.Sequence_2Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.SequenceBlockLength);
        body.Clear();
        MemoryMarshal.Write(body.Slice(WireOffsets.SequenceBodyNextSeqNoOffset, 4), in nextSeqNo);
        return total;
    }

    /// <summary>
    /// Writes <c>SequenceReset_1</c> (V16). Returns total bytes. Marks the
    /// start of an instrument-replay or snapshot-recovery loop (the
    /// schema fixes <c>NewSeqNo=1</c> as a constant; the message has no
    /// body fields).
    /// </summary>
    public static int WriteSequenceResetFrame(Span<byte> dst)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SequenceResetBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.SequenceReset_1Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));
        return total;
    }

    /// <summary>
    /// Writes <c>EmptyBook_9</c> (V16). Returns total bytes. Emitted when
    /// a previously-populated book side becomes empty after a cancel /
    /// fill (gap-functional #22). The MDUpdateAction (NEW) and
    /// MDEntryType (EMPTY_BOOK) are template-level constants per schema.
    /// </summary>
    public static int WriteEmptyBookFrame(
        Span<byte> dst,
        long securityId,
        ulong mdEntryTimestampNanos)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.EmptyBookBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.EmptyBook_9Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.EmptyBookBlockLength);
        body.Clear();
        MemoryMarshal.Write(body.Slice(WireOffsets.EmptyBookBodySecurityIdOffset, 8), in securityId);
        MemoryMarshal.Write(body.Slice(WireOffsets.EmptyBookBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);
        return total;
    }

    /// <summary>
    /// Writes <c>MassDeleteOrders_MBO_52</c> (V16). Returns total bytes.
    /// Emitted once per (SecurityID, Side) at the start of a mass-cancel
    /// operation as an atomic boundary marker (gap-functional #8 / #199).
    /// Per-order <c>DeleteOrder_MBO_51</c> frames for the same orders
    /// follow in the same UMDF packet, so consumers that ignore
    /// <c>MassDeleteOrders</c> remain correct.
    /// </summary>
    public static int WriteMassDeleteOrdersFrame(
        Span<byte> dst,
        long securityId,
        byte mdEntryType,
        uint rptSeq,
        ulong transactTimeNanos)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.MassDeleteOrdersBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.MassDeleteOrders_MBO_52Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.MassDeleteOrdersBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.MassDeleteOrdersBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator overlays SecurityExchange at offset 8 in the
        // V16 schema (1 byte). Leave at 0 (no event flags set).
        body[WireOffsets.MassDeleteOrdersBodyMdUpdateActionOffset] = (byte)MDUpdateAction.DELETE_THRU;
        body[WireOffsets.MassDeleteOrdersBodyMdEntryTypeOffset] = mdEntryType;
        MemoryMarshal.Write(body.Slice(WireOffsets.MassDeleteOrdersBodyTransactTimeOffset, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.MassDeleteOrdersBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>SecurityStatus_3</c> (V16). Returns total bytes. Emitted
    /// when an instrument's trading phase transitions
    /// (gap-functional §5 / #201). <paramref name="securityTradingEvent"/>
    /// is optional (255 = NULL); <paramref name="rptSeq"/> 0 indicates
    /// NULL on the wire but production callers should always pass the
    /// engine-allocated sequence so consumers can detect gaps.
    /// </summary>
    public static int WriteSecurityStatusFrame(
        Span<byte> dst,
        long securityId,
        byte tradingSessionId,
        byte securityTradingStatus,
        byte securityTradingEvent,
        ushort tradeDate,
        ulong tradSesOpenTimeNanos,
        ulong transactTimeNanos,
        uint rptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.SecurityStatus_3Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.SecurityStatusBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityStatusBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator overlays SecurityExchange at offset 8 (1 byte).
        // Leave at 0 (no flags set).
        body[WireOffsets.SecurityStatusBodyTradingSessionIdOffset] = tradingSessionId;
        body[WireOffsets.SecurityStatusBodySecurityTradingStatusOffset] = securityTradingStatus;
        body[WireOffsets.SecurityStatusBodySecurityTradingEventOffset] = securityTradingEvent;
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityStatusBodyTradeDateOffset, 2), in tradeDate);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityStatusBodyTradSesOpenTimeOffset, 8), in tradSesOpenTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityStatusBodyTransactTimeOffset, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityStatusBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>PriceBand_22</c> (V16). Returns total bytes. The schema carries
    /// one effective low/high envelope per instrument, so callers pass the
    /// already-resolved absolute limit prices.
    /// </summary>
    public static int WritePriceBandFrame(
        Span<byte> dst,
        long securityId,
        long lowLimitPriceMantissa,
        long highLimitPriceMantissa,
        ulong mdEntryTimestampNanos,
        uint rptSeq,
        byte priceBandType = (byte)PriceBandType.HARD_LIMIT,
        byte priceLimitType = (byte)PriceLimitType.PRICE_UNIT,
        long tradingReferencePriceMantissa = long.MinValue,
        byte priceBandMidpointPriceType = 255)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.PriceBandBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.PriceBand_22Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.PriceBandBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.PriceBandBodySecurityIdOffset, 8), in securityId);
        body[WireOffsets.PriceBandBodyPriceBandTypeOffset] = priceBandType;
        body[WireOffsets.PriceBandBodyPriceLimitTypeOffset] = priceLimitType;
        body[WireOffsets.PriceBandBodyPriceBandMidpointPriceTypeOffset] = priceBandMidpointPriceType;
        MemoryMarshal.Write(body.Slice(WireOffsets.PriceBandBodyLowLimitPriceOffset, 8), in lowLimitPriceMantissa);
        MemoryMarshal.Write(body.Slice(WireOffsets.PriceBandBodyHighLimitPriceOffset, 8), in highLimitPriceMantissa);
        MemoryMarshal.Write(body.Slice(WireOffsets.PriceBandBodyTradingReferencePriceOffset, 8), in tradingReferencePriceMantissa);
        MemoryMarshal.Write(body.Slice(WireOffsets.PriceBandBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.PriceBandBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>SecurityGroupPhase_10</c> (V16). Returns total bytes.
    /// Emitted when a security group's phase transitions
    /// (gap-functional §5 / #201). <paramref name="securityGroup"/> is an
    /// 8-byte ASCII identifier left-padded with NUL.
    /// </summary>
    public static int WriteSecurityGroupPhaseFrame(
        Span<byte> dst,
        ReadOnlySpan<byte> securityGroup,
        byte tradingSessionId,
        byte tradingSessionSubId,
        byte securityTradingEvent,
        ushort tradeDate,
        ulong tradSesOpenTimeNanos,
        ulong transactTimeNanos)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityGroupPhaseBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.SecurityGroupPhase_10Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.SecurityGroupPhaseBlockLength);
        body.Clear();

        var groupSlice = body.Slice(
            WireOffsets.SecurityGroupPhaseBodySecurityGroupOffset,
            WireOffsets.SecurityGroupPhaseBodySecurityGroupLength);
        var copyLen = Math.Min(securityGroup.Length, groupSlice.Length);
        if (copyLen > 0) securityGroup[..copyLen].CopyTo(groupSlice);
        body[WireOffsets.SecurityGroupPhaseBodyTradingSessionIdOffset] = tradingSessionId;
        body[WireOffsets.SecurityGroupPhaseBodyTradingSessionSubIdOffset] = tradingSessionSubId;
        body[WireOffsets.SecurityGroupPhaseBodySecurityTradingEventOffset] = securityTradingEvent;
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityGroupPhaseBodyTradeDateOffset, 2), in tradeDate);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityGroupPhaseBodyTradSesOpenTimeOffset, 8), in tradSesOpenTimeNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.SecurityGroupPhaseBodyTransactTimeOffset, 8), in transactTimeNanos);

        return total;
    }

    /// <summary>
    /// Writes <c>TheoreticalOpeningPrice_16</c> (V16). Returns total bytes.
    /// Emitted on every accumulation event during an auction phase
    /// (gap-functional §6 / Onda M · M2 / #229). When <paramref name="hasTop"/>
    /// is false the encoder writes <c>mDUpdateAction=DELETE</c> with NULL
    /// price and quantity, signalling the consumer to clear any prior TOP.
    /// </summary>
    public static int WriteTheoreticalOpeningPriceFrame(
        Span<byte> dst,
        long securityId,
        bool hasTop,
        long priceMantissa,
        long quantity,
        ushort tradeDate,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.TheoreticalOpeningPriceBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.TheoreticalOpeningPrice_16Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.TheoreticalOpeningPriceBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.TheoreticalOpeningPriceBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator overlays SecurityExchange at offset 8 (1 byte).
        // Leave at 0 (no flags set).
        body[WireOffsets.TheoreticalOpeningPriceBodyMdUpdateActionOffset] = hasTop ? (byte)0 : (byte)2;
        MemoryMarshal.Write(body.Slice(WireOffsets.TheoreticalOpeningPriceBodyTradeDateOffset, 2), in tradeDate);
        long pxOnWire = hasTop ? priceMantissa : long.MinValue;
        long qtyOnWire = hasTop ? quantity : long.MinValue;
        MemoryMarshal.Write(body.Slice(WireOffsets.TheoreticalOpeningPriceBodyMdEntryPxOffset, 8), in pxOnWire);
        MemoryMarshal.Write(body.Slice(WireOffsets.TheoreticalOpeningPriceBodyMdEntrySizeOffset, 8), in qtyOnWire);
        MemoryMarshal.Write(body.Slice(WireOffsets.TheoreticalOpeningPriceBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.TheoreticalOpeningPriceBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// <c>ImbalanceCondition</c> bitset value for "more buyers" (bit 8 set).
    /// </summary>
    public const ushort ImbalanceConditionMoreBuyers = 1 << 8;

    /// <summary>
    /// <c>ImbalanceCondition</c> bitset value for "more sellers" (bit 9 set).
    /// </summary>
    public const ushort ImbalanceConditionMoreSellers = 1 << 9;

    /// <summary>
    /// Writes <c>AuctionImbalance_19</c> (V16). Returns total bytes.
    /// Emitted alongside <c>TheoreticalOpeningPrice_16</c> on every
    /// accumulation event during an auction phase (gap-functional §6 /
    /// Onda M · M2 / #229). <paramref name="imbalanceCondition"/> is
    /// the raw bitset (0 = balanced, <see cref="ImbalanceConditionMoreBuyers"/>,
    /// or <see cref="ImbalanceConditionMoreSellers"/>);
    /// <paramref name="imbalanceQty"/> is the residual one-sided
    /// quantity that would be left unmatched at the TOP price (or the
    /// total resting on the only populated side when no crossing
    /// exists). When the auction has cleared and there is no remaining
    /// imbalance, pass <c>hasImbalance=false</c> and the encoder writes
    /// <c>mDUpdateAction=DELETE</c> with NULL quantity.
    /// </summary>
    public static int WriteAuctionImbalanceFrame(
        Span<byte> dst,
        long securityId,
        bool hasImbalance,
        ushort imbalanceCondition,
        long imbalanceQty,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.AuctionImbalanceBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.AuctionImbalance_19Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.AuctionImbalanceBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.AuctionImbalanceBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator at offset 8 left zeroed.
        body[WireOffsets.AuctionImbalanceBodyMdUpdateActionOffset] = hasImbalance ? (byte)0 : (byte)2;
        ushort cond = hasImbalance ? imbalanceCondition : (ushort)0;
        MemoryMarshal.Write(body.Slice(WireOffsets.AuctionImbalanceBodyImbalanceConditionOffset, 2), in cond);
        long qtyOnWire = hasImbalance ? imbalanceQty : long.MinValue;
        MemoryMarshal.Write(body.Slice(WireOffsets.AuctionImbalanceBodyMdEntrySizeOffset, 8), in qtyOnWire);
        MemoryMarshal.Write(body.Slice(WireOffsets.AuctionImbalanceBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.AuctionImbalanceBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// <c>OpenCloseSettlFlag.DAILY</c> = 0 — schema enum value (byte 0,
    /// NOT the ASCII digit '0'). Used for OpeningPrice_15 (daily-open)
    /// and ClosingPrice_17 (daily-close).
    /// </summary>
    public const byte OpenCloseSettlFlagDaily = 0;

    /// <summary>
    /// Writes <c>OpeningPrice_15</c> (V16). Returns total bytes.
    /// Emitted exactly once per opening uncross that printed at least
    /// one trade (Onda M4 / issue #231). The ECP's <c>NetChgPrevDay</c>
    /// is always written as NULL (long.MinValue) — net-change vs the
    /// previous-day close is a downstream concern.
    /// </summary>
    public static int WriteOpeningPriceFrame(
        Span<byte> dst,
        long securityId,
        long priceMantissa,
        ushort tradeDate,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OpeningPriceBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.OpeningPrice_15Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.OpeningPriceBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.OpeningPriceBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator overlays SecurityExchange at offset 8 — left zeroed.
        body[WireOffsets.OpeningPriceBodyMdUpdateActionOffset] = 0; // NEW
        body[WireOffsets.OpeningPriceBodyOpenCloseSettlFlagOffset] = OpenCloseSettlFlagDaily;
        MemoryMarshal.Write(body.Slice(WireOffsets.OpeningPriceBodyMdEntryPxOffset, 8), in priceMantissa);
        long netChgNull = long.MinValue;
        MemoryMarshal.Write(body.Slice(WireOffsets.OpeningPriceBodyNetChgPrevDayOffset, 8), in netChgNull);
        MemoryMarshal.Write(body.Slice(WireOffsets.OpeningPriceBodyTradeDateOffset, 2), in tradeDate);
        MemoryMarshal.Write(body.Slice(WireOffsets.OpeningPriceBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.OpeningPriceBodyRptSeqOffset, 4), in rptSeq);

        return total;
    }

    /// <summary>
    /// Writes <c>ClosingPrice_17</c> (V16). Returns total bytes.
    /// Emitted exactly once per closing-call uncross that printed at
    /// least one trade (Onda M4 / issue #231). <c>LastTradeDate</c> is
    /// written as NULL — it is informational metadata for cases where
    /// the close carries forward a price from a previous trading day,
    /// which the simulator does not currently model.
    /// </summary>
    public static int WriteClosingPriceFrame(
        Span<byte> dst,
        long securityId,
        long priceMantissa,
        ushort tradeDate,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int total = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.ClosingPriceBlockLength;
        if (dst.Length < total) ThrowTooSmall(nameof(dst), total);

        WriteFramingHeader(dst, total);
        B3.Umdf.Mbo.Sbe.V16.ClosingPrice_17Data.WriteHeader(
            dst.Slice(WireOffsets.FramingHeaderSize, WireOffsets.SbeMessageHeaderSize));

        var body = dst.Slice(
            WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
            WireOffsets.ClosingPriceBlockLength);
        body.Clear();

        MemoryMarshal.Write(body.Slice(WireOffsets.ClosingPriceBodySecurityIdOffset, 8), in securityId);
        // MatchEventIndicator overlays SecurityExchange at offset 8 — left zeroed.
        body[WireOffsets.ClosingPriceBodyOpenCloseSettlFlagOffset] = OpenCloseSettlFlagDaily;
        MemoryMarshal.Write(body.Slice(WireOffsets.ClosingPriceBodyMdEntryPxOffset, 8), in priceMantissa);
        ushort lastTradeDateNull = 0;
        MemoryMarshal.Write(body.Slice(WireOffsets.ClosingPriceBodyLastTradeDateOffset, 2), in lastTradeDateNull);
        MemoryMarshal.Write(body.Slice(WireOffsets.ClosingPriceBodyTradeDateOffset, 2), in tradeDate);
        MemoryMarshal.Write(body.Slice(WireOffsets.ClosingPriceBodyMdEntryTimestampOffset, 8), in mdEntryTimestampNanos);
        MemoryMarshal.Write(body.Slice(WireOffsets.ClosingPriceBodyRptSeqOffset, 4), in rptSeq);

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
