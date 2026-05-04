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

        // Gateway resolves OrderId → owning session via OrderOwnershipMap
        // and evicts the entry. Pass the active session's ClOrdId (if any)
        // so the wire ER carries the requester's id while OrigClOrdID
        // points to the owner's original ClOrdID.
        _outbound.WriteExecutionReportPassiveCancel(e.OrderId, e, _currentClOrdId, _currentReceivedTimeNanos);
        _metrics?.IncExecutionReport(ExecutionReportKind.CancelPassive);
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

        // Tell the Gateway to release the ownership entry — no wire ER here
        // (the per-trade ER_Trade frames have already covered the fills).
        _outbound.NotifyOrderTerminal(e.OrderId);
    }

    public void OnTrade(in TradeEvent e)
    {
        AssertOnLoopThread();
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
        // ER_Trade for the resting side: Gateway resolves owner via the
        // OrderOwnershipMap.
        _outbound.WriteExecutionReportPassiveTrade(e.RestingOrderId, e, leavesQty: 0, cumQty: e.Quantity);
        _metrics?.IncExecutionReport(ExecutionReportKind.TradePassive);
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
}
