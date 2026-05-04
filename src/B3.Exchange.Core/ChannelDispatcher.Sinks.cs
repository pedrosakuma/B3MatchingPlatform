using B3.Exchange.Matching;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// Sink facet of <see cref="ChannelDispatcher"/> (issue #168 split):
/// the <c>IMatchingEventSink</c> implementation. Engine-emitted events
/// are translated into UMDF MBO/Trade frames buffered into the per-command
/// packet via <see cref="ReserveOrFlush"/> + <see cref="Commit"/>, and
/// (where applicable) into per-session ExecutionReport callbacks routed
/// through <see cref="_outbound"/>. All callbacks run on the dispatch
/// thread (asserted via <see cref="AssertOnLoopThread"/>).
/// </summary>
public sealed partial class ChannelDispatcher
{
    public void OnOrderAccepted(in OrderAcceptedEvent e)
    {
        AssertOnLoopThread();
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.RemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
        Commit(n);

        if (_hasCurrentSession)
        {
            // Issue #167: register canonical order-state on the dispatch
            // thread (single writer) BEFORE emitting the ER so any passive
            // trade fired in the same dispatch turn can resolve owner ↦
            // session locally.
            _orders.Register(e.OrderId, _currentSession, _currentClOrdId, _currentFirm, e.Side, e.SecurityId);
            _outbound.WriteExecutionReportNew(_currentSession, _currentFirm, _currentClOrdId, e, _currentReceivedTimeNanos);
            _metrics?.IncExecutionReport(ExecutionReportKind.New);
        }
    }

