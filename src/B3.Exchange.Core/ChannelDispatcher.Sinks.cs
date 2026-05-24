using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging;
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
        UmdfFrameBuilder.WriteOrderAdded(FrameSink,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.RemainingQuantity, e.RptSeq, e.InsertTimestampNanos);

        if (_hasCurrentSession)
        {
            // Issue #167: register canonical order-state on the dispatch
            // thread (single writer) BEFORE emitting the ER so any passive
            // trade fired in the same dispatch turn can resolve owner ↦
            // session locally.
            // Issue #319: also stamp the aggressor's running cumQty into
            // the registry so post-resting passive fills continue from
            // where the (partial-fill-then-rest) aggressor left off
            // instead of resetting cum to 0. If the OrderId is already
            // registered (e.g. a parked stop that just triggered and
            // partially filled before resting), preserve its existing
            // tracking — the dispatcher's _aggressor* counters belong to
            // the outermost trade-causing command, not to the
            // re-executed stop.
            if (!_orders.TryResolve(e.OrderId, out _))
            {
                _orders.Register(e.OrderId, _currentSession, _currentClOrdId, _currentFirm, e.Side, e.SecurityId,
                    originalQty: _aggressorOrigQty > 0 ? _aggressorOrigQty : e.RemainingQuantity,
                    cumQty: _aggressorCumQty);
                IncrementOpenOrders(_currentFirm);
            }
            _outbound.WriteExecutionReportNew(_currentSession, _currentFirm, _currentClOrdId, e, _currentReceivedTimeNanos, CurrentDurability);
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
        UmdfFrameBuilder.WriteOrderUpdate(FrameSink,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.NewRemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
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
            receivedTimeNanos: _currentReceivedTimeNanos,
            durability: CurrentDurability);
        _metrics?.IncExecutionReport(ExecutionReportKind.Replace);

        // Refresh the canonical (Firm, ClOrdID) → OrderId index so
        // subsequent Cancel/Modify by OrigClOrdID = newClOrdId resolve
        // to this order. Owner stays unchanged; only the ClOrdID
        // identity is rotated.
        if (newClOrdId != 0 && newClOrdId != origClOrdId)
            _orders.Reregister(e.OrderId, newClOrdId);

        // Issue #319: a Replace can change the wire OrderQty. The new
        // OrderQty equals previously-executed (cumQty) plus the engine's
        // new remaining quantity; refresh the registry so subsequent
        // passive fills emit leavesQty against the right denominator.
        if (_orders.TryResolve(e.OrderId, out var refreshed))
            _orders.UpdateOriginalQty(e.OrderId, refreshed.CumQty + e.NewRemainingQuantity);
    }

    public void OnOrderBookSideEmpty(in OrderBookSideEmptyEvent e)
    {
        AssertOnLoopThread();
        UmdfFrameBuilder.WriteOrderBookSideEmpty(FrameSink, e.SecurityId, e.TransactTimeNanos);
    }

    public void OnOrderMassCanceled(in OrderMassCanceledEvent e)
    {
        AssertOnLoopThread();
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        UmdfFrameBuilder.WriteOrderMassCanceled(FrameSink, e.SecurityId, entryType, e.RptSeq, e.TransactTimeNanos);
    }

    public void OnTradingPhaseChanged(in TradingPhaseChangedEvent e)
    {
        AssertOnLoopThread();
        // Issue #321: maintain a thread-safe per-securityId phase snapshot
        // so the HTTP admin endpoint can decide between SetPhase and
        // UncrossAuction without piercing the engine off-thread.
        _phaseSnapshot[e.SecurityId] = e.Phase;
        // tradingSessionID/securityTradingEvent unused for now (255 = NULL
        // for the optional event); tradeDate/tradSesOpenTime defaulted to 0.
        UmdfFrameBuilder.WriteTradingPhaseChanged(FrameSink, e.SecurityId, (byte)e.Phase, e.RptSeq, e.TransactTimeNanos);
    }

    /// <summary>
    /// Issue #322: B3-aligned best-effort wire markers on
    /// <c>securityTradingEvent</c> for halt and resume. The downstream
    /// consumer just needs them to be distinct and non-NULL; the
    /// <c>SecurityStatus_3</c> frame's <c>securityTradingStatus</c> still
    /// carries the engine's preserved <see cref="TradingPhase"/> so the
    /// post-resume phase is unambiguous.
    /// </summary>

    public void OnInstrumentHalted(in InstrumentHaltedEvent e)
    {
        AssertOnLoopThread();
        byte phaseByte = _phaseSnapshot.TryGetValue(e.SecurityId, out var phase)
            ? (byte)phase
            : (byte)TradingPhase.Open;
        UmdfFrameBuilder.WriteInstrumentHalted(FrameSink, e.SecurityId, phaseByte, e.RptSeq, e.TransactTimeNanos);
    }

    public void OnInstrumentResumed(in InstrumentResumedEvent e)
    {
        AssertOnLoopThread();
        byte phaseByte = _phaseSnapshot.TryGetValue(e.SecurityId, out var phase)
            ? (byte)phase
            : (byte)TradingPhase.Open;
        UmdfFrameBuilder.WriteInstrumentResumed(FrameSink, e.SecurityId, phaseByte, e.RptSeq, e.TransactTimeNanos);
    }

    public void OnAuctionTopChanged(in AuctionTopChangedEvent e)
    {
        AssertOnLoopThread();

        UmdfFrameBuilder.WriteTheoreticalOpeningPrice(FrameSink,
            securityId: e.SecurityId,
            hasTop: e.HasTop,
            priceMantissa: e.TopPriceMantissa,
            quantity: e.TopQuantity,
            tradeDate: _tradeDate,
            mdEntryTimestampNanos: e.TransactTimeNanos,
            rptSeq: e.RptSeq);

        ushort cond = e.HasImbalance
            ? (e.ImbalanceSide == Side.Buy
                ? B3.Umdf.WireEncoder.UmdfWireEncoder.ImbalanceConditionMoreBuyers
                : B3.Umdf.WireEncoder.UmdfWireEncoder.ImbalanceConditionMoreSellers)
            : (ushort)0;
        UmdfFrameBuilder.WriteAuctionImbalance(FrameSink,
            securityId: e.SecurityId,
            hasImbalance: e.HasImbalance,
            imbalanceCondition: cond,
            imbalanceQty: e.ImbalanceQuantity,
            mdEntryTimestampNanos: e.TransactTimeNanos,
            rptSeq: e.RptSeq);
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

        // Issue #321: capture for the operator HTTP outcome. Only the
        // most recent print for the currently-dispatching command is
        // retained; reset to null at the start of each Process* method.
        _pendingAuctionPrint = new AuctionPrintInfo(e.Kind, e.PriceMantissa, e.ClearedQuantity);

        if (e.Kind == AuctionPrintKind.Opening)
        {
            UmdfFrameBuilder.WriteOpeningPrice(FrameSink,
                securityId: e.SecurityId,
                priceMantissa: e.PriceMantissa,
                tradeDate: _tradeDate,
                mdEntryTimestampNanos: e.TransactTimeNanos,
                rptSeq: e.RptSeq);
        }
        else
        {
            UmdfFrameBuilder.WriteClosingPrice(FrameSink,
                securityId: e.SecurityId,
                priceMantissa: e.PriceMantissa,
                tradeDate: _tradeDate,
                mdEntryTimestampNanos: e.TransactTimeNanos,
                rptSeq: e.RptSeq);
        }
    }

    public void OnOrderCanceled(in OrderCanceledEvent e)
    {
        AssertOnLoopThread();

        // Issue #357: an IOC/FOK aggressor that found zero crossing
        // liquidity is cancelled by the engine to give the originating
        // session a definitive terminal ER. The order never rested on
        // the public book, so we skip the MBO DeleteOrder frame and
        // route the ER directly via the active command context (the
        // requester is the aggressor; no registry entry to evict).
        if (e.Reason == CancelReason.IocUnmatched)
        {
            if (_hasCurrentSession)
            {
                _outbound.WriteExecutionReportPassiveCancel(_currentSession, _currentClOrdId, e.OrderId, e,
                    _currentClOrdId, _currentReceivedTimeNanos, CurrentDurability);
                _metrics?.IncExecutionReport(ExecutionReportKind.CancelPassive);
            }
            return;
        }

        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        UmdfFrameBuilder.WriteOrderDeleted(FrameSink,
            e.SecurityId, e.OrderId, entryType, e.RemainingQuantityAtCancel, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);

        // Issue #167: resolve owner locally on the dispatch thread, then
        // evict the canonical entry. Pass the active session's ClOrdId (if
        // any) so the wire ER carries the requester's id while
        // OrigClOrdID points to the owner's original ClOrdID.
        if (_orders.TryEvict(e.OrderId, out var owner))
        {
            DecrementOpenOrders(owner.Firm);
            _outbound.WriteExecutionReportPassiveCancel(owner.Session, owner.ClOrdId, e.OrderId, e,
                _currentClOrdId, _currentReceivedTimeNanos, CurrentDurability);
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
        UmdfFrameBuilder.WriteOrderDeleted(FrameSink,
            e.SecurityId, e.OrderId, entryType, e.FinalFilledQuantity, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);

        // Tell the canonical registry the order has reached terminal state
        // — no wire ER here (the per-trade ER_Trade frames have already
        // covered the fills).
        if (_orders.TryEvict(e.OrderId, out var owner))
            DecrementOpenOrders(owner.Firm);
    }

    public void OnTrade(in TradeEvent e)
    {
        AssertOnLoopThread();
        _lastTradePriceBySecurity[e.SecurityId] = e.PriceMantissa;
        // Issue #218 (Onda L · L5): when a cross sweep phase is active,
        // accumulate the aggressor's filled qty so the loop can decide how
        // much of the prioritized leg remains for the internal print.
        if (_crossSweepFilledQty.HasValue)
            _crossSweepFilledQty = _crossSweepFilledQty.Value + e.Quantity;
        bool aggressorIsBuy = e.AggressorSide == Side.Buy;
        uint buyer = aggressorIsBuy ? e.AggressorFirm : e.RestingFirm;
        uint seller = aggressorIsBuy ? e.RestingFirm : e.AggressorFirm;
        UmdfFrameBuilder.WriteTrade(FrameSink,
            e.SecurityId, e.PriceMantissa, e.Quantity, e.TradeId, _tradeDate, e.TransactTimeNanos, e.RptSeq,
            buyerFirm: buyer, sellerFirm: seller);

        // ER_Trade for the aggressor side: routed to the active session by
        // SessionId. Issue #319: cumQty/leavesQty are accumulated across
        // every fill of the outermost command's aggressor (set by
        // BeginAggressor in the dispatch loop) so multi-fill scenarios
        // emit monotonically-cumulative wire values per the FIX/B3
        // contract. _aggressorOrigQty == 0 means the aggressor was a
        // re-entrant submit the dispatcher did not stamp (e.g. a
        // triggered stop the engine rolled internally) — fall back to
        // the per-trade quantity rather than a misleading negative
        // leaves.
        if (_hasCurrentSession)
        {
            _aggressorCumQty += e.Quantity;
            long aggCum = _aggressorCumQty;
            long aggLeaves = _aggressorOrigQty > 0
                ? Math.Max(0, _aggressorOrigQty - aggCum)
                : 0;
            _outbound.WriteExecutionReportTrade(_currentSession, e, isAggressor: true,
                ownerOrderId: e.AggressorOrderId, clOrdIdValue: _currentClOrdId,
                leavesQty: aggLeaves, cumQty: aggCum, durability: CurrentDurability);
            _metrics?.IncExecutionReport(ExecutionReportKind.Trade);
        }
        // ER_Trade for the resting side: resolve owner locally on the
        // dispatch thread (#167) and pass pre-resolved (session, clOrdId)
        // to the Gateway. If the owner has no live session entry the
        // Gateway drops at SessionRegistry.TryGet.
        // Issue #319: per-order cum/leaves tracking lives in the
        // registry so multi-fill resting orders emit monotonic
        // (cumQty, leavesQty) and the final fill carries leaves=0.
        ulong restClOrdIdForAudit = 0UL;
        if (_orders.TryResolve(e.RestingOrderId, out var owner))
        {
            // Snapshot the resting ClOrdId here so the audit record below
            // sees the same owner the passive ER did, even if the owning
            // session is concurrently evicted by HostRouter.OnSessionClosed
            // before we build the audit record (the registry is backed by
            // ConcurrentDictionary, so this is observable).
            restClOrdIdForAudit = owner.ClOrdId;
            long restCum, restLeaves;
            if (!_orders.OnTrade(e.RestingOrderId, e.Quantity, out restCum, out restLeaves))
            {
                restCum = e.Quantity;
                restLeaves = 0;
            }
            _outbound.WriteExecutionReportPassiveTrade(owner.Session, owner.ClOrdId, e.RestingOrderId,
                e, leavesQty: restLeaves, cumQty: restCum, durability: CurrentDurability);
            _metrics?.IncExecutionReport(ExecutionReportKind.TradePassive);
        }

        // #329 PR-1: emit the per-trade audit record after both ER writes so
        // the audit log's order matches the wire-published trade order.
        //
        // Aggressor ClOrdId resolution policy: prefer the registry lookup
        // on AggressorOrderId because operator/auction work runs without a
        // bound session (HasSession=false → _currentClOrdId is stale), yet
        // both legs of the cross are real registered orders. Fall back to
        // _currentClOrdId only when the aggressor side is not in the
        // registry (e.g. a transient cross/internal print the dispatcher
        // never registered). Resting ClOrdId is taken from the snapshot
        // captured above to avoid the close-race second-lookup window.
        ulong aggClOrdIdForAudit = _orders.TryResolve(e.AggressorOrderId, out var aggOwner)
            ? aggOwner.ClOrdId
            : (_hasCurrentSession ? _currentClOrdId : 0UL);
        bool aggressorIsBuyForAudit = e.AggressorSide == Side.Buy;
        var record = new B3.Exchange.PostTrade.PostTradeRecord(
            TradeId: e.TradeId,
            TransactTimeNanos: e.TransactTimeNanos,
            SecurityId: e.SecurityId,
            AggressorSide: e.AggressorSide,
            Quantity: e.Quantity,
            PriceMantissa: e.PriceMantissa,
            BuyClOrdId: aggressorIsBuyForAudit ? aggClOrdIdForAudit : restClOrdIdForAudit,
            SellClOrdId: aggressorIsBuyForAudit ? restClOrdIdForAudit : aggClOrdIdForAudit,
            BuyFirm: aggressorIsBuyForAudit ? e.AggressorFirm : e.RestingFirm,
            SellFirm: aggressorIsBuyForAudit ? e.RestingFirm : e.AggressorFirm,
            BuyOrderId: aggressorIsBuyForAudit ? e.AggressorOrderId : e.RestingOrderId,
            SellOrderId: aggressorIsBuyForAudit ? e.RestingOrderId : e.AggressorOrderId);
        try
        {
            // Issue #329 PR-5: during WAL replay, skip trades whose owning
            // command was already fsync'd to the audit log pre-crash. The
            // sidecar-persisted watermark was captured into
            // _bootAuditDurableSeq at the start of ReplayWalOnLoopThread
            // (the live path leaves it at long.MaxValue so this branch
            // collapses outside replay).
            if (_replayMode && _lastAppliedSeq <= _bootAuditDurableSeq)
            {
                _metrics?.IncAuditReplaySkipped();
            }
            else
            {
                _postTradeSink.OnTrade(record);
            }
        }
        catch (Exception ex)
        {
            // Audit-sink exceptions must not poison the dispatch loop. The
            // durability watermark (PR-4) will surface persistent failures
            // by refusing to advance auditDurableSeq, which in turn blocks
            // WAL truncation — the operationally correct backpressure.
            _logger.LogError(ex, "post-trade sink threw on channel {Channel} tradeId={TradeId}; record dropped",
                ChannelNumber, e.TradeId);
        }
    }

    public void OnReject(in B3.Exchange.Matching.RejectEvent e)
    {
        AssertOnLoopThread();
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportReject(_currentSession, e, _currentClOrdId, CurrentDurability);
            _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
        }
    }

    private void IncrementOpenOrders(uint firm)
    {
        int next = _openOrdersByFirm.TryGetValue(firm, out int current) ? current + 1 : 1;
        _openOrdersByFirm[firm] = next;
        _openOrderMetrics?.Set(ChannelNumber, firm, next);
    }

    private void DecrementOpenOrders(uint firm)
    {
        if (!_openOrdersByFirm.TryGetValue(firm, out int current) || current <= 0)
            return;
        int next = current - 1;
        if (next == 0)
            _openOrdersByFirm.Remove(firm);
        else
            _openOrdersByFirm[firm] = next;
        _openOrderMetrics?.Set(ChannelNumber, firm, next);
    }

    private void ClearOpenOrderCounts()
    {
        if (_openOrdersByFirm.Count == 0) return;
        foreach (uint firm in _openOrdersByFirm.Keys.ToArray())
            _openOrderMetrics?.Set(ChannelNumber, firm, 0);
        _openOrdersByFirm.Clear();
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
        UmdfFrameBuilder.WriteOrderDeleted(FrameSink,
            e.SecurityId, e.OrderId, entryType, 0L, e.DeleteRptSeq, e.TransactTimeNanos, e.PriceMantissa);

        // 2. OrderAdded for the replenished slice at the back of the level.
        UmdfFrameBuilder.WriteOrderAdded(FrameSink,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.NewVisibleQuantity,
            e.AddRptSeq, e.InsertTimestampNanos);
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
            _orders.Register(e.OrderId, _currentSession, _currentClOrdId, _currentFirm, e.Side, e.SecurityId,
                originalQty: e.Quantity);
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
            _outbound.WriteExecutionReportNew(_currentSession, _currentFirm, _currentClOrdId, accepted, _currentReceivedTimeNanos, CurrentDurability);
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
        if (_orders.TryEvict(e.OrderId, out var owner))
        {
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
                _currentClOrdId, _currentReceivedTimeNanos, CurrentDurability);
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
