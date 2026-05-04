using B3.EntryPoint.Wire;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

/// <summary>
/// Per-template byte-level encoders for the outbound ExecutionReports we
/// support. The generated SBE structs declare overlapping field offsets
/// (e.g. <c>SecurityExchange</c> + <c>OrderID</c>/<c>LeavesQty</c>/<c>CumQty</c>
/// at offset 44 across templates), so writing through the struct setters in
/// the wrong order overwrites the wire-essential field. We bypass that risk
/// by zeroing the body and writing only the fields the consumer cares about
/// at their verified absolute offsets.
///
/// Each <c>Encode*</c> method writes the SBE 8-byte header + body and returns
/// the total bytes written (8 + BlockLength). Field offsets below are
/// pinned to the V2 schema generated under
/// <c>B3.Entrypoint.Fixp.Sbe.V6.V2</c> in this repo (and OrderCancelRequest
/// from V6 base).
/// </summary>
internal static class ExecutionReportEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;            // SOFH(4) + SBE(8)
    private const int BusinessHeaderSize = 18;
    private const ulong UTCTimestampNullValue = ulong.MaxValue;
    private const long PriceNullMantissa = long.MinValue;

    // All ExecutionReport_* templates are encoded against the **V6** schema
    // (#248). The published `B3.EntryPoint.Client` SDK's InboundDecoder
    // ignores the SBE header `version` field and hard-casts payloads to the
    // latest schema struct via `MemoryMarshal.AsRef<…>` — anything smaller
    // than the V6 BlockLength trips an ArgumentOutOfRangeException and
    // tears down the inbound loop. Bumping every encoder to V6 keeps us
    // compatible with the partner library while remaining backwards-readable
    // by older consumers (BlockLength only grows; trailing fields use SBE
    // null sentinels).
    //
    // BlockLengths and trailing field offsets verified against the generated
    // `B3.Entrypoint.Fixp.Sbe.V6.{ExecutionReport_*Data}` structs:
    //   ER_New    BLOCK_LENGTH = 176  receivedTime@144 + crossType@162 / crossPriori@163 /
    //                                 mmProtReset@164 nulls + tradingSubAccount@172=0
    //   ER_Modify BLOCK_LENGTH = 188  receivedTime@160 + mmProtReset@178 +
    //                                 execRestatementReason@179 nulls + strategyID@180 /
    //                                 tradingSubAccount@184 = 0
    //   ER_Cancel BLOCK_LENGTH = 182  receivedTime@156 (no non-zero trailing nulls)
    //   ER_Trade  BLOCK_LENGTH = 174  V6 trailing nulls at @154 (tradingSessionID),
    //                                 @155 (tradingSessionSubID), @156 (securityTradingStatus),
    //                                 @157 (crossType), @158 (crossPrioritization);
    //                                 strategyID@160 / impliedEventID@164 /
    //                                 tradingSubAccount@170 = 0
    //   ER_Reject BLOCK_LENGTH = 164  receivedTime@138 + ordTagID/investorID/strategyID/
    //                                 tradingSubAccount tail (all zero nulls)
    public const int ExecReportNewBlock = 176;
    public const int ExecReportModifyBlock = 188;
    public const int ExecReportCancelBlock = 182;
    public const int ExecReportTradeBlock = 174;
    public const int ExecReportRejectBlock = 164;

    public const int ExecReportNewTotal = HeaderSize + ExecReportNewBlock;
    public const int ExecReportModifyTotal = HeaderSize + ExecReportModifyBlock;
    public const int ExecReportCancelTotal = HeaderSize + ExecReportCancelBlock;
    public const int ExecReportTradeTotal = HeaderSize + ExecReportTradeBlock;
    public const int ExecReportRejectTotal = HeaderSize + ExecReportRejectBlock;

    private const byte SideBuy = (byte)'1';
    private const byte SideSell = (byte)'2';
    private const byte OrdStatusNew = (byte)'0';
    private const byte OrdStatusPartiallyFilled = (byte)'1';
    private const byte OrdStatusFilled = (byte)'2';
    private const byte OrdStatusCanceled = (byte)'4';
    private const byte OrdStatusReplaced = (byte)'5';
    private const byte OrdStatusRejected = (byte)'8';
    private const byte OrdTypeMarket = (byte)'1';
    private const byte OrdTypeLimit = (byte)'2';
    private const byte OrdTypeStopLoss = (byte)'3';
    private const byte OrdTypeStopLimit = (byte)'4';
    private const byte OrdTypeMwl = (byte)'K';
    private const byte TifDay = (byte)'0';
    private const byte TifIoc = (byte)'3';
    private const byte TifFok = (byte)'4';
    private const byte TifGtc = (byte)'1';
    private const byte TifGtd = (byte)'6';
    private const byte TifAtClose = (byte)'7';
    private const byte TifGoodForAuction = (byte)'A';
    private const byte ExecTypeTrade = (byte)'F';

    private static byte EncodeSide(Matching.Side s) => s == Matching.Side.Buy ? SideBuy : SideSell;
    private static byte EncodeOrdType(Matching.OrderType t) => t switch
    {
        Matching.OrderType.Market => OrdTypeMarket,
        Matching.OrderType.Limit => OrdTypeLimit,
        Matching.OrderType.StopLoss => OrdTypeStopLoss,
        Matching.OrderType.StopLimit => OrdTypeStopLimit,
        Matching.OrderType.MarketWithLeftover => OrdTypeMwl,
        _ => OrdTypeLimit,
    };
    private static byte EncodeTif(Matching.TimeInForce t) => t switch
    {
        Matching.TimeInForce.Day => TifDay,
        Matching.TimeInForce.IOC => TifIoc,
        Matching.TimeInForce.FOK => TifFok,
        Matching.TimeInForce.Gtc => TifGtc,
        Matching.TimeInForce.Gtd => TifGtd,
        Matching.TimeInForce.AtClose => TifAtClose,
        Matching.TimeInForce.GoodForAuction => TifGoodForAuction,
        _ => 0,
    };

    private static void WriteBusinessHeader(Span<byte> body, uint sessionId, uint msgSeqNum, ulong sendingTimeNanos)
    {
        // OutboundBusinessHeader (sequential, Pack=1):
        //   SessionID(4) | MsgSeqNum(4) | SendingTime(8 ulong, optional null=ulong.MaxValue)
        //   | EventIndicator(1 byte) | MarketSegmentID(1 byte optional null=255)
        MemoryMarshal.Write(body.Slice(0, 4), in sessionId);
        MemoryMarshal.Write(body.Slice(4, 4), in msgSeqNum);
        MemoryMarshal.Write(body.Slice(8, 8), in sendingTimeNanos);
        body[16] = 0;
        body[17] = 255;
    }

    public static int EncodeExecReportNew(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        Matching.Side side, ulong clOrdIdValue, long secondaryOrderId, long securityId, long orderId,
        ulong execId, ulong transactTimeNanos, Matching.OrderType ordType, Matching.TimeInForce tif,
        long orderQty, long? priceMantissa, ulong receivedTimeNanos = UTCTimestampNullValue)
    {
        if (dst.Length < ExecReportNewTotal) throw new ArgumentException("buffer too small for ER_New", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)ExecReportNewTotal,
            ExecReportNewBlock, EntryPointFrameReader.TidExecutionReportNew, version: 6);
        var body = dst.Slice(HeaderSize, ExecReportNewBlock);
        body.Clear();
        WriteBusinessHeader(body, sessionId, msgSeqNum, sendingTimeNanos);
        body[18] = EncodeSide(side);
        body[19] = OrdStatusNew;
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdIdValue);
        MemoryMarshal.Write(body.Slice(28, 8), in secondaryOrderId);
        MemoryMarshal.Write(body.Slice(36, 8), in securityId);
        MemoryMarshal.Write(body.Slice(44, 8), in orderId);
        MemoryMarshal.Write(body.Slice(56, 8), in execId);
        MemoryMarshal.Write(body.Slice(64, 8), in transactTimeNanos);
        ulong nullTs = UTCTimestampNullValue;
        MemoryMarshal.Write(body.Slice(72, 8), in nullTs);                  // MarketSegmentReceivedTime
        long nullPx = PriceNullMantissa;
        MemoryMarshal.Write(body.Slice(80, 8), in nullPx);                  // ProtectionPrice
        body[92] = EncodeOrdType(ordType);
        body[93] = EncodeTif(tif);
        MemoryMarshal.Write(body.Slice(96, 8), in orderQty);
        long px = priceMantissa ?? PriceNullMantissa;
        MemoryMarshal.Write(body.Slice(104, 8), in px);
        MemoryMarshal.Write(body.Slice(112, 8), in nullPx);                 // StopPx
        // V3 trailing fields: receivedTime + non-zero null sentinels.
        MemoryMarshal.Write(body.Slice(144, 8), in receivedTimeNanos);      // ReceivedTime (tag 35544)
        // ordTagID@155 null=0 already, investorID@156 null=zeros already, strategyID@168 null=0 already.
        body[162] = 255;                                                    // CrossType null
        body[163] = 255;                                                    // CrossPrioritization null
        body[164] = 255;                                                    // MmProtectionReset null
        // V6 trailing field: tradingSubAccount@172 (uint, null=0) — covered
        // by body.Clear() above. strategyID@168 (int, null=0) ditto.
        return ExecReportNewTotal;
    }

    public static int EncodeExecReportModify(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        Matching.Side side, ulong clOrdIdValue, ulong origClOrdIdValue, long secondaryOrderId,
        long securityId, long orderId, ulong execId, ulong transactTimeNanos,
        long leavesQty, long cumQty, long orderQty, long priceMantissa,
        ulong receivedTimeNanos = UTCTimestampNullValue)
    {
        if (dst.Length < ExecReportModifyTotal) throw new ArgumentException("buffer too small for ER_Modify", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)ExecReportModifyTotal,
            ExecReportModifyBlock, EntryPointFrameReader.TidExecutionReportModify, version: 6);
        var body = dst.Slice(HeaderSize, ExecReportModifyBlock);
        body.Clear();
        WriteBusinessHeader(body, sessionId, msgSeqNum, sendingTimeNanos);
        body[18] = EncodeSide(side);
        body[19] = OrdStatusReplaced;
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdIdValue);
        MemoryMarshal.Write(body.Slice(28, 8), in secondaryOrderId);
        MemoryMarshal.Write(body.Slice(36, 8), in securityId);
        MemoryMarshal.Write(body.Slice(44, 8), in leavesQty);               // overlap SecExchange — write LeavesQty
        MemoryMarshal.Write(body.Slice(56, 8), in execId);
        MemoryMarshal.Write(body.Slice(64, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(72, 8), in cumQty);
        ulong nullTs = UTCTimestampNullValue;
        MemoryMarshal.Write(body.Slice(80, 8), in nullTs);                  // MarketSegmentReceivedTime
        MemoryMarshal.Write(body.Slice(88, 8), in orderId);
        MemoryMarshal.Write(body.Slice(96, 8), in origClOrdIdValue);
        long nullPx = PriceNullMantissa;
        MemoryMarshal.Write(body.Slice(104, 8), in nullPx);                 // ProtectionPrice
        body[116] = OrdTypeLimit;                                           // OrdType - replace can only be on limit
        body[117] = TifDay;                                                 // TimeInForce - replace inherits original; default Day
        MemoryMarshal.Write(body.Slice(120, 8), in orderQty);
        MemoryMarshal.Write(body.Slice(128, 8), in priceMantissa);
        MemoryMarshal.Write(body.Slice(136, 8), in nullPx);                 // StopPx
        // V6 trailing fields: receivedTime@160 + mmProtectionReset@178 +
        // execRestatementReason@179 (was strategyID@179 in V3 — V6 inserts
        // execRestatementReason here and shifts strategyID/tradingSubAccount).
        MemoryMarshal.Write(body.Slice(160, 8), in receivedTimeNanos);      // ReceivedTime (tag 35544)
        // ordTagID@171 null=0 already, investorID@172 null=zeros already.
        body[178] = 255;                                                    // MmProtectionReset null
        body[179] = 255;                                                    // ExecRestatementReason null
        // strategyID@180 (int, null=0) and tradingSubAccount@184 (uint,
        // null=0) covered by body.Clear() above.
        return ExecReportModifyTotal;
    }

    public static int EncodeExecReportCancel(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        Matching.Side side, ulong clOrdIdValue, ulong origClOrdIdValue, long secondaryOrderId,
        long securityId, long orderId, ulong execId, ulong transactTimeNanos,
        long cumQty, long orderQty, long? priceMantissa,
        ulong receivedTimeNanos = UTCTimestampNullValue)
    {
        if (dst.Length < ExecReportCancelTotal) throw new ArgumentException("buffer too small for ER_Cancel", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)ExecReportCancelTotal,
            ExecReportCancelBlock, EntryPointFrameReader.TidExecutionReportCancel, version: 6);
        var body = dst.Slice(HeaderSize, ExecReportCancelBlock);
        body.Clear();
        WriteBusinessHeader(body, sessionId, msgSeqNum, sendingTimeNanos);
        body[18] = EncodeSide(side);
        body[19] = OrdStatusCanceled;
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdIdValue);
        MemoryMarshal.Write(body.Slice(28, 8), in secondaryOrderId);
        MemoryMarshal.Write(body.Slice(36, 8), in securityId);
        MemoryMarshal.Write(body.Slice(44, 8), in cumQty);                  // overlap SecExchange
        MemoryMarshal.Write(body.Slice(56, 8), in execId);
        MemoryMarshal.Write(body.Slice(64, 8), in transactTimeNanos);
        ulong nullTs = UTCTimestampNullValue;
        MemoryMarshal.Write(body.Slice(72, 8), in nullTs);                  // MarketSegmentReceivedTime
        MemoryMarshal.Write(body.Slice(80, 8), in orderId);
        MemoryMarshal.Write(body.Slice(88, 8), in origClOrdIdValue);
        body[99] = 255;                                                     // ExecRestatementReason null
        body[112] = OrdTypeLimit;
        body[113] = TifDay;
        MemoryMarshal.Write(body.Slice(116, 8), in orderQty);
        long px = priceMantissa ?? PriceNullMantissa;
        MemoryMarshal.Write(body.Slice(124, 8), in px);                     // Price
        long nullPx = PriceNullMantissa;
        MemoryMarshal.Write(body.Slice(132, 8), in nullPx);                 // StopPx
        // V3 trailing fields: receivedTime@156 + ordTagID/investorID/strategyID/actionRequestedFromSessionID
        // null sentinels are all zero (handled by body.Clear() above).
        MemoryMarshal.Write(body.Slice(156, 8), in receivedTimeNanos);      // ReceivedTime (tag 35544)
        return ExecReportCancelTotal;
    }

    public static int EncodeExecReportTrade(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        Matching.Side side, ulong clOrdIdValue, long secondaryOrderId,
        long securityId, long orderId, long lastQty, long lastPxMantissa,
        ulong execId, ulong transactTimeNanos, long leavesQty, long cumQty,
        bool aggressor, uint tradeId, uint contraBroker, ushort tradeDate, long orderQty)
    {
        if (dst.Length < ExecReportTradeTotal) throw new ArgumentException("buffer too small for ER_Trade", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)ExecReportTradeTotal,
            ExecReportTradeBlock, EntryPointFrameReader.TidExecutionReportTrade, version: 6);
        var body = dst.Slice(HeaderSize, ExecReportTradeBlock);
        body.Clear();
        WriteBusinessHeader(body, sessionId, msgSeqNum, sendingTimeNanos);
        body[18] = EncodeSide(side);
        body[19] = leavesQty == 0 ? OrdStatusFilled : OrdStatusPartiallyFilled;
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdIdValue);
        MemoryMarshal.Write(body.Slice(28, 8), in secondaryOrderId);
        MemoryMarshal.Write(body.Slice(36, 8), in securityId);
        // body[44..48] = Account (overlap SecExchange) — leave 0 (null)
        MemoryMarshal.Write(body.Slice(48, 8), in lastQty);
        MemoryMarshal.Write(body.Slice(56, 8), in lastPxMantissa);
        MemoryMarshal.Write(body.Slice(64, 8), in execId);
        MemoryMarshal.Write(body.Slice(72, 8), in transactTimeNanos);
        MemoryMarshal.Write(body.Slice(80, 8), in leavesQty);
        MemoryMarshal.Write(body.Slice(88, 8), in cumQty);
        body[96] = aggressor ? (byte)1 : (byte)0;
        body[97] = ExecTypeTrade;
        MemoryMarshal.Write(body.Slice(100, 4), in tradeId);
        MemoryMarshal.Write(body.Slice(104, 4), in contraBroker);
        MemoryMarshal.Write(body.Slice(108, 8), in orderId);
        MemoryMarshal.Write(body.Slice(116, 2), in tradeDate);
        ushort crossedNull = 65535;
        MemoryMarshal.Write(body.Slice(144, 2), in crossedNull);            // CrossedIndicator null
        MemoryMarshal.Write(body.Slice(146, 8), in orderQty);
        // V6 trailing fields (offsets 154..174):
        body[154] = 255;                                                    // TradingSessionID null
        body[155] = 255;                                                    // TradingSessionSubID null
        body[156] = 255;                                                    // SecurityTradingStatus null
        body[157] = 255;                                                    // CrossType null
        body[158] = 255;                                                    // CrossPrioritization null
        // strategyID@160 (int, null=0), impliedEventID@164 (6 bytes; eventID
        // and noRelatedTrades both null=0) and tradingSubAccount@170 (uint,
        // null=0) are covered by body.Clear() above.
        return ExecReportTradeTotal;
    }

    public static int EncodeExecReportReject(Span<byte> dst,
        uint sessionId, uint msgSeqNum, ulong sendingTimeNanos,
        ulong clOrdIdValue, ulong origClOrdIdValue, long securityId, long orderIdOrZero,
        uint rejectReason, ulong transactTimeNanos,
        ulong receivedTimeNanos = UTCTimestampNullValue)
    {
        if (dst.Length < ExecReportRejectTotal) throw new ArgumentException("buffer too small for ER_Reject", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)ExecReportRejectTotal,
            ExecReportRejectBlock, EntryPointFrameReader.TidExecutionReportReject, version: 6);
        var body = dst.Slice(HeaderSize, ExecReportRejectBlock);
        body.Clear();
        WriteBusinessHeader(body, sessionId, msgSeqNum, sendingTimeNanos);
        body[18] = 0;                                                       // Side null
        body[19] = 0;                                                       // CxlRejResponseTo null (we use ER_Reject for new-order rejects too)
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdIdValue);
        // SecondaryOrderID@28 = 0 (null)
        MemoryMarshal.Write(body.Slice(36, 8), in securityId);
        // OrdRejReason: schema type RejReason is uint32 (#GAP-17 / #53).
        // Field overlaps SecurityExchange (offset 44) per the SBE schema;
        // writing the full 4 LE bytes leaves SecurityExchange unset, which
        // matches the engine's behaviour today (we never populate it).
        MemoryMarshal.Write(body.Slice(44, 4), in rejectReason);            // OrdRejReason (overlap SecExchange)
        MemoryMarshal.Write(body.Slice(48, 8), in transactTimeNanos);
        ulong zeroExecId = 0;
        MemoryMarshal.Write(body.Slice(56, 8), in zeroExecId);              // ExecID 0
        MemoryMarshal.Write(body.Slice(64, 8), in orderIdOrZero);           // OrderID
        MemoryMarshal.Write(body.Slice(72, 8), in origClOrdIdValue);
        // Account@80 = 0 (null), OrdType@84 = 0, TimeInForce@85 = 0,
        // ExpireDate@86, OrderQty@88 (null), Price@96, StopPx@104, ...
        long nullPx = PriceNullMantissa;
        MemoryMarshal.Write(body.Slice(96, 8), in nullPx);                  // Price null
        MemoryMarshal.Write(body.Slice(104, 8), in nullPx);                 // StopPx null
        ushort crossedNull = 65535;
        MemoryMarshal.Write(body.Slice(136, 2), in crossedNull);            // CrossedIndicator null
        // V6 trailing fields: receivedTime@138 (UTCTimestampNanosOptional,
        // 8-byte mantissa null=ulong.MaxValue; trailing precision/unit/epoch
        // bytes default to 0 and the SDK gates on the mantissa); ordTagID@149
        // (byte null=0), investorID@150 (6 bytes null=zeros), strategyID@156
        // (int null=0), tradingSubAccount@160 (uint null=0) — all covered
        // by body.Clear() above.
        MemoryMarshal.Write(body.Slice(138, 8), in receivedTimeNanos);      // ReceivedTime (tag 35544)
        return ExecReportRejectTotal;
    }
}
