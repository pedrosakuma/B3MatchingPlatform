using B3.Exchange.Instruments;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Matching;

/// <summary>
/// Single-threaded matching engine for one UMDF channel. Holds one
/// <see cref="LimitOrderBook"/> per instrument plus monotonic order/trade-id
/// allocators and an <see cref="RptSeq"/> that is incremented on every emitted
/// MBO/Trade event so the integration layer can stamp <c>RptSeq</c> on UMDF
/// frames without separate bookkeeping.
///
/// <para>
/// Threading invariant (issue #169): every public mutation/read entry point
/// (<see cref="Submit"/>, <see cref="Cancel"/>, <see cref="Replace"/>,
/// <see cref="MassCancel"/>, <see cref="AllocateNextRptSeq"/>,
/// <see cref="ResetForChannelReset"/>) plus the <see cref="IMatchingEventSink"/>
/// callbacks they trigger MUST run on a single owner thread. The
/// <c>ChannelDispatcher</c> binds its loop thread via
/// <see cref="BindToDispatchThread"/> on startup. In DEBUG builds, a
/// per-call assert fires if any caller violates this. Any cross-thread
/// invocation must be marshalled via <c>ChannelDispatcher.EnqueueXxx</c>.
/// </para>
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

    // Per-instrument trading phase (gap-functional §5 / issue #201).
    // Defaults to Open for every loaded instrument so existing tests and
    // operators that never call SetTradingPhase keep observing the
    // continuous-trading behaviour.
    private readonly Dictionary<long, TradingPhase> _phaseById;

    private bool _dispatching;

    // Single-thread invariant (issue #169). Latched on first call to any
    // mutation/read entry point (or eagerly via BindToDispatchThread) so
    // future call-site regressions are caught at test time. Production
    // callers (ChannelDispatcher) bind explicitly so even the very first
    // engine call is checked. Unit tests that exercise the engine on the
    // xUnit thread without binding are tolerated via lazy latching.
    private Thread? _ownerThread;

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
        _phaseById = new Dictionary<long, TradingPhase>();
        foreach (var i in instruments)
        {
            var rules = new InstrumentTradingRules(i);
            _rulesById.Add(i.SecurityId, rules);
            _booksById.Add(i.SecurityId, new LimitOrderBook(i.SecurityId));
            _phaseById.Add(i.SecurityId, TradingPhase.Open);
        }
        _logger.LogInformation("matching engine initialized with {InstrumentCount} instruments", _rulesById.Count);
    }

    public uint CurrentRptSeq => _rptSeq;
    public long PeekNextOrderId => _nextOrderId;
    public SelfTradePrevention SelfTradePrevention => _stp;

    /// <summary>
    /// Allocates and returns the next <c>RptSeq</c> value without emitting
    /// any matching event. Designed for the operator-triggered trade-bust
    /// replay path (issue #15) where the dispatcher synthesises a
    /// <c>TradeBust_57</c> frame outside the engine but must keep the
    /// channel's <c>RptSeq</c> sequence dense. May only be invoked from the
    /// dispatch thread; throws if called while the engine is mid-dispatch
    /// (i.e. from inside a sink callback).
    /// </summary>
    public uint AllocateNextRptSeq()
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("AllocateNextRptSeq called from inside a sink callback. Operator commands must not interleave with engine dispatch.");
        return ++_rptSeq;
    }

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
        AssertOnOwnerThread();
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

    /// <summary>
    /// Current trading phase for the supplied security. Throws
    /// <see cref="KeyNotFoundException"/> if the security is unknown.
    /// </summary>
    public TradingPhase GetTradingPhase(long securityId) => _phaseById[securityId];

    /// <summary>
    /// Operator-issued trading-phase transition for a single instrument
    /// (gap-functional §5 / #201). Idempotent: a no-op transition emits no
    /// event. Otherwise the engine emits a <see cref="TradingPhaseChangedEvent"/>
    /// (consuming one <c>RptSeq</c>) which the integration layer translates
    /// into a UMDF <c>SecurityStatus_3</c> frame. Must be invoked from the
    /// dispatch thread; cannot run mid-dispatch.
    /// </summary>
    public bool SetTradingPhase(long securityId, TradingPhase phase, ulong txnNanos)
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("SetTradingPhase called from inside a sink callback. Operator commands must not interleave with engine dispatch.");
        if (!_phaseById.TryGetValue(securityId, out var current))
            throw new KeyNotFoundException($"unknown securityId {securityId}");
        if (current == phase) return false;
        _phaseById[securityId] = phase;
        _sink.OnTradingPhaseChanged(new TradingPhaseChangedEvent(
            SecurityId: securityId,
            Phase: phase,
            TransactTimeNanos: txnNanos,
            RptSeq: ++_rptSeq));
        return true;
    }

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

            // #201: trading-phase gating combined with #202 TIF semantics.
            // Day/IOC/FOK/GTC require Open. AtClose requires FinalClosingCall.
            // GoodForAuction requires Reserved (pre-open auction). GTD is
            // wire-accepted but not yet implementable in the engine — the
            // ExpireDate is not plumbed through NewOrderCommand yet.
            var phase = _phaseById[cmd.SecurityId];
            if (cmd.Tif == TimeInForce.Gtd)
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.TimeInForceNotSupported, cmd.EnteredAtNanos);
                return;
            }
            var requiredPhase = cmd.Tif switch
            {
                TimeInForce.AtClose => TradingPhase.FinalClosingCall,
                TimeInForce.GoodForAuction => TradingPhase.Reserved,
                _ => TradingPhase.Open,
            };
            if (phase != requiredPhase)
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.MarketClosed, cmd.EnteredAtNanos);
                return;
            }

            // Quantity rules apply to all order types.
            if (cmd.Quantity <= 0) { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.QuantityNonPositive, cmd.EnteredAtNanos); return; }
            if (cmd.Quantity % rules.LotSize != 0) { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.QuantityNotMultipleOfLot, cmd.EnteredAtNanos); return; }

            // #203 (MinQty subset): MinQty must be in (0, Quantity]. Zero
            // means "no minimum-fill constraint" and is the legacy path.
            if (cmd.MinQty != 0 && (long)cmd.MinQty > cmd.Quantity)
            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }

            // #211: iceberg validation. MaxFloor in (0, Quantity], multiple
            // of lot, and only meaningful for resting limit orders. Market
            // / IOC / FOK / Gtd / AtClose / GoodForAuction would never let
            // hidden qty replenish, so reject them.
            if (cmd.MaxFloor != 0)
            {
                if ((long)cmd.MaxFloor > cmd.Quantity
                    || (long)cmd.MaxFloor % rules.LotSize != 0)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
                if (cmd.Type != OrderType.Limit
                    || (cmd.Tif != TimeInForce.Day && cmd.Tif != TimeInForce.Gtc))
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
            }

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
                // Market orders must be IOC or FOK. Day/Gtc/Gtd/AtClose/GoodForAuction
                // are all resting TIFs that can't apply to a market order.
                if (cmd.Tif != TimeInForce.IOC && cmd.Tif != TimeInForce.FOK)
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

            // #203 (MinQty subset): pre-check immediately fillable quantity
            // against the opposite side. Same crossing predicate as FOK but
            // the threshold is MinQty instead of full Quantity. Any residual
            // (Quantity - filled) is allowed to rest if TIF permits. STP
            // self-trade exclusion is intentionally not factored in here:
            // MinQty is a venue-level liquidity guard expressed against the
            // book, and a separate STP cancel will surface to the client
            // through the normal aggressor path.
            if (cmd.MinQty != 0)
            {
                long fillable = book.FillableQuantityAgainst(cmd.Side, cmd.PriceMantissa, isMarket: cmd.Type == OrderType.Market);
                if (fillable < (long)cmd.MinQty)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.MinQtyNotMet, cmd.EnteredAtNanos); return; }
            }

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

    /// <summary>
    /// Mass-cancel the supplied resting orderIds (spec §4.8 / #GAP-19).
    /// Each resolved order emits one <see cref="OrderCanceledEvent"/> with
    /// <see cref="CancelReason.MassCancel"/>; orderIds that no longer
    /// resolve (already filled / cancelled in the same dispatch turn)
    /// are skipped silently. Returns the number of orders actually
    /// cancelled. Caller (the channel dispatcher) is expected to have
    /// already filtered by session / firm / side / asset using its
    /// <c>OrderOwnership</c> map, so the engine sees only orderIds.
    /// </summary>
    public int MassCancel(IReadOnlyCollection<long> orderIds, ulong enteredAtNanos)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count == 0) return 0;
        EnterDispatch();
        try
        {
            // First pass — group target orderIds by (SecurityId, Side) so we
            // can emit one OrderMassCanceledEvent summary per group BEFORE
            // the per-order cancels (UMDF MassDeleteOrders_MBO_52, gap #8).
            // Orders that no longer resolve are silently dropped, matching
            // the second-pass behaviour below. We use a small dictionary
            // since the typical mass-cancel touches O(10) groups.
            Dictionary<(long securityId, Side side), int>? groups = null;
            foreach (var orderId in orderIds)
            {
                foreach (var book in _booksById.Values)
                {
                    if (book.TryGet(orderId, out var resting))
                    {
                        groups ??= new Dictionary<(long, Side), int>();
                        var key = (book.SecurityId, resting.Side);
                        groups[key] = groups.TryGetValue(key, out var c) ? c + 1 : 1;
                        break;
                    }
                }
            }
            if (groups is not null)
            {
                foreach (var kv in groups)
                {
                    _sink.OnOrderMassCanceled(new OrderMassCanceledEvent(
                        SecurityId: kv.Key.securityId,
                        Side: kv.Key.side,
                        CancelledCount: kv.Value,
                        TransactTimeNanos: enteredAtNanos,
                        RptSeq: NextRptSeq()));
                }
            }

            int cancelled = 0;
            foreach (var orderId in orderIds)
            {
                // Locate the book that owns this orderId. Mass-cancel runs
                // in O(books × orderIds) which is acceptable since a typical
                // mass-cancel touches a small number of resting orders
                // within one channel.
                foreach (var book in _booksById.Values)
                {
                    if (book.TryGet(orderId, out var resting))
                    {
                        EmitCanceled(book, resting, enteredAtNanos, CancelReason.MassCancel);
                        cancelled++;
                        break;
                    }
                }
            }
            return cancelled;
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

            // #204: effective Type/TIF — null means "preserve original".
            // The resting order is by construction Limit (Market never rests),
            // so the only meaningful Type override is Limit -> Market, which
            // turns the priority-loss path into a market aggressor.
            var effectiveType = cmd.NewOrdType ?? OrderType.Limit;
            var effectiveTif = cmd.NewTif ?? resting.Tif;

            // Phase gating — same rules as a brand-new order. A replace that
            // lands during a trading halt must be rejected before mutating
            // the resting order; if the new TIF is incompatible with the
            // current phase the replace is treated like a new order would be.
            var phase = _phaseById[cmd.SecurityId];
            if (effectiveTif == TimeInForce.Gtd)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.TimeInForceNotSupported, cmd.EnteredAtNanos); return; }
            var requiredPhase = effectiveTif switch
            {
                TimeInForce.AtClose => TradingPhase.FinalClosingCall,
                TimeInForce.GoodForAuction => TradingPhase.Reserved,
                _ => TradingPhase.Open,
            };
            if (phase != requiredPhase)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.MarketClosed, cmd.EnteredAtNanos); return; }

            // Market replace must be IOC/FOK (cannot rest); price is ignored
            // in the aggressor path but we still validate it for Limit.
            if (effectiveType == OrderType.Market)
            {
                if (effectiveTif != TimeInForce.IOC && effectiveTif != TimeInForce.FOK)
                { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.MarketNotImmediateOrCancel, cmd.EnteredAtNanos); return; }
            }
            else
            {
                if (cmd.NewPriceMantissa <= 0) { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.PriceNonPositive, cmd.EnteredAtNanos); return; }
                if (cmd.NewPriceMantissa < rules.MinPriceMantissa || cmd.NewPriceMantissa > rules.MaxPriceMantissa)
                { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.PriceOutOfBand, cmd.EnteredAtNanos); return; }
                if (cmd.NewPriceMantissa % rules.TickSizeMantissa != 0)
                { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.PriceNotOnTick, cmd.EnteredAtNanos); return; }
            }

            // Priority-keep is only possible if Type/TIF are unchanged: a Type
            // or TIF transition is by definition a logical re-entry (the order
            // semantics differ), so we must DEL+NEW.
            bool priorityKept = effectiveType == OrderType.Limit
                                && effectiveTif == resting.Tif
                                && cmd.NewPriceMantissa == resting.PriceMantissa
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
            // it through the normal aggressor path. Type/TIF come from the
            // explicit replace overrides (or the resting order's originals
            // when omitted by the caller). Issue #204.
            var replacement = new NewOrderCommand(
                ClOrdId: cmd.ClOrdId,
                SecurityId: cmd.SecurityId,
                Side: side,
                Type: effectiveType,
                Tif: effectiveTif,
                PriceMantissa: effectiveType == OrderType.Market ? 0L : cmd.NewPriceMantissa,
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
                    // #211: iceberg replenish — if the maker has hidden
                    // reserve, the visible slice was just exhausted but
                    // the order is not yet fully filled. Take a fresh
                    // visible slice from the hidden reserve, re-insert
                    // at the BACK of the same price level (time-priority
                    // loss), and emit IcebergReplenishedEvent so the
                    // sink can flush a paired Delete + Add MBO frame
                    // for the same OrderID. The aggressor does NOT
                    // re-cross the replenished slice in the same dispatch
                    // (loop iterates via the pre-captured `next`), which
                    // matches the spec's "loses time priority" guarantee.
                    if (maker.HiddenQuantity > 0)
                    {
                        long newVisible = Math.Min(maker.MaxFloor, maker.HiddenQuantity);
                        long newHidden = maker.HiddenQuantity - newVisible;
                        var icebergSide = maker.Side;
                        long icebergPx = maker.PriceMantissa;
                        long icebergOid = maker.OrderId;
                        // Atomically: remove from current spot, mutate
                        // visible/hidden counters, re-insert at tail of
                        // same level. The book.Remove + book.Insert pair
                        // is correct even if this is the only order at
                        // the level (Insert recreates the level).
                        book.Remove(maker);
                        maker.RemainingQuantity = newVisible;
                        maker.HiddenQuantity = newHidden;
                        // Reset insert timestamp to the trade time — the
                        // replenished slice is logically a fresh entry at
                        // the back of the queue.
                        var refreshed = new RestingOrder
                        {
                            OrderId = icebergOid,
                            ClOrdId = maker.ClOrdId,
                            Side = icebergSide,
                            PriceMantissa = icebergPx,
                            EnteringFirm = maker.EnteringFirm,
                            InsertTimestampNanos = cmd.EnteredAtNanos,
                            RemainingQuantity = newVisible,
                            Tif = maker.Tif,
                            MaxFloor = maker.MaxFloor,
                            HiddenQuantity = newHidden,
                        };
                        book.Insert(refreshed);
                        uint deleteSeq = NextRptSeq();
                        uint addSeq = NextRptSeq();
                        _sink.OnIcebergReplenished(new IcebergReplenishedEvent(
                            SecurityId: book.SecurityId,
                            OrderId: icebergOid,
                            Side: icebergSide,
                            PriceMantissa: icebergPx,
                            NewVisibleQuantity: newVisible,
                            RemainingHiddenQuantity: newHidden,
                            InsertTimestampNanos: cmd.EnteredAtNanos,
                            TransactTimeNanos: cmd.EnteredAtNanos,
                            DeleteRptSeq: deleteSeq,
                            AddRptSeq: addSeq));
                        maker = next;
                        continue;
                    }

                    // Maker fully filled → remove + OnOrderFilled.
                    long finalFilled = tradeQty; // For simplicity OrderFilledEvent reports
                                                 // the LAST trade's qty; downstream cares
                                                 // only about the delete, not the fill total.
                    var makerSide = maker.Side;
                    book.Remove(maker);
                    _sink.OnOrderFilled(new OrderFilledEvent(
                        SecurityId: book.SecurityId,
                        OrderId: maker.OrderId,
                        Side: makerSide,
                        PriceMantissa: maker.PriceMantissa,
                        FinalFilledQuantity: finalFilled,
                        TransactTimeNanos: cmd.EnteredAtNanos,
                        RptSeq: NextRptSeq()));
                    // #200: emit EmptyBook_9 if this fill drained the side.
                    if (book.BestLevel(makerSide) is null)
                    {
                        _sink.OnOrderBookSideEmpty(new OrderBookSideEmptyEvent(
                            SecurityId: book.SecurityId,
                            Side: makerSide,
                            TransactTimeNanos: cmd.EnteredAtNanos));
                    }
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

        // Day/GTC order with remainder rests on the book.
        // Day/GTC order with remainder rests on the book. For iceberg
        // orders (#211), expose only the visible slice on the book and
        // hold the rest as HiddenQuantity for replenish on consumption.
        long visible = aggressorRemaining;
        long hidden = 0;
        if (cmd.MaxFloor != 0 && (long)cmd.MaxFloor < aggressorRemaining)
        {
            visible = (long)cmd.MaxFloor;
            hidden = aggressorRemaining - visible;
        }
        var resting = new RestingOrder
        {
            OrderId = aggressorOrderIdForTrades,
            ClOrdId = cmd.ClOrdId,
            Side = cmd.Side,
            PriceMantissa = limitPx,
            EnteringFirm = cmd.EnteringFirm,
            InsertTimestampNanos = cmd.EnteredAtNanos,
            RemainingQuantity = visible,
            Tif = cmd.Tif,
            MaxFloor = (long)cmd.MaxFloor,
            HiddenQuantity = hidden,
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
        // #200: when this cancel emptied the side, publish EmptyBook_9 so
        // recovery consumers can drop their per-side state without waiting
        // for the next snapshot rotation.
        if (book.BestLevel(side) is null)
        {
            _sink.OnOrderBookSideEmpty(new OrderBookSideEmptyEvent(
                SecurityId: book.SecurityId,
                Side: side,
                TransactTimeNanos: txn));
        }
    }

    private void Reject(string clOrdId, long securityId, long orderId, RejectReason r, ulong txn)
        => _sink.OnReject(new RejectEvent(clOrdId, securityId, orderId, r, txn));

    private uint NextRptSeq() => ++_rptSeq;

    private void EnterDispatch()
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("MatchingEngine called reentrantly from a sink callback. Sinks must not invoke engine methods.");
        _dispatching = true;
    }

    private void ExitDispatch() => _dispatching = false;

    /// <summary>
    /// Eagerly binds the engine to the dispatch-loop thread. Called by
    /// <c>ChannelDispatcher</c> on entry to its run loop so that any
    /// off-thread engine call is caught on the very first invocation in
    /// DEBUG builds. Subsequent calls with the same thread are no-ops;
    /// calls from a different thread fail the assert.
    /// Issue #169 (single-thread invariant audit).
    /// </summary>
    public void BindToDispatchThread(Thread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var prior = Interlocked.CompareExchange(ref _ownerThread, thread, null);
        System.Diagnostics.Debug.Assert(
            prior == null || prior == thread,
            $"MatchingEngine already bound to a different thread "
            + $"(existing={prior?.ManagedThreadId}, new={thread.ManagedThreadId})");
    }

    /// <summary>
    /// Asserts the calling thread owns the engine. Latches the owner
    /// thread on first call if no explicit binding was made (so unit
    /// tests that drive the engine directly remain consistent across
    /// their lifetime). Compiled out in Release.
    /// Issue #169 (single-thread invariant).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOnOwnerThread()
    {
        var current = Thread.CurrentThread;
        var owner = Interlocked.CompareExchange(ref _ownerThread, current, null);
        System.Diagnostics.Debug.Assert(
            owner == null || owner == current,
            $"MatchingEngine entered off the owner thread "
            + $"(owner={owner?.ManagedThreadId}, "
            + $"actual={current.ManagedThreadId})");
    }
}
