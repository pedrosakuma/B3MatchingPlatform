namespace B3.Umdf.WireEncoder;

/// <summary>
/// Static helpers that collapse the Reserve→Encode→Commit triplet for each
/// UMDF message type emitted by <c>ChannelDispatcher</c>. Each method computes
/// the constant frame size, calls <see cref="IUmdfFrameSink.Reserve"/>, invokes
/// the corresponding <see cref="UmdfWireEncoder"/> method, then calls
/// <see cref="IUmdfFrameSink.Commit"/>. Wire bytes are unchanged.
/// </summary>
public static class UmdfFrameBuilder
{
    /// <summary>
    /// Official UMDF 2.2.0 <c>SecurityTradingStatus</c> value meaning the
    /// instrument is not available for trading (administrative halt).
    /// Issue #583: replaces the previous out-of-domain marker approach.
    /// </summary>
    public const byte SecurityTradingStatusForbidden = 18;

    /// <summary>
    /// Official UMDF 2.2.0 <c>SecurityTradingEvent</c> value meaning the
    /// instrument's status is being maintained separately from its
    /// security group's phase. Written when an instrument enters
    /// administrative halt (issue #583).
    /// </summary>
    public const byte SecurityTradingEventSecurityStatusChange = 101;

    /// <summary>
    /// Official UMDF 2.2.0 <c>SecurityTradingEvent</c> value meaning the
    /// instrument's status now follows its security group's phase again.
    /// Written when an instrument resumes from administrative halt,
    /// restoring the preserved pre-halt phase (issue #583).
    /// </summary>
    public const byte SecurityTradingEventSecurityRejoinsGroupStatus = 102;