    public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e)
    {
        AssertOnLoopThread();
        // Update on the wire = OrderAdded with action UPDATE (0x01).
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.NewRemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
        // Patch MdUpdateAction byte from NEW(0x00) to UPDATE(0x01).
        int actionOffset = B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBodyMdUpdateActionOffset;
        dst[actionOffset] = 0x01; // MDUpdateAction.CHANGE
        Commit(n);
    }

    public void OnOrderModified(in OrderModifiedEvent e)
    {
        AssertOnLoopThread();
        // Issue #251: priority-kept Replace acknowledgment. The UMDF
        // UPDATE frame was already buffered by OnOrderQuantityReduced
        // earlier in this same dispatch turn; here we only emit the
        // per-session ER_Modify back to the requester.
        if (!_hasCurrentSession) return;
        if (!_orders.TryResolve(e.OrderId, out var owner)) return;

        // ClOrdID = requester's new id; OrigClOrdID = owner's previous
        // ClOrdID (the one identifying the order before this Replace).
        ulong newClOrdId = _currentClOrdId;
        ulong origClOrdId = owner.ClOrdId;
        _outbound.WriteExecutionReportModify(_currentSession,
            e.SecurityId, e.OrderId,
            clOrdIdValue: newClOrdId, origClOrdIdValue: origClOrdId,
            side: e.Side, newPriceMantissa: e.NewPriceMantissa,
            newRemainingQty: e.NewRemainingQuantity,
            transactTimeNanos: e.TransactTimeNanos, rptSeq: e.RptSeq,
            receivedTimeNanos: _currentReceivedTimeNanos);
        _metrics?.IncExecutionReport(ExecutionReportKind.Replace);

        // Refresh the canonical (Firm, ClOrdID) → OrderId index so
        // subsequent Cancel/Modify by OrigClOrdID = newClOrdId resolve
        // to this order. Owner stays unchanged; only the ClOrdID
        // identity is rotated.
        if (newClOrdId != 0 && newClOrdId != origClOrdId)
            _orders.Reregister(e.OrderId, newClOrdId);
    }

    public void OnOrderBookSideEmpty(in OrderBookSideEmptyEvent e)
    {
        AssertOnLoopThread();
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.EmptyBookBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteEmptyBookFrame(
            dst, e.SecurityId, e.TransactTimeNanos);
        Commit(n);
    }

    public void OnOrderMassCanceled(in OrderMassCanceledEvent e)
    {
        AssertOnLoopThread();
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.MassDeleteOrdersBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteMassDeleteOrdersFrame(
            dst, e.SecurityId, entryType, e.RptSeq, e.TransactTimeNanos);
        Commit(n);
    }

    public void OnTradingPhaseChanged(in TradingPhaseChangedEvent e)
    {
        AssertOnLoopThread();
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SecurityStatusBlockLength);
        // tradingSessionID/securityTradingEvent unused for now (255 = NULL
        // for the optional event); tradeDate/tradSesOpenTime defaulted to 0.
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteSecurityStatusFrame(
            dst,
            securityId: e.SecurityId,
            tradingSessionId: 0,
            securityTradingStatus: (byte)e.Phase,
            securityTradingEvent: 255,
            tradeDate: 0,
            tradSesOpenTimeNanos: 0,
            transactTimeNanos: e.TransactTimeNanos,
            rptSeq: e.RptSeq);
        Commit(n);
    }

    public void OnAuctionTopChanged(in AuctionTopChangedEvent e)
    {
        AssertOnLoopThread();

        var topDst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.TheoreticalOpeningPriceBlockLength);
        int nTop = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTheoreticalOpeningPriceFrame(
            topDst,
            securityId: e.SecurityId,
            hasTop: e.HasTop,
            priceMantissa: e.TopPriceMantissa,
            quantity: e.TopQuantity,
            tradeDate: _tradeDate,
            mdEntryTimestampNanos: e.TransactTimeNanos,
            rptSeq: e.RptSeq);
        Commit(nTop);

        ushort cond = e.HasImbalance
            ? (e.ImbalanceSide == Side.Buy
                ? B3.Umdf.WireEncoder.UmdfWireEncoder.ImbalanceConditionMoreBuyers
                : B3.Umdf.WireEncoder.UmdfWireEncoder.ImbalanceConditionMoreSellers)
            : (ushort)0;
        var imbDst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.AuctionImbalanceBlockLength);
        int nImb = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteAuctionImbalanceFrame(
            imbDst,
            securityId: e.SecurityId,
            hasImbalance: e.HasImbalance,
            imbalanceCondition: cond,
            imbalanceQty: e.ImbalanceQuantity,
            mdEntryTimestampNanos: e.TransactTimeNanos,
            rptSeq: e.RptSeq);
        Commit(nImb);
    }

    /// <summary>
    /// Onda M4 / issue #231: emits a single <c>OpeningPrice_15</c> or
    /// <c>ClosingPrice_17</c> frame per uncross. The dispatcher injects
    /// the configured <c>_tradeDate</c> (kept off the matching event so
    /// the engine doesn't need calendar awareness), mirroring the
    /// pattern used by <see cref="OnAuctionTopChanged"/>.
    /// </summary>
    public void OnAuctionPrint(in AuctionPrintEvent e)
    {
        AssertOnLoopThread();

        if (e.Kind == AuctionPrintKind.Opening)
        {
            var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
                + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
                + B3.Umdf.WireEncoder.WireOffsets.OpeningPriceBlockLength);
            int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOpeningPriceFrame(
                dst,
                securityId: e.SecurityId,
                priceMantissa: e.PriceMantissa,
                tradeDate: _tradeDate,
                mdEntryTimestampNanos: e.TransactTimeNanos,
                rptSeq: e.RptSeq);
            Commit(n);
        }
        else
        {
            var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
                + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
                + B3.Umdf.WireEncoder.WireOffsets.ClosingPriceBlockLength);
            int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteClosingPriceFrame(
                dst,
                securityId: e.SecurityId,
                priceMantissa: e.PriceMantissa,
                tradeDate: _tradeDate,
                mdEntryTimestampNanos: e.TransactTimeNanos,
                rptSeq: e.RptSeq);
            Commit(n);
        }
    }

    public void OnOrderCanceled(in OrderCanceledEvent e)
    {
        AssertOnLoopThread();
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.RemainingQuantityAtCancel, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(n);

        // Issue #167: resolve owner locally on the dispatch thread, then
        // evict the canonical entry. Pass the active session's ClOrdId (if
        // any) so the wire ER carries the requester's id while
        // OrigClOrdID points to the owner's original ClOrdID.
        if (_orders.TryResolve(e.OrderId, out var owner))
        {
            _orders.Evict(e.OrderId);
            _outbound.WriteExecutionReportPassiveCancel(owner.Session, owner.ClOrdId, e.OrderId, e,
                _currentClOrdId, _currentReceivedTimeNanos);
            _metrics?.IncExecutionReport(ExecutionReportKind.CancelPassive);
        }
    }

    public void OnOrderFilled(in OrderFilledEvent e)
    {
        AssertOnLoopThread();
        // Fully consumed by trades — emit DeleteOrder; the per-fill ER_Trade
        // events were already dispatched via OnTrade.
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.FinalFilledQuantity, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(n);

        // Tell the canonical registry the order has reached terminal state
        // — no wire ER here (the per-trade ER_Trade frames have already
        // covered the fills).
        _orders.Evict(e.OrderId);
    }

    public void OnTrade(in TradeEvent e)
    {
        AssertOnLoopThread();
        // Issue #218 (Onda L · L5): when a cross sweep phase is active,
        // accumulate the aggressor's filled qty so the loop can decide how
        // much of the prioritized leg remains for the internal print.
        if (_crossSweepFilledQty.HasValue)
            _crossSweepFilledQty = _crossSweepFilledQty.Value + e.Quantity;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.TradeBlockLength);
        bool aggressorIsBuy = e.AggressorSide == Side.Buy;
        uint buyer = aggressorIsBuy ? e.AggressorFirm : e.RestingFirm;
        uint seller = aggressorIsBuy ? e.RestingFirm : e.AggressorFirm;
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTradeFrame(dst,
            e.SecurityId, e.PriceMantissa, e.Quantity, e.TradeId, _tradeDate, e.TransactTimeNanos, e.RptSeq,
            buyerFirm: buyer, sellerFirm: seller);
        Commit(n);

        // ER_Trade for the aggressor side: routed to the active session by
        // SessionId. We do not maintain per-aggressor cum/leaves tracking
        // here; integration tests are scope-limited to single-fill scenarios.
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportTrade(_currentSession, e, isAggressor: true,
                ownerOrderId: e.AggressorOrderId, clOrdIdValue: _currentClOrdId,
                leavesQty: 0, cumQty: e.Quantity);
            _metrics?.IncExecutionReport(ExecutionReportKind.Trade);
        }
        // ER_Trade for the resting side: resolve owner locally on the
        // dispatch thread (#167) and pass pre-resolved (session, clOrdId)
        // to the Gateway. If the owner has no live session entry the
        // Gateway drops at SessionRegistry.TryGet.
        if (_orders.TryResolve(e.RestingOrderId, out var owner))
        {
            _outbound.WriteExecutionReportPassiveTrade(owner.Session, owner.ClOrdId, e.RestingOrderId,
                e, leavesQty: 0, cumQty: e.Quantity);
            _metrics?.IncExecutionReport(ExecutionReportKind.TradePassive);
        }
    }

    public void OnReject(in B3.Exchange.Matching.RejectEvent e)
    {
        AssertOnLoopThread();
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportReject(_currentSession, e, _currentClOrdId);
            _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
        }
    }

    public void OnIcebergReplenished(in IcebergReplenishedEvent e)
    {
        AssertOnLoopThread();
        // #211: encode the replenish as a Delete + Add MBO frame pair for
        // the same OrderID. The order's canonical state (and therefore the
        // owning session/ClOrdID for ER routing) is intentionally NOT
        // touched: the order is logically the same; only its position in
        // the price-level queue changed. No ExecutionReport is emitted —
        // the per-trade ER_Trade frames already covered the fills that
        // exhausted the visible slice.
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;

        // 1. DeleteOrder for the consumed visible spot. Quantity is 0
        //    because the slice was fully traded away (it's a "removed by
        //    consumption" delete from the consumer's perspective).
        var del = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int dn = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(del,
            e.SecurityId, e.OrderId, entryType, 0L, e.DeleteRptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(dn);

        // 2. OrderAdded for the replenished slice at the back of the level.
        var add = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int an = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(add,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.NewVisibleQuantity,
            e.AddRptSeq, e.InsertTimestampNanos);
        Commit(an);
    }

    /// <summary>
    /// Issue #214: stop order accepted off-book. We register the canonical
    /// order ownership (so a later cancel or a triggered passive trade
    /// can resolve the owner) and emit ER_New back to the active session.
    /// No MBO frame is emitted because stops are not on the public book
    /// until they trigger. The wire ER reuses the regular OrderAccepted
    /// shape and therefore does not carry StopPx/OrdType=Stop on the
    /// wire — an MVP limitation tracked in the issue body.
    /// </summary>
    public void OnStopOrderAccepted(in StopOrderAcceptedEvent e)
    {
        AssertOnLoopThread();
        if (_hasCurrentSession)
        {
            _orders.Register(e.OrderId, _currentSession, _currentClOrdId, _currentFirm, e.Side, e.SecurityId);
            var accepted = new OrderAcceptedEvent(
                SecurityId: e.SecurityId,
                OrderId: e.OrderId,
                ClOrdId: e.ClOrdId,
                Side: e.Side,
                PriceMantissa: e.LimitPriceMantissa,
                RemainingQuantity: e.Quantity,
                EnteringFirm: e.EnteringFirm,
                InsertTimestampNanos: e.InsertTimestampNanos,
                RptSeq: e.RptSeq);
            _outbound.WriteExecutionReportNew(_currentSession, _currentFirm, _currentClOrdId, accepted, _currentReceivedTimeNanos);
            _metrics?.IncExecutionReport(ExecutionReportKind.New);
        }
    }

    /// <summary>
    /// Issue #214: untriggered stop canceled. Mirrors the passive-cancel
    /// path of <see cref="OnOrderCanceled"/> — resolve owner, emit
    /// ER_Cancel, evict ownership. No MBO frame (the stop never showed
    /// on the public book).
    /// </summary>
    public void OnStopOrderCanceled(in StopOrderCanceledEvent e)
    {
        AssertOnLoopThread();
        if (_orders.TryResolve(e.OrderId, out var owner))
        {
            _orders.Evict(e.OrderId);
            var canceled = new OrderCanceledEvent(
                SecurityId: e.SecurityId,
                OrderId: e.OrderId,
                Side: e.Side,
                PriceMantissa: e.StopPxMantissa,
                RemainingQuantityAtCancel: e.RemainingQuantityAtCancel,
                TransactTimeNanos: e.TransactTimeNanos,
                Reason: CancelReason.Client,
                RptSeq: e.RptSeq);
            _outbound.WriteExecutionReportPassiveCancel(owner.Session, owner.ClOrdId, e.OrderId, canceled,
                _currentClOrdId, _currentReceivedTimeNanos);
            _metrics?.IncExecutionReport(ExecutionReportKind.CancelPassive);
        }
    }

    /// <summary>
    /// Issue #214: stop trigger fired. The triggered execution that
    /// follows is routed through <see cref="OnTrade"/> /
    /// <see cref="OnOrderAccepted"/> just like a fresh aggressor, so
    /// nothing on the wire needs to be emitted here. Hook reserved for
    /// future telemetry / audit instrumentation.
    /// </summary>
    public void OnStopOrderTriggered(in StopOrderTriggeredEvent e)
    {
        AssertOnLoopThread();
    }
}
