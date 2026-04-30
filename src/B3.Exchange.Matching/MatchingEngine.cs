using B3.Exchange.Instruments;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Matching;

/// <summary>
/// Single-threaded matching engine for one UMDF channel. Holds one
/// <see cref="LimitOrderBook"/> per instrument plus monotonic order/trade-id
/// allocators and an <see cref="RptSeq"/> that is incremented on every emitted
/// MBO/Trade event so the integration layer can stamp <c>RptSeq</c> on UMDF
/// frames without separate bookkeeping.
/// </summary>
public sealed class MatchingEngine
{
    private readonly Dictionary<long, InstrumentTradingRules> _rulesById;
    private readonly Dictionary<long, LimitOrderBook> _booksById;
    private readonly IMatchingEventSink _sink;
    private readonly ILogger<MatchingEngine> _logger;
    private readonly SelfTradePrevention _stp;

    // === Long-running stability audit (issue #4) ===
    //   _nextOrderId : long. ~9.22e18 max. At 1e9 orders/sec → 292 years.
    //                 Effectively non-overflowing for 24/7 operation.
    //   _nextTradeId : uint. ~4.29e9 max. At 100k trades/sec → ~12 hours.
    //                 The B3 UMDF wire schema's TradeID field is uint, so we
    //                 cannot widen here without a wire-format change. Wraps
    //                 to 0 silently; downstream consumers correlate trades
    //                 via (TradeID, TradeDate) so a wrap in the same trading
    //                 day would create ambiguity. Acceptable for the
    //                 simulator (sessions reset trade-day boundaries); flag
    //                 for re-evaluation if we ever target sustained
    //                 production-grade rates inside one trading day.
    //   _rptSeq      : uint. Same limits as _nextTradeId. Per-channel,
    //                 per-(SequenceVersion). The integration layer's
    //                 SequenceVersion bump on packet-seq overflow does NOT
    //                 reset _rptSeq — they are independent counters.
    private long _nextOrderId = 1;
    private uint _nextTradeId = 1;
    private uint _rptSeq;

    private bool _dispatching;