    /// <summary>
    /// Writes an <c>Order_MBO_50</c> NEW frame (action=NEW).
    /// Used for <c>OnOrderAccepted</c> and the replenish-add half of
    /// <c>OnIcebergReplenished</c>.
    /// </summary>
    public static void WriteOrderAdded(
        IUmdfFrameSink sink,
        long securityId,
        long orderId,
        byte mdEntryType,
        long priceMantissa,
        long quantity,
        uint rptSeq,
        ulong insertTimestampNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OrderBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteOrderAddedFrame(
            dst, securityId, orderId, mdEntryType, priceMantissa, quantity, rptSeq, insertTimestampNanos);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes an <c>Order_MBO_50</c> UPDATE frame (action=CHANGE/0x01).
    /// Used for <c>OnOrderQuantityReduced</c>. The MdUpdateAction byte is
    /// patched inside this method; callers pass the same arguments as for
    /// <see cref="WriteOrderAdded"/>.
    /// </summary>
    public static void WriteOrderUpdate(
        IUmdfFrameSink sink,
        long securityId,
        long orderId,
        byte mdEntryType,
        long priceMantissa,
        long newRemainingQuantity,
        uint rptSeq,
        ulong insertTimestampNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OrderBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteOrderAddedFrame(
            dst, securityId, orderId, mdEntryType, priceMantissa, newRemainingQuantity, rptSeq, insertTimestampNanos);
        // Patch MdUpdateAction from NEW(0x00) to CHANGE(0x01).
        dst[WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OrderBodyMdUpdateActionOffset] = 0x01;
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>DeleteOrder_MBO_51</c> frame.
    /// Used for <c>OnOrderCanceled</c>, <c>OnOrderFilled</c>, and the
    /// delete half of <c>OnIcebergReplenished</c>.
    /// </summary>
    public static void WriteOrderDeleted(
        IUmdfFrameSink sink,
        long securityId,
        long orderId,
        byte mdEntryType,
        long size,
        uint rptSeq,
        ulong transactTimeNanos,
        long? priceMantissa = null)
    {
        const int frameSize = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.DeleteOrderBlockLength;
        var dst = sink.Reserve(frameSize);
        int n = UmdfWireEncoder.WriteOrderDeletedFrame(
            dst, securityId, orderId, mdEntryType, size, rptSeq, transactTimeNanos, priceMantissa);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes an <c>EmptyBook_9</c> frame. Used for <c>OnOrderBookSideEmpty</c>.
    /// </summary>
    public static void WriteOrderBookSideEmpty(
        IUmdfFrameSink sink,
        long securityId,
        ulong transactTimeNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.EmptyBookBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteEmptyBookFrame(dst, securityId, transactTimeNanos);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>MassDeleteOrders_MBO_52</c> frame. Used for
    /// <c>OnOrderMassCanceled</c>.
    /// </summary>
    public static void WriteOrderMassCanceled(
        IUmdfFrameSink sink,
        long securityId,
        byte mdEntryType,
        uint rptSeq,
        ulong transactTimeNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.MassDeleteOrdersBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteMassDeleteOrdersFrame(dst, securityId, mdEntryType, rptSeq, transactTimeNanos);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>SecurityStatus_3</c> frame for a trading-phase transition.
    /// <c>securityTradingEvent</c> is 255 (NULL). Used for
    /// <c>OnTradingPhaseChanged</c>.
    /// </summary>
    public static void WriteTradingPhaseChanged(
        IUmdfFrameSink sink,
        long securityId,
        byte securityTradingStatus,
        uint rptSeq,
        ulong transactTimeNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteSecurityStatusFrame(
            dst,
            securityId: securityId,
            tradingSessionId: 0,
            securityTradingStatus: securityTradingStatus,
            securityTradingEvent: 255,
            tradeDate: 0,
            tradSesOpenTimeNanos: 0,
            transactTimeNanos: transactTimeNanos,
            rptSeq: rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>SecurityStatus_3</c> frame for an instrument halt.
    /// <c>securityTradingStatus</c> is always
    /// <see cref="SecurityTradingStatusForbidden"/> (issue #583: the
    /// official "not available for trading" status, not the pre-halt
    /// phase) and <c>securityTradingEvent</c> is
    /// <see cref="SecurityTradingEventSecurityStatusChange"/>, marking
    /// that this instrument's status now diverges from its security
    /// group's phase. Used for <c>OnInstrumentHalted</c>.
    /// </summary>
    public static void WriteInstrumentHalted(
        IUmdfFrameSink sink,
        long securityId,
        uint rptSeq,
        ulong transactTimeNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteSecurityStatusFrame(
            dst,
            securityId: securityId,
            tradingSessionId: 0,
            securityTradingStatus: SecurityTradingStatusForbidden,
            securityTradingEvent: SecurityTradingEventSecurityStatusChange,
            tradeDate: 0,
            tradSesOpenTimeNanos: 0,
            transactTimeNanos: transactTimeNanos,
            rptSeq: rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>SecurityStatus_3</c> frame for an instrument resume.
    /// <paramref name="restoredSecurityTradingStatus"/> is the pre-halt
    /// phase the engine preserved while the instrument was halted, and
    /// <c>securityTradingEvent</c> is
    /// <see cref="SecurityTradingEventSecurityRejoinsGroupStatus"/>,
    /// marking that this instrument's status now follows its security
    /// group's phase again (issue #583). Used for
    /// <c>OnInstrumentResumed</c>.
    /// </summary>
    public static void WriteInstrumentResumed(
        IUmdfFrameSink sink,
        long securityId,
        byte restoredSecurityTradingStatus,
        uint rptSeq,
        ulong transactTimeNanos)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteSecurityStatusFrame(
            dst,
            securityId: securityId,
            tradingSessionId: 0,
            securityTradingStatus: restoredSecurityTradingStatus,
            securityTradingEvent: SecurityTradingEventSecurityRejoinsGroupStatus,
            tradeDate: 0,
            tradSesOpenTimeNanos: 0,
            transactTimeNanos: transactTimeNanos,
            rptSeq: rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>PriceBand_22</c> frame.
    /// </summary>
    public static void WritePriceBand(
        IUmdfFrameSink sink,
        long securityId,
        long lowLimitPriceMantissa,
        long highLimitPriceMantissa,
        ulong mdEntryTimestampNanos,
        uint rptSeq,
        byte priceBandType = (byte)B3.Umdf.Mbo.Sbe.V16.PriceBandType.HARD_LIMIT,
        byte priceLimitType = (byte)B3.Umdf.Mbo.Sbe.V16.PriceLimitType.PRICE_UNIT,
        long tradingReferencePriceMantissa = long.MinValue,
        byte priceBandMidpointPriceType = 255)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.PriceBandBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WritePriceBandFrame(
            dst,
            securityId,
            lowLimitPriceMantissa,
            highLimitPriceMantissa,
            mdEntryTimestampNanos,
            rptSeq,
            priceBandType,
            priceLimitType,
            tradingReferencePriceMantissa,
            priceBandMidpointPriceType);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>TheoreticalOpeningPrice_16</c> frame. Used for the first
    /// frame in <c>OnAuctionTopChanged</c>.
    /// </summary>
    public static void WriteTheoreticalOpeningPrice(
        IUmdfFrameSink sink,
        long securityId,
        bool hasTop,
        long priceMantissa,
        long quantity,
        ushort tradeDate,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.TheoreticalOpeningPriceBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteTheoreticalOpeningPriceFrame(
            dst, securityId, hasTop, priceMantissa, quantity, tradeDate, mdEntryTimestampNanos, rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes an <c>AuctionImbalance_19</c> frame. Used for the second
    /// frame in <c>OnAuctionTopChanged</c>.
    /// </summary>
    public static void WriteAuctionImbalance(
        IUmdfFrameSink sink,
        long securityId,
        bool hasImbalance,
        ushort imbalanceCondition,
        long imbalanceQty,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.AuctionImbalanceBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteAuctionImbalanceFrame(
            dst, securityId, hasImbalance, imbalanceCondition, imbalanceQty, mdEntryTimestampNanos, rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes an <c>OpeningPrice_15</c> frame. Used for the Opening branch
    /// of <c>OnAuctionPrint</c>.
    /// </summary>
    public static void WriteOpeningPrice(
        IUmdfFrameSink sink,
        long securityId,
        long priceMantissa,
        ushort tradeDate,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OpeningPriceBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteOpeningPriceFrame(
            dst, securityId, priceMantissa, tradeDate, mdEntryTimestampNanos, rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>ClosingPrice_17</c> frame. Used for the Closing branch
    /// of <c>OnAuctionPrint</c>.
    /// </summary>
    public static void WriteClosingPrice(
        IUmdfFrameSink sink,
        long securityId,
        long priceMantissa,
        ushort tradeDate,
        ulong mdEntryTimestampNanos,
        uint rptSeq)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.ClosingPriceBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteClosingPriceFrame(
            dst, securityId, priceMantissa, tradeDate, mdEntryTimestampNanos, rptSeq);
        sink.Commit(n);
    }

    /// <summary>
    /// Writes a <c>Trade_53</c> frame. Used for <c>OnTrade</c>.
    /// </summary>
    public static void WriteTrade(
        IUmdfFrameSink sink,
        long securityId,
        long priceMantissa,
        long quantity,
        uint tradeId,
        ushort tradeDate,
        ulong transactTimeNanos,
        uint rptSeq,
        uint? buyerFirm,
        uint? sellerFirm,
        bool isSweepTrade = false)
    {
        const int size = WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.TradeBlockLength;
        var dst = sink.Reserve(size);
        int n = UmdfWireEncoder.WriteTradeFrame(
            dst, securityId, priceMantissa, quantity, tradeId, tradeDate, transactTimeNanos, rptSeq,
            buyerFirm, sellerFirm, isSweepTrade);
        sink.Commit(n);
    }
}