    public MatchingEngine(IEnumerable<Instrument> instruments, IMatchingEventSink sink,
        ILogger<MatchingEngine> logger,
        SelfTradePrevention selfTradePrevention = SelfTradePrevention.None)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);
        _sink = sink;
        _logger = logger;
        _stp = selfTradePrevention;
        _rulesById = new Dictionary<long, InstrumentTradingRules>();
        _booksById = new Dictionary<long, LimitOrderBook>();
        foreach (var i in instruments)
        {
            var rules = new InstrumentTradingRules(i);
            _rulesById.Add(i.SecurityId, rules);
            _booksById.Add(i.SecurityId, new LimitOrderBook(i.SecurityId));
        }
        _logger.LogInformation("matching engine initialized with {InstrumentCount} instruments", _rulesById.Count);
    }

    public uint CurrentRptSeq => _rptSeq;
    public long PeekNextOrderId => _nextOrderId;
    public SelfTradePrevention SelfTradePrevention => _stp;

    /// <summary>
    /// Hard reset of every per-instrument book and the engine's
    /// <c>RptSeq</c> counter. Designed for the operator-initiated
    /// channel-reset path (issue #6): the dispatcher invokes this on
    /// the dispatch thread, paired with a <c>ChannelReset_11</c> emission
    /// and a <c>SequenceVersion</c> bump on both the incremental and
    /// snapshot channels. Order-id and trade-id allocators are
    /// intentionally NOT reset — those identifiers must remain unique
    /// for the lifetime of the host so audit/replay tools can distinguish
    /// pre- and post-reset entities.
    /// </summary>
    public void ResetForChannelReset()
    {
        if (_dispatching)
            throw new InvalidOperationException("cannot reset while a command is being dispatched");
        foreach (var book in _booksById.Values) book.Clear();
        _rptSeq = 0;
    }

    /// <summary>
    /// Returns the <see cref="LimitOrderBook"/>'s public snapshot iterator.
    /// Throws <see cref="KeyNotFoundException"/> if the security id is unknown.
    /// </summary>
    public IEnumerable<RestingOrderView> EnumerateBook(long securityId, Side side)
        => _booksById[securityId].EnumerateOrders(side);

    public int OrderCount(long securityId) => _booksById[securityId].OrderCount;

    public void Submit(NewOrderCommand cmd)
    {
        EnterDispatch();
        try
        {
            if (!_rulesById.TryGetValue(cmd.SecurityId, out var rules))
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.UnknownInstrument, cmd.EnteredAtNanos);
                return;
            }

            // Quantity rules apply to all order types.
            if (cmd.Quantity <= 0) { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.QuantityNonPositive, cmd.EnteredAtNanos); return; }
            if (cmd.Quantity % rules.LotSize != 0) { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.QuantityNotMultipleOfLot, cmd.EnteredAtNanos); return; }

            if (cmd.Type == OrderType.Limit)
            {
                if (cmd.PriceMantissa <= 0) { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceNonPositive, cmd.EnteredAtNanos); return; }
                if (cmd.PriceMantissa < rules.MinPriceMantissa || cmd.PriceMantissa > rules.MaxPriceMantissa)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceOutOfBand, cmd.EnteredAtNanos); return; }
                if (cmd.PriceMantissa % rules.TickSizeMantissa != 0)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceNotOnTick, cmd.EnteredAtNanos); return; }
            }
            else // Market
            {
                if (cmd.Tif == TimeInForce.Day)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.MarketNotImmediateOrCancel, cmd.EnteredAtNanos); return; }
            }

            var book = _booksById[cmd.SecurityId];

            // FOK pre-check: walk fillable qty and reject without any side effect
            // if insufficient. Same crossing predicate as the actual match path.
            if (cmd.Tif == TimeInForce.FOK)
            {
                long fillable = book.FillableQuantityAgainst(cmd.Side, cmd.PriceMantissa, isMarket: cmd.Type == OrderType.Market);
                if (fillable < cmd.Quantity)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.FokUnfillable, cmd.EnteredAtNanos); return; }

                // If STP is enabled, FOK must check for self-trades: orders from the same firm
                // would be prevented from matching, so reject FOK as unfillable if any exist.
                if (_stp != SelfTradePrevention.None)
                {
                    bool isMarketOrder = cmd.Type == OrderType.Market;
                    foreach (var level in book.OppositeLevels(cmd.Side))
                    {
                        if (!isMarketOrder && !LimitOrderBook.PriceCrosses(cmd.Side, level.PriceMantissa, cmd.PriceMantissa))
                            break;

                        var order = level.Head;
                        while (order is not null)
                        {
                            if (order.EnteringFirm == cmd.EnteringFirm)
                            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.FokUnfillable, cmd.EnteredAtNanos); return; }
                            order = order.Next;
                        }
                    }
                }
            }

            // Market order with empty opposite book: reject early — we never want a
            // market order to be "accepted with no fills".
            if (cmd.Type == OrderType.Market && book.BestLevel(LimitOrderBook.Opposite(cmd.Side)) is null)
            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.MarketNoLiquidity, cmd.EnteredAtNanos); return; }

            ExecuteAggressor(cmd, rules, book);
        }
        finally { ExitDispatch(); }
    }

    public void Cancel(CancelOrderCommand cmd)
    {
        EnterDispatch();
        try
        {
            if (!_booksById.TryGetValue(cmd.SecurityId, out var book))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownInstrument, cmd.EnteredAtNanos); return; }
            if (!book.TryGet(cmd.OrderId, out var resting))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownOrderId, cmd.EnteredAtNanos); return; }

            EmitCanceled(book, resting, cmd.EnteredAtNanos, CancelReason.Client);
        }
        finally { ExitDispatch(); }
    }

    public void Replace(ReplaceOrderCommand cmd)
    {
        EnterDispatch();
        try
        {
            if (!_rulesById.TryGetValue(cmd.SecurityId, out var rules))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownInstrument, cmd.EnteredAtNanos); return; }
            var book = _booksById[cmd.SecurityId];
            if (!book.TryGet(cmd.OrderId, out var resting))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownOrderId, cmd.EnteredAtNanos); return; }

            // Validate new params *before* mutating.
            if (cmd.NewQuantity <= 0) { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.QuantityNonPositive, cmd.EnteredAtNanos); return; }
            if (cmd.NewQuantity % rules.LotSize != 0) { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.QuantityNotMultipleOfLot, cmd.EnteredAtNanos); return; }
            if (cmd.NewPriceMantissa <= 0) { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.PriceNonPositive, cmd.EnteredAtNanos); return; }
            if (cmd.NewPriceMantissa < rules.MinPriceMantissa || cmd.NewPriceMantissa > rules.MaxPriceMantissa)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.PriceOutOfBand, cmd.EnteredAtNanos); return; }
            if (cmd.NewPriceMantissa % rules.TickSizeMantissa != 0)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.PriceNotOnTick, cmd.EnteredAtNanos); return; }

            bool priorityKept = cmd.NewPriceMantissa == resting.PriceMantissa
                                && cmd.NewQuantity <= resting.RemainingQuantity;

            if (priorityKept)
            {
                // In-place quantity decrease: priority preserved, original
                // InsertTimestamp preserved → OrderQuantityReducedEvent (UPDATE).
                long delta = resting.RemainingQuantity - cmd.NewQuantity;
                resting.RemainingQuantity = cmd.NewQuantity;
                resting.Level!.TotalQuantity -= delta;
                _sink.OnOrderQuantityReduced(new OrderQuantityReducedEvent(
                    SecurityId: book.SecurityId,
                    OrderId: resting.OrderId,
                    Side: resting.Side,
                    PriceMantissa: resting.PriceMantissa,
                    NewRemainingQuantity: cmd.NewQuantity,
                    InsertTimestampNanos: resting.InsertTimestampNanos,
                    TransactTimeNanos: cmd.EnteredAtNanos,
                    RptSeq: NextRptSeq()));
                return;
            }

            // Priority lost: emit DEL of the resting order, then submit the
            // replacement as a fresh aggressor (which may cross or rest).
            var side = resting.Side;
            EmitCanceled(book, resting, cmd.EnteredAtNanos, CancelReason.ReplaceLostPriority);

            // Build a synthetic NewOrderCommand for the replacement and process
            // it through the normal aggressor path. TIF=Day so unfilled
            // remainder rests (this matches FIX OrderCancelReplace semantics).
            var replacement = new NewOrderCommand(
                ClOrdId: cmd.ClOrdId,
                SecurityId: cmd.SecurityId,
                Side: side,
                Type: OrderType.Limit,
                Tif: TimeInForce.Day,
                PriceMantissa: cmd.NewPriceMantissa,
                Quantity: cmd.NewQuantity,
                EnteringFirm: resting.EnteringFirm,
                EnteredAtNanos: cmd.EnteredAtNanos);
            ExecuteAggressor(replacement, rules, book);
        }
        finally { ExitDispatch(); }
    }

    private void ExecuteAggressor(NewOrderCommand cmd, InstrumentTradingRules rules, LimitOrderBook book)
    {
        long aggressorRemaining = cmd.Quantity;
        long aggressorOrderIdForTrades = _nextOrderId++;
        bool isMarket = cmd.Type == OrderType.Market;
        long limitPx = cmd.PriceMantissa;
        bool stpAggressorCanceled = false;

        // Walk opposite levels in priority order. We snapshot the level list to a
        // local copy (LimitOrderBook.OppositeLevels) so removing a level mid-iter
        // is safe.
        foreach (var level in book.OppositeLevels(cmd.Side))
        {
            if (aggressorRemaining == 0) break;
            if (stpAggressorCanceled) break;
            if (!isMarket && !LimitOrderBook.PriceCrosses(cmd.Side, level.PriceMantissa, limitPx)) break;

            // Within a level, consume FIFO from Head.
            var maker = level.Head;
            while (maker is not null && aggressorRemaining > 0)
            {
                var next = maker.Next; // capture before mutation

                // Self-trade prevention: maker and aggressor share EnteringFirm.
                if (_stp != SelfTradePrevention.None && maker.EnteringFirm == cmd.EnteringFirm)
                {
                    switch (_stp)
                    {
                        case SelfTradePrevention.CancelResting:
                            // Cancel the conflicting maker and continue matching
                            // the aggressor against the next maker / level.
                            EmitCanceled(book, maker, cmd.EnteredAtNanos, CancelReason.SelfTradePrevention);
                            maker = next;
                            continue;
                        case SelfTradePrevention.CancelBoth:
                            EmitCanceled(book, maker, cmd.EnteredAtNanos, CancelReason.SelfTradePrevention);
                            stpAggressorCanceled = true;
                            break;
                        case SelfTradePrevention.CancelAggressor:
                            stpAggressorCanceled = true;
                            break;
                    }
                    break;
                }

                long tradeQty = Math.Min(aggressorRemaining, maker.RemainingQuantity);
                long tradePx = maker.PriceMantissa;

                // Determine buyer/seller by side
                uint buyerFirm = cmd.Side == Side.Buy ? cmd.EnteringFirm : maker.EnteringFirm;
                uint sellerFirm = cmd.Side == Side.Buy ? maker.EnteringFirm : cmd.EnteringFirm;

                _sink.OnTrade(new TradeEvent(
                    SecurityId: book.SecurityId,
                    TradeId: _nextTradeId++,
                    PriceMantissa: tradePx,
                    Quantity: tradeQty,
                    AggressorSide: cmd.Side,
                    AggressorOrderId: aggressorOrderIdForTrades,
                    AggressorClOrdId: cmd.ClOrdId,
                    AggressorFirm: cmd.EnteringFirm,
                    RestingOrderId: maker.OrderId,
                    RestingFirm: maker.EnteringFirm,
                    TransactTimeNanos: cmd.EnteredAtNanos,
                    RptSeq: NextRptSeq()));

                aggressorRemaining -= tradeQty;
                maker.RemainingQuantity -= tradeQty;
                level.TotalQuantity -= tradeQty;

                if (maker.RemainingQuantity == 0)
                {
                    // Maker fully filled → remove + OnOrderFilled.
                    long finalFilled = tradeQty; // For simplicity OrderFilledEvent reports
                                                 // the LAST trade's qty; downstream cares
                                                 // only about the delete, not the fill total.
                    book.Remove(maker);
                    _sink.OnOrderFilled(new OrderFilledEvent(
                        SecurityId: book.SecurityId,
                        OrderId: maker.OrderId,
                        Side: maker.Side,
                        PriceMantissa: maker.PriceMantissa,
                        FinalFilledQuantity: finalFilled,
                        TransactTimeNanos: cmd.EnteredAtNanos,
                        RptSeq: NextRptSeq()));
                }
                else
                {
                    // Maker partially filled → emit OrderQuantityReduced (UPDATE).
                    _sink.OnOrderQuantityReduced(new OrderQuantityReducedEvent(
                        SecurityId: book.SecurityId,
                        OrderId: maker.OrderId,
                        Side: maker.Side,
                        PriceMantissa: maker.PriceMantissa,
                        NewRemainingQuantity: maker.RemainingQuantity,
                        InsertTimestampNanos: maker.InsertTimestampNanos,
                        TransactTimeNanos: cmd.EnteredAtNanos,
                        RptSeq: NextRptSeq()));
                }
                maker = next;
            }
        }

        // Aggressor has remainder?
        if (aggressorRemaining == 0) return;

        if (stpAggressorCanceled)
        {
            // STP canceled the aggressor's residual. The aggressor never rested
            // on the book, so no MBO event is emitted (mirrors the IOC-remainder
            // pattern). The originating session is informed via a Reject ER —
            // any trades already executed against other firms still stand.
            Reject(cmd.ClOrdId, cmd.SecurityId, aggressorOrderIdForTrades,
                RejectReason.SelfTradePrevention, cmd.EnteredAtNanos);
            return;
        }

        if (isMarket)
        {
            // Market remainder → no resting (already validated MarketNoLiquidity
            // upfront, but if the book emptied mid-walk we just stop here without
            // further events for the aggressor — there is no resting order to
            // cancel and we never emitted Accepted).
            return;
        }

        if (cmd.Tif == TimeInForce.IOC || cmd.Tif == TimeInForce.FOK)
        {
            // FOK reaches here only via the pre-check (impossible for it to
            // partially fill); IOC remainder is silently dropped from the book
            // perspective — no MBO event needed for an order that never rested.
            return;
        }

        // Day order with remainder rests on the book.
        var resting = new RestingOrder
        {
            OrderId = aggressorOrderIdForTrades,
            ClOrdId = cmd.ClOrdId,
            Side = cmd.Side,
            PriceMantissa = limitPx,
            EnteringFirm = cmd.EnteringFirm,
            InsertTimestampNanos = cmd.EnteredAtNanos,
            RemainingQuantity = aggressorRemaining,
        };
        book.Insert(resting);
        _sink.OnOrderAccepted(new OrderAcceptedEvent(
            SecurityId: book.SecurityId,
            OrderId: resting.OrderId,
            ClOrdId: cmd.ClOrdId,
            Side: resting.Side,
            PriceMantissa: resting.PriceMantissa,
            RemainingQuantity: resting.RemainingQuantity,
            EnteringFirm: resting.EnteringFirm,
            InsertTimestampNanos: resting.InsertTimestampNanos,
            RptSeq: NextRptSeq()));
    }

    private void EmitCanceled(LimitOrderBook book, RestingOrder o, ulong txn, CancelReason reason)
    {
        var side = o.Side;
        long px = o.PriceMantissa;
        long qty = o.RemainingQuantity;
        long oid = o.OrderId;
        book.Remove(o);
        _sink.OnOrderCanceled(new OrderCanceledEvent(
            SecurityId: book.SecurityId,
            OrderId: oid,
            Side: side,
            PriceMantissa: px,
            RemainingQuantityAtCancel: qty,
            TransactTimeNanos: txn,
            Reason: reason,
            RptSeq: NextRptSeq()));
    }

    private void Reject(string clOrdId, long securityId, long orderId, RejectReason r, ulong txn)
        => _sink.OnReject(new RejectEvent(clOrdId, securityId, orderId, r, txn));

    private uint NextRptSeq() => ++_rptSeq;

    private void EnterDispatch()
    {
        if (_dispatching)
            throw new InvalidOperationException("MatchingEngine called reentrantly from a sink callback. Sinks must not invoke engine methods.");
        _dispatching = true;
    }

    private void ExitDispatch() => _dispatching = false;
}
