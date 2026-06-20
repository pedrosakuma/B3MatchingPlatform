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

    // Issue #214: per-instrument stop-order book. Stops are parked
    // off-book waiting for a trigger trade. Buy stops fire when the
    // last trade price is >= StopPx; sell stops fire when last trade
    // price is <= StopPx. Triggered stops are routed via the normal
    // matching path (Market for StopLoss, Limit for StopLimit) reusing
    // the original OrderId so client correlation survives the trigger.
    // The lookup-by-orderId map supports cancel of an untriggered stop.
    private readonly Dictionary<long, List<RestingStop>> _stopsBySymbol;
    private readonly Dictionary<long, RestingStop> _stopById;

    // Issue #229 (Onda M · M2): per-instrument indicative auction state.
    // Used by RecomputeAuctionTopIfApplicable to throttle UMDF
    // TheoreticalOpeningPrice_16 / AuctionImbalance_19 emission so an
    // accumulation event that does not change the indicative state does
    // not produce duplicate frames. Entries are seeded lazily; the
    // sentinel "no prior state" is the all-default record.
    private readonly Dictionary<long, AuctionTopState> _auctionTopById = new();

    // Reusable scratch buffers for ComputeAuctionTop (round-2 perf #15).
    // The engine is single-threaded and ComputeAuctionTop is invoked
    // strictly synchronously inside an EnterDispatch/ExitDispatch
    // bracket, so a single shared set of buffers is safe and
    // eliminates per-invocation List/SortedSet allocations on the
    // auction hot path.
    private readonly List<(long Px, long Qty)> _auctionBidsScratch = new();
    private readonly List<(long Px, long Qty)> _auctionAsksScratch = new();
    private readonly List<long> _auctionCandidatesScratch = new();

    // Reusable scratch buffer for MassCancel single-pass resolution
    // (round-2 perf #8). Holds the (book, resting) pairs resolved on
    // the first pass so the second pass — which emits the per-order
    // cancel events — can iterate without re-scanning every book.
    private readonly List<(LimitOrderBook Book, RestingOrder Resting)> _massCancelResolved = new();

    private readonly record struct AuctionTopState(
        bool HasTop,
        long TopPriceMantissa,
        long TopQuantity,
        bool HasImbalance,
        Side ImbalanceSide,
        long ImbalanceQuantity);

    private bool _dispatching;
    private byte[] _currentMemo = [];

    // Issue #322: per-instrument administrative-halt overlay. While an
    // entry is present here, Submit and Replace are short-circuited
    // with RejectReason.InstrumentHalted before any other validation.
    // Cancel paths are intentionally NOT gated so participants can pull
    // resting orders during a halt (consistent with B3 behaviour). The
    // engine's TradingPhase is preserved across halt/resume so resuming
    // restores trading without losing the book.
    private readonly Dictionary<long, HaltState> _haltById = new();

    // HaltState lives at namespace scope — see HaltState.cs.


    // Single-thread invariant (issue #169, #384). Latched on first call to
    // any mutation/read entry point (or eagerly via BindToDispatchThread)
    // so future call-site regressions are caught at test time. Production
    // callers (ChannelDispatcher) bind explicitly so even the very first
    // engine call is checked. Unit tests that exercise the engine on the
    // xUnit thread without binding are tolerated via lazy latching.
    private readonly Threading.SingleWriterGuard _writerGuard = new("MatchingEngine");

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
        _stopsBySymbol = new Dictionary<long, List<RestingStop>>();
        _stopById = new Dictionary<long, RestingStop>();
        foreach (var i in instruments)
        {
            var rules = new InstrumentTradingRules(i);
            _rulesById.Add(i.SecurityId, rules);
            _booksById.Add(i.SecurityId, new LimitOrderBook(i.SecurityId));
            _phaseById.Add(i.SecurityId, TradingPhase.Open);
            _stopsBySymbol.Add(i.SecurityId, new List<RestingStop>());
        }
        _logger.LogInformation("matching engine initialized with {InstrumentCount} instruments", _rulesById.Count);
    }

    public uint CurrentRptSeq => _rptSeq;
    public long PeekNextOrderId => _nextOrderId;
    public SelfTradePrevention SelfTradePrevention => _stp;

    /// <summary>
    /// Captures the engine's resumable state for persistence (issue #260).
    /// Counters, per-instrument trading phase and every resting order in
    /// every book are included; transient artefacts (auction-top
    /// throttling, dispatch flags, stop-order book) are intentionally
    /// omitted — see <see cref="EngineStateSnapshot"/> remarks.
    ///
    /// <para>Must be invoked from the dispatch thread (matching the
    /// engine's single-thread invariant); cannot run mid-dispatch.</para>
    /// </summary>
    public EngineStateSnapshot CaptureState()
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("CaptureState called from inside a sink callback. Snapshots must not interleave with engine dispatch.");
        var phases = new List<EngineStateSnapshot.PhaseEntry>(_phaseById.Count);
        foreach (var kv in _phaseById)
            phases.Add(new EngineStateSnapshot.PhaseEntry(kv.Key, kv.Value));
        var books = new List<EngineStateSnapshot.BookSnapshot>(_booksById.Count);
        foreach (var kv in _booksById)
        {
            var orders = kv.Value.EnumerateAllOrdersForSnapshot().ToList();
            books.Add(new EngineStateSnapshot.BookSnapshot(kv.Key, orders));
        }
        // Issue #262: untriggered stops are persisted so a restart no
        // longer silently loses parked StopLoss/StopLimit orders.
        // Iterate _stopsBySymbol (instead of _stopById) so the per-symbol
        // arrival order is preserved on restore — that order is observable
        // when multiple stops trigger off the same trade and otherwise
        // would scramble across restarts.
        var stops = new List<RestingStopRecord>(_stopById.Count);
        foreach (var kv in _stopsBySymbol)
        {
            foreach (var s in kv.Value)
            {
                stops.Add(new RestingStopRecord(
                    OrderId: s.OrderId,
                    ClOrdId: s.ClOrdId,
                    SecurityId: s.SecurityId,
                    Side: s.Side,
                    StopType: s.StopType,
                    Tif: s.Tif,
                    StopPxMantissa: s.StopPxMantissa,
                    LimitPriceMantissa: s.LimitPriceMantissa,
                    Quantity: s.Quantity,
                    EnteringFirm: s.EnteringFirm,
                    EnteredAtNanos: s.EnteredAtNanos,
                    OrdTagId: s.OrdTagId,
                    InvestorId: s.InvestorId)
                { Memo = s.Memo });
            }
        }
        // Issue #322: capture per-instrument administrative halts so the
        // overlay survives a restart. Empty list is collapsed to null at
        // the codec layer to keep encode→decode→encode byte-stable for
        // hosts that never halted anything.
        List<EngineStateSnapshot.HaltEntry>? halts = null;
        if (_haltById.Count > 0)
        {
            halts = new List<EngineStateSnapshot.HaltEntry>(_haltById.Count);
            foreach (var kv in _haltById)
                halts.Add(new EngineStateSnapshot.HaltEntry(kv.Key, (byte)kv.Value.Reason, kv.Value.HaltedAtNanos, kv.Value.Note));
        }
        return new EngineStateSnapshot(_nextOrderId, _nextTradeId, _rptSeq, phases, books, stops, halts);
    }

    /// <summary>
    /// Restores engine state previously produced by <see cref="CaptureState"/>
    /// (issue #260). The engine MUST be freshly constructed (all books
    /// empty, counters at their defaults) — restore refuses to overwrite a
    /// non-empty state to avoid silently merging an old snapshot on top of
    /// live state. Phases and books are matched by SecurityId; unknown
    /// SecurityIds in the snapshot are ignored with a warning so an
    /// instrument removed from the channel config does not break boot.
    /// </summary>
    public void RestoreState(EngineStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("cannot restore state while a command is being dispatched");
        if (_nextOrderId != 1 || _nextTradeId != 1 || _rptSeq != 0)
            throw new InvalidOperationException("RestoreState requires a freshly constructed engine");
        foreach (var book in _booksById.Values)
        {
            if (book.OrderCount != 0)
                throw new InvalidOperationException("RestoreState requires empty books on the target engine");
        }
        if (_stopById.Count != 0)
            throw new InvalidOperationException("RestoreState requires no parked stops on the target engine");
        if (_haltById.Count != 0)
            throw new InvalidOperationException("RestoreState requires no halt overlay on the target engine");

        _nextOrderId = snapshot.NextOrderId;
        _nextTradeId = snapshot.NextTradeId;
        _rptSeq = snapshot.RptSeq;
        foreach (var p in snapshot.Phases)
        {
            if (_phaseById.ContainsKey(p.SecurityId))
                _phaseById[p.SecurityId] = p.Phase;
            else
                _logger.LogWarning("RestoreState: ignoring phase entry for unknown securityId {SecurityId}", p.SecurityId);
        }
        int restored = 0;
        foreach (var b in snapshot.Books)
        {
            if (!_booksById.TryGetValue(b.SecurityId, out var book))
            {
                _logger.LogWarning("RestoreState: ignoring book for unknown securityId {SecurityId} ({OrderCount} orders dropped)",
                    b.SecurityId, b.Orders.Count);
                continue;
            }
            foreach (var o in b.Orders)
            {
                book.RestoreOrder(o);
                restored++;
            }
        }
        // Issue #262: rebuild parked stops. Snapshots predating #262 carry
        // null Stops, treated as empty so older state files keep loading.
        int restoredStops = 0;
        if (snapshot.Stops is { } stopRecords)
        {
            foreach (var s in stopRecords)
            {
                if (!_stopsBySymbol.TryGetValue(s.SecurityId, out var bucket))
                {
                    _logger.LogWarning("RestoreState: ignoring stop orderId {OrderId} for unknown securityId {SecurityId}", s.OrderId, s.SecurityId);
                    continue;
                }
                if (_stopById.ContainsKey(s.OrderId))
                    throw new InvalidOperationException($"RestoreState: duplicate stop orderId {s.OrderId}");
                var stop = new RestingStop
                {
                    OrderId = s.OrderId,
                    ClOrdId = s.ClOrdId,
                    Side = s.Side,
                    StopType = s.StopType,
                    Tif = s.Tif,
                    StopPxMantissa = s.StopPxMantissa,
                    LimitPriceMantissa = s.LimitPriceMantissa,
                    Quantity = s.Quantity,
                    EnteringFirm = s.EnteringFirm,
                    EnteredAtNanos = s.EnteredAtNanos,
                    SecurityId = s.SecurityId,
                    Memo = s.Memo,
                    OrdTagId = s.OrdTagId,
                    InvestorId = s.InvestorId,
                };
                bucket.Add(stop);
                _stopById.Add(s.OrderId, stop);
                restoredStops++;
            }
        }
        // Issue #322: rebuild administrative-halt overlay. Snapshots
        // predating #322 carry null Halts, treated as empty so older
        // state files keep loading.
        if (snapshot.Halts is { } haltRecords)
        {
            foreach (var h in haltRecords)
            {
                if (!_phaseById.ContainsKey(h.SecurityId))
                {
                    _logger.LogWarning("RestoreState: ignoring halt entry for unknown securityId {SecurityId}", h.SecurityId);
                    continue;
                }
                _haltById[h.SecurityId] = new HaltState((HaltReason)h.Reason, h.HaltedAtNanos, h.Note);
            }
        }
        _logger.LogInformation("RestoreState complete: nextOrderId={NextOrderId} nextTradeId={NextTradeId} rptSeq={RptSeq} restingOrders={RestingOrders} restingStops={RestingStops}",
            _nextOrderId, _nextTradeId, _rptSeq, restored, restoredStops);
    }

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
        foreach (var list in _stopsBySymbol.Values) list.Clear();
        _stopById.Clear();
        _haltById.Clear();
        _rptSeq = 0;
    }

    /// <summary>
    /// Returns the <see cref="LimitOrderBook"/>'s public snapshot iterator.
    /// Throws <see cref="KeyNotFoundException"/> if the security id is unknown.
    /// </summary>
    public IEnumerable<RestingOrderView> EnumerateBook(long securityId, Side side)
        => _booksById[securityId].EnumerateOrders(side);

    public int OrderCount(long securityId) => _booksById[securityId].OrderCount;

    public IReadOnlyList<(long SecurityId, int Count)> OrderCountsSnapshot()
    {
        AssertOnOwnerThread();
        var result = new List<(long SecurityId, int Count)>(_booksById.Count);
        foreach (var kv in _booksById)
            result.Add((kv.Key, kv.Value.OrderCount));
        return result;
    }

    /// <summary>
    /// Total number of untriggered stop orders parked across every
    /// instrument. Issue #262 — exposes a counter the persistence tests
    /// and HTTP /metrics endpoint can read without snapshotting state.
    /// Read on the engine's owner thread.
    /// </summary>
    public int StopOrderCount
    {
        get { AssertOnOwnerThread(); return _stopById.Count; }
    }

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
        return ApplyTradingPhase(securityId, phase, txnNanos);
    }

    /// <summary>
    /// Internal phase transition used by both <see cref="SetTradingPhase"/>
    /// and <see cref="UncrossAuction"/>. Skips the dispatch-guard checks
    /// because the caller already validated entry conditions; emits one
    /// <see cref="TradingPhaseChangedEvent"/> if the new phase differs from
    /// the current.
    /// </summary>
    private bool ApplyTradingPhase(long securityId, TradingPhase phase, ulong txnNanos)
    {
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

    /// <summary>
    /// <summary>
    /// Issue #322: places <paramref name="securityId"/> into administrative
    /// halt. Idempotent — returns <c>true</c> if the call flipped the
    /// instrument from "not halted" to "halted" (and emitted an
    /// <see cref="InstrumentHaltedEvent"/>); returns <c>false</c> if the
    /// instrument was already halted (no event emitted, no state change).
    /// While halted, Submit and Replace are rejected with
    /// <see cref="RejectReason.InstrumentHalted"/>; Cancel is allowed.
    /// Throws <see cref="KeyNotFoundException"/> if the security is
    /// unknown to this channel. Must run on the dispatch thread; not
    /// re-entrant from inside a sink callback.
    /// </summary>
    public bool HaltInstrument(long securityId, HaltReason reason, string? note, ulong txnNanos)
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("HaltInstrument called from inside a sink callback. Operator commands must not interleave with engine dispatch.");
        if (!_phaseById.ContainsKey(securityId))
            throw new KeyNotFoundException($"unknown securityId {securityId}");
        if (_haltById.ContainsKey(securityId)) return false;
        _haltById[securityId] = new HaltState(reason, txnNanos, note);
        _sink.OnInstrumentHalted(new InstrumentHaltedEvent(
            SecurityId: securityId,
            Reason: reason,
            Note: note,
            TransactTimeNanos: txnNanos,
            RptSeq: ++_rptSeq));
        return true;
    }

    /// <summary>
    /// Issue #322: resumes <paramref name="securityId"/> from administrative
    /// halt. Idempotent — returns <c>true</c> if the call flipped the
    /// instrument from halted to not-halted (and emitted an
    /// <see cref="InstrumentResumedEvent"/>); returns <c>false</c> if the
    /// instrument was not halted (no event emitted, no state change).
    /// Throws <see cref="KeyNotFoundException"/> if the security is
    /// unknown to this channel.
    /// </summary>
    public bool ResumeInstrument(long securityId, ulong txnNanos)
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("ResumeInstrument called from inside a sink callback. Operator commands must not interleave with engine dispatch.");
        if (!_phaseById.ContainsKey(securityId))
            throw new KeyNotFoundException($"unknown securityId {securityId}");
        if (!_haltById.Remove(securityId)) return false;
        _sink.OnInstrumentResumed(new InstrumentResumedEvent(
            SecurityId: securityId,
            TransactTimeNanos: txnNanos,
            RptSeq: ++_rptSeq));
        return true;
    }

    /// <summary>
    /// Issue #322: returns <c>true</c> with the populated halt state when
    /// <paramref name="securityId"/> is currently halted, <c>false</c>
    /// otherwise. Read on the dispatch thread.
    /// </summary>
    public bool IsHalted(long securityId, out HaltState state)
    {
        AssertOnOwnerThread();
        return _haltById.TryGetValue(securityId, out state);
    }

    /// <summary>
    /// Operator-issued auction uncross (gap-functional §6 / Onda M ·
    /// M3 / #230). Drains crossing volume at the single Theoretical
    /// Opening Price computed from the current resting book, emits one
    /// <see cref="TradeEvent"/> per matched pair (deterministic
    /// aggressor-side: order with the later
    /// <c>InsertTimestampNanos</c>; tie → buy side), then transitions
    /// the trading phase to <paramref name="targetPhase"/>.
    /// <para>
    /// Required current phase / target combinations:
    /// <list type="bullet">
    ///   <item><see cref="TradingPhase.Reserved"/> →
    ///     <see cref="TradingPhase.Open"/></item>
    ///   <item><see cref="TradingPhase.FinalClosingCall"/> →
    ///     <see cref="TradingPhase.Close"/></item>
    /// </list>
    /// Any other source/target throws
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// <para>
    /// Iceberg makers (<see cref="MaxFloor"/> &gt; 0) participate with
    /// their visible slice only — the hidden reserve is invisible to
    /// the TOP computation (consistent with continuous matching). When
    /// a visible slice is fully drained but hidden remains, the
    /// existing replenishment path fires and the refreshed slice goes
    /// to the back of the level (no double-trade in the same uncross).
    /// </para>
    /// <para>
    /// Returns <c>true</c> if any trade printed, <c>false</c> if the
    /// book had no crossing.
    /// </para>
    /// </summary>
    public bool UncrossAuction(long securityId, TradingPhase targetPhase, ulong txnNanos)
    {
        AssertOnOwnerThread();
        if (_dispatching)
            throw new InvalidOperationException("UncrossAuction called from inside a sink callback. Operator commands must not interleave with engine dispatch.");
        if (!_phaseById.TryGetValue(securityId, out var current))
            throw new KeyNotFoundException($"unknown securityId {securityId}");

        bool fromOpening = current == TradingPhase.Reserved && targetPhase == TradingPhase.Open;
        bool fromClosing = current == TradingPhase.FinalClosingCall && targetPhase == TradingPhase.Close;
        if (!fromOpening && !fromClosing)
        {
            throw new InvalidOperationException(
                $"UncrossAuction invalid transition: {current} → {targetPhase} (allowed: Reserved→Open, FinalClosingCall→Close)");
        }
        if (!_rulesById.TryGetValue(securityId, out var rules))
            throw new KeyNotFoundException($"unknown securityId rules {securityId}");
        if (!_booksById.TryGetValue(securityId, out var book))
            throw new KeyNotFoundException($"unknown securityId book {securityId}");

        var topState = ComputeAuctionTop(book);
        bool anyTrade = false;
        long clearedQty = 0;
        if (topState.HasTop)
        {
            anyTrade = DrainAtTopPrice(book, topState.TopPriceMantissa, txnNanos, out clearedQty);
        }

        // M4 (issue #231): emit a single OpeningPrice/ClosingPrice
        // print before the post-drain TOP recomputation. Only fired
        // when at least one trade printed — there is no "empty"
        // auction print on the wire. The integration sink picks the
        // UMDF template based on Kind.
        if (anyTrade)
        {
            _sink.OnAuctionPrint(new AuctionPrintEvent(
                SecurityId: securityId,
                Kind: fromOpening ? AuctionPrintKind.Opening : AuctionPrintKind.Closing,
                PriceMantissa: topState.TopPriceMantissa,
                ClearedQuantity: clearedQty,
                TransactTimeNanos: txnNanos,
                RptSeq: NextRptSeq()));
        }

        // M5 (issue #232): expire any survivor whose TIF was bound to
        // the auction phase we are leaving. GoodForAuction lives in
        // Reserved only; AtClose lives in FinalClosingCall only.
        // Anything left on the book after the drain is, by definition,
        // unfilled and must NOT carry over into the continuous phase.
        // Day / Gtc / Gtd survive the transition unchanged.
        // Snapshot first (the cancel mutates _byOrderId) then iterate.
        var expiringTif = fromOpening
            ? TimeInForce.GoodForAuction
            : TimeInForce.AtClose;
        foreach (var resting in book.SnapshotOrders())
        {
            if (resting.Tif == expiringTif)
            {
                EmitCanceled(book, resting, txnNanos, CancelReason.AuctionExpired);
            }
        }

        // M2 hook: the drain mutated the book — recompute and emit a
        // final TheoreticalOpeningPrice/Imbalance frame (typically
        // HasTop=false now since the crossing volume was consumed).
        // We invoke this BEFORE the phase transition so consumers see
        // the auction state collapse to "no crossing" immediately
        // before the SecurityStatus phase change.
        var post = ComputeAuctionTop(book);
        if (!_auctionTopById.TryGetValue(securityId, out var prev) || prev != post)
        {
            _auctionTopById[securityId] = post;
            _sink.OnAuctionTopChanged(new AuctionTopChangedEvent(
                SecurityId: securityId,
                HasTop: post.HasTop,
                TopPriceMantissa: post.TopPriceMantissa,
                TopQuantity: post.TopQuantity,
                HasImbalance: post.HasImbalance,
                ImbalanceSide: post.ImbalanceSide,
                ImbalanceQuantity: post.ImbalanceQuantity,
                TransactTimeNanos: txnNanos,
                RptSeq: ++_rptSeq));
        }

        ApplyTradingPhase(securityId, targetPhase, txnNanos);
        return anyTrade;
    }

    /// <summary>
    /// Walks the buy and sell sides best-first, pairing head orders
    /// at <paramref name="topPrice"/> until one side runs out (or no
    /// further crossing exists at that level). One
    /// <see cref="TradeEvent"/> per pair; iceberg replenishment runs
    /// in-place. Returns <c>true</c> if at least one trade printed.
    /// </summary>
    private bool DrainAtTopPrice(LimitOrderBook book, long topPrice, ulong txnNanos, out long clearedQuantity)
    {
        bool anyTrade = false;
        clearedQuantity = 0;
        while (true)
        {
            var bidLevel = book.BestLevel(Side.Buy);
            var askLevel = book.BestLevel(Side.Sell);
            if (bidLevel is null || askLevel is null) break;
            // Only orders at prices that meet/exceed TOP participate.
            if (bidLevel.PriceMantissa < topPrice) break;
            if (askLevel.PriceMantissa > topPrice) break;
            var buy = bidLevel.Head;
            var sell = askLevel.Head;
            if (buy is null || sell is null) break;

            long tradeQty = Math.Min(buy.RemainingQuantity, sell.RemainingQuantity);

            // Deterministic aggressor side: order with the later (larger)
            // InsertTimestampNanos is the "younger" side and is recorded
            // as the aggressor; equal timestamps tiebreak to Buy.
            bool buyIsYounger = buy.InsertTimestampNanos > sell.InsertTimestampNanos
                || (buy.InsertTimestampNanos == sell.InsertTimestampNanos);
            var aggressor = buyIsYounger ? buy : sell;
            var maker = buyIsYounger ? sell : buy;

            _sink.OnTrade(new TradeEvent(
                SecurityId: book.SecurityId,
                TradeId: _nextTradeId++,
                PriceMantissa: topPrice,
                Quantity: tradeQty,
                AggressorSide: aggressor.Side,
                AggressorOrderId: aggressor.OrderId,
                AggressorClOrdId: aggressor.ClOrdId,
                AggressorFirm: aggressor.EnteringFirm,
                RestingOrderId: maker.OrderId,
                RestingFirm: maker.EnteringFirm,
                TransactTimeNanos: txnNanos,
                RptSeq: NextRptSeq()));
            anyTrade = true;
            clearedQuantity += tradeQty;

            buy.RemainingQuantity -= tradeQty;
            sell.RemainingQuantity -= tradeQty;
            bidLevel.TotalQuantity -= tradeQty;
            askLevel.TotalQuantity -= tradeQty;

            // Apply post-trade state to each side: full → fill (with
            // iceberg replenish), partial → quantity-reduced UPDATE.
            FinalizeUncrossSide(book, buy, tradeQty, txnNanos);
            FinalizeUncrossSide(book, sell, tradeQty, txnNanos);
        }
        return anyTrade;
    }

    /// <summary>
    /// Post-trade state machine for one side of an uncross pair.
    /// Mirrors the relevant tail of
    /// <see cref="ExecuteAggressorWithOrderId"/>: full fill emits
    /// <see cref="OrderFilledEvent"/> (and triggers iceberg replenish
    /// if hidden reserve remains); partial fill emits
    /// <see cref="OrderQuantityReducedEvent"/>; empty-book emits
    /// <see cref="OrderBookSideEmptyEvent"/> on full drain.
    /// </summary>
    private void FinalizeUncrossSide(LimitOrderBook book, RestingOrder o, long lastTradeQty, ulong txnNanos)
    {
        if (o.RemainingQuantity > 0)
        {
            _sink.OnOrderQuantityReduced(new OrderQuantityReducedEvent(
                SecurityId: book.SecurityId,
                OrderId: o.OrderId,
                Side: o.Side,
                PriceMantissa: o.PriceMantissa,
                NewRemainingQuantity: o.RemainingQuantity,
                InsertTimestampNanos: o.InsertTimestampNanos,
                TransactTimeNanos: txnNanos,
                RptSeq: NextRptSeq()));
            return;
        }

        // Iceberg with hidden reserve: replenish, re-insert at back.
        // (Same semantics as the continuous-matching branch.)
        if (o.HiddenQuantity > 0)
        {
            long newVisible = Math.Min(o.MaxFloor, o.HiddenQuantity);
            long newHidden = o.HiddenQuantity - newVisible;
            var icebergSide = o.Side;
            long icebergPx = o.PriceMantissa;
            long icebergOid = o.OrderId;
            // Mutate-in-place + re-Insert. Remove clears Level/Prev/Next and
            // the by-order-id index; Insert re-adds at the tail of the same
            // price level, so the order loses time priority while retaining
            // its identity (no RestingOrder allocation).
            book.Remove(o);
            o.RemainingQuantity = newVisible;
            o.HiddenQuantity = newHidden;
            o.InsertTimestampNanos = txnNanos;
            book.Insert(o);
            uint deleteSeq = NextRptSeq();
            uint addSeq = NextRptSeq();
            _sink.OnIcebergReplenished(new IcebergReplenishedEvent(
                SecurityId: book.SecurityId,
                OrderId: icebergOid,
                Side: icebergSide,
                PriceMantissa: icebergPx,
                NewVisibleQuantity: newVisible,
                RemainingHiddenQuantity: newHidden,
                InsertTimestampNanos: txnNanos,
                TransactTimeNanos: txnNanos,
                DeleteRptSeq: deleteSeq,
                AddRptSeq: addSeq));
            return;
        }

        var side = o.Side;
        book.Remove(o);
        _sink.OnOrderFilled(new OrderFilledEvent(
            SecurityId: book.SecurityId,
            OrderId: o.OrderId,
            Side: side,
            PriceMantissa: o.PriceMantissa,
            FinalFilledQuantity: lastTradeQty,
            TransactTimeNanos: txnNanos,
            RptSeq: NextRptSeq()));
        if (book.BestLevel(side) is null)
        {
            _sink.OnOrderBookSideEmpty(new OrderBookSideEmptyEvent(
                SecurityId: book.SecurityId,
                Side: side,
                TransactTimeNanos: txnNanos));
        }
    }

    public void Submit(NewOrderCommand cmd) => SubmitImpl(cmd, emitUnmatchedIocClose: true);

    /// <summary>
    /// Internal entry point used by <c>ChannelDispatcher</c> for the
    /// AgainstBook cross sweep leg (#218 / Onda L · L5). Semantically
    /// identical to <see cref="Submit(NewOrderCommand)"/> except that a
    /// zero-fill IOC outcome does NOT emit an
    /// <see cref="CancelReason.IocUnmatched"/> closure event: the cross
    /// workflow continues with phase-2 / phase-3 submissions reusing the
    /// same per-leg ClOrdID and would observe a contradictory terminal
    /// cancel for an order that is still active in the same cross.
    /// Refs #357.
    /// </summary>
    public void SubmitCrossSweep(NewOrderCommand cmd) => SubmitImpl(cmd, emitUnmatchedIocClose: false);

    private void SubmitImpl(NewOrderCommand cmd, bool emitUnmatchedIocClose)
    {
        _currentMemo = cmd.Memo;
        EnterDispatch();
        try
        {
            if (cmd.PreTradeRejectReason is { } preTradeRejectReason)
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, preTradeRejectReason, cmd.EnteredAtNanos);
                return;
            }

            if (cmd.UnsupportedOrderCharacteristic)
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.UnsupportedOrderCharacteristic, cmd.EnteredAtNanos);
                return;
            }

            if (!_rulesById.TryGetValue(cmd.SecurityId, out var rules))
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.UnknownInstrument, cmd.EnteredAtNanos);
                return;
            }

            // Issue #322: administrative halt overlay short-circuits before
            // any other validation. Cancels of resting orders remain
            // permitted (handled in Cancel below).
            if (_haltById.ContainsKey(cmd.SecurityId))
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InstrumentHalted, cmd.EnteredAtNanos);
                return;
            }

            // #201: trading-phase gating combined with #202 TIF semantics.
            // Day/IOC/FOK/GTC require Open. AtClose requires FinalClosingCall.
            // GoodForAuction requires Reserved (pre-open auction). GTD
            // (#499) rests like Day/Gtc (requires Open) but carries an
            // ExpireDate driving the end-of-day expiry sweep.
            var phase = _phaseById[cmd.SecurityId];
            // GAP-23 / #499: GTD requires a non-zero ExpireDate; a non-zero
            // ExpireDate is meaningless on any other TIF. The gateway
            // decoder enforces the same pairing for NewOrderSingle, but
            // re-validate here so direct-API and WAL-replay paths stay
            // consistent. (The engine is clockless: a past-dated ExpireDate
            // is accepted here and removed by the next daily sweep. #504 adds
            // an entry-time reject for stale GTD in the gateway decoder, which
            // holds the clock — the engine itself stays date-agnostic.)
            if (cmd.Tif == TimeInForce.Gtd)
            {
                if (cmd.ExpireDate == 0)
                {
                    Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos);
                    return;
                }
            }
            else if (cmd.ExpireDate != 0)
            {
                Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos);
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

            // Issue #214: Stop / Stop-limit. Validate StopPx + (LimitPx
            // for StopLimit) and park off-book in the trigger book.
            // Triggering happens post-trade in TriggerStopsAfterTrade.
            // Stop validation is done inside SubmitStop because it has
            // its own price/tick/band rules and cannot satisfy the
            // Market/Limit-type validations below.
            if (cmd.Type == OrderType.StopLoss || cmd.Type == OrderType.StopLimit)
            {
                SubmitStop(cmd, rules);
                return;
            }

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
            else if (cmd.Type == OrderType.Market)
            {
                // Market orders must be IOC or FOK. Day/Gtc/Gtd/AtClose/GoodForAuction
                // are all resting TIFs that can't apply to a market order.
                if (cmd.Tif != TimeInForce.IOC && cmd.Tif != TimeInForce.FOK)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.MarketNotImmediateOrCancel, cmd.EnteredAtNanos); return; }
            }
            else // MarketWithLeftover (#215)
            {
                // MWL must be Day TIF: the unfilled remainder rests on the
                // book as a Day Limit at the last execution price.
                if (cmd.Tif != TimeInForce.Day)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
                // MWL never carries a Price on the wire — the resting price
                // is derived from the last executed trade.
                if (cmd.PriceMantissa != 0)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
            }

            var book = _booksById[cmd.SecurityId];

            // FOK pre-check: walk fillable qty and reject without any side effect
            // if insufficient. Same crossing predicate as the actual match path.
            if (cmd.Tif == TimeInForce.FOK)
            {
                long fillable = book.FillableQuantityAgainst(cmd.Side, cmd.PriceMantissa, isMarket: IsMarketLike(cmd.Type));
                if (fillable < cmd.Quantity)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.FokUnfillable, cmd.EnteredAtNanos); return; }

                // If STP is enabled, FOK must check for self-trades: orders from the same firm
                // would be prevented from matching, so reject FOK as unfillable if any exist.
                if (_stp != SelfTradePrevention.None)
                {
                    bool isMarketOrder = IsMarketLike(cmd.Type);
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

            // Market / MWL with empty opposite book: reject early — neither type
            // should ever be "accepted with no fills" (MWL would have nothing
            // to anchor the leftover-as-Limit price to).
            if (IsMarketLike(cmd.Type) && book.BestLevel(LimitOrderBook.Opposite(cmd.Side)) is null)
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
                long fillable = book.FillableQuantityAgainst(cmd.Side, cmd.PriceMantissa, isMarket: IsMarketLike(cmd.Type));
                if (fillable < (long)cmd.MinQty)
                { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.MinQtyNotMet, cmd.EnteredAtNanos); return; }
            }

            // Issue #214: stop branch handled earlier (right after MinQty
            // range-check). Falling through here means cmd.Type is Limit
            // or Market.
            //
            // Issue #228 (Onda M · M1): during auction phases (Reserved /
            // FinalClosingCall) the engine accepts the order onto the book
            // but bypasses continuous matching entirely — no Trade_53 events
            // are emitted until the operator-issued auction uncross (M3).
            // The TIF gate above guarantees that only GoodForAuction (in
            // Reserved) and AtClose (in FinalClosingCall) reach this path,
            // and both are wire-restricted to Limit Order type, so we can
            // rest cmd directly with cmd.PriceMantissa.
            if (phase == TradingPhase.Reserved || phase == TradingPhase.FinalClosingCall)
            {
                RestForAuction(cmd, rules, book);
                return;
            }
            ExecuteAggressor(cmd, rules, book, emitUnmatchedIocClose);
        }
        finally { ExitDispatch(); }
    }

    public void Cancel(CancelOrderCommand cmd)
    {
        _currentMemo = cmd.Memo;
        EnterDispatch();
        try
        {
            if (!_booksById.TryGetValue(cmd.SecurityId, out var book))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownInstrument, cmd.EnteredAtNanos); return; }

            // Issue #214: if the OrderId is a parked stop, cancel it
            // off-book and emit StopOrderCanceledEvent. Stops never
            // reach the LimitOrderBook so book.TryGet won't find them.
            if (_stopById.TryGetValue(cmd.OrderId, out var stop) && stop.SecurityId == cmd.SecurityId)
            {
                _stopById.Remove(cmd.OrderId);
                _stopsBySymbol[cmd.SecurityId].Remove(stop);
                _sink.OnStopOrderCanceled(new StopOrderCanceledEvent(
                    SecurityId: cmd.SecurityId,
                    OrderId: stop.OrderId,
                    Side: stop.Side,
                    StopPxMantissa: stop.StopPxMantissa,
                    RemainingQuantityAtCancel: stop.Quantity,
                    TransactTimeNanos: cmd.EnteredAtNanos,
                    RptSeq: NextRptSeq(),
                    Memo: cmd.Memo));
                return;
            }

            if (!book.TryGet(cmd.OrderId, out var resting))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownOrderId, cmd.EnteredAtNanos); return; }

            EmitCanceled(book, resting, cmd.EnteredAtNanos, CancelReason.Client, cmd.Memo);
            RecomputeAuctionTopIfApplicable(cmd.SecurityId, cmd.EnteredAtNanos);
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
        => MassCancel(orderIds, new MassCancelCommand(0, null, enteredAtNanos));

    public int MassCancel(IReadOnlyCollection<long> orderIds, MassCancelCommand command)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        ArgumentNullException.ThrowIfNull(command);
        if (orderIds.Count == 0) return 0;
        EnterDispatch();
        try
        {
            // Single-pass resolution (round-2 perf #8): resolve each candidate
            // once, then apply all MassAction filters before summary/cancel emit.
            var resolved = _massCancelResolved;
            resolved.Clear();
            Dictionary<(long securityId, Side side), int>? groups = null;
            foreach (var orderId in orderIds)
            {
                foreach (var book in _booksById.Values)
                {
                    if (book.TryGet(orderId, out var resting))
                    {
                        if (!MatchesMassCancelFilter(book, resting, command))
                            break;
                        resolved.Add((book, resting));
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
                        TransactTimeNanos: command.EnteredAtNanos,
                        RptSeq: NextRptSeq()));
                }
            }

            int cancelled = resolved.Count;
            for (int i = 0; i < resolved.Count; i++)
            {
                var (book, resting) = resolved[i];
                EmitCanceled(book, resting, command.EnteredAtNanos, CancelReason.MassCancel);
            }
            resolved.Clear();
            return cancelled;
        }
        finally { ExitDispatch(); }
    }

    /// <summary>
    /// GAP-23 / issue #499: end-of-trading-day Good-Till-Date expiry sweep.
    /// Cancels every resting <see cref="TimeInForce.Gtd"/> order across all
    /// books owned by this engine whose <c>ExpireDate</c> (last tradeable
    /// local-market date, days since Unix epoch) is on or before
    /// <paramref name="currentDate"/> — the trading day being closed. Each
    /// cancellation flows through the standard
    /// <see cref="IMatchingEventSink.OnOrderCanceled"/> path with
    /// <see cref="CancelReason.GtdExpired"/>, so the dispatcher emits a
    /// per-order <c>ER_Cancel</c> back to the originating session and a UMDF
    /// <c>OrderDelete</c> frame exactly like a client cancel.
    /// <para>The engine is clockless by design (ADR 0009): the caller
    /// supplies the boundary date. Idempotent — a second sweep with the same
    /// date is a no-op once the matching orders are gone. Returns the number
    /// of orders cancelled.</para>
    /// </summary>
    public int ExpireGtdOrders(ushort currentDate, ulong txnNanos)
    {
        EnterDispatch();
        try
        {
            int cancelled = 0;
            foreach (var book in _booksById.Values)
            {
                // SnapshotOrders() copies into the book's scratch buffer, so
                // EmitCanceled (which mutates the book's index) is safe to
                // call while iterating the snapshot. The buffer is consumed
                // fully before the next book's SnapshotOrders() overwrites it.
                foreach (var resting in book.SnapshotOrders())
                {
                    if (resting.Tif == TimeInForce.Gtd
                        && resting.ExpireDate != 0
                        && resting.ExpireDate <= currentDate)
                    {
                        EmitCanceled(book, resting, txnNanos, CancelReason.GtdExpired);
                        cancelled++;
                    }
                }
            }
            return cancelled;
        }
        finally { ExitDispatch(); }
    }

    /// <summary>
    /// Issue #506: cancels every resting <see cref="TimeInForce.Day"/> order
    /// — and every parked Day stop / stop-limit order — across the books
    /// owned by this engine at the end of the trading session. Each
    /// cancellation flows through the standard cancel path with
    /// <see cref="CancelReason.DayExpired"/>, so the dispatcher emits a
    /// terminal <c>ExecutionReport</c> (<c>OrdStatus=EXPIRED 'C'</c>) back to
    /// the originating session and a UMDF <c>OrderDelete</c> frame.
    /// <para>Unlike <see cref="ExpireGtdOrders"/> the sweep is
    /// <em>unconditional</em> — every Day order expires at the session
    /// boundary regardless of date — so the engine needs no boundary date
    /// argument and stays fully deterministic for replay (ADR 0009).
    /// Idempotent: a second sweep is a no-op once the Day orders are gone.
    /// Returns the number of orders cancelled.</para>
    /// </summary>
    public int ExpireDayOrders(ulong txnNanos)
    {
        EnterDispatch();
        try
        {
            int cancelled = 0;
            foreach (var book in _booksById.Values)
            {
                // SnapshotOrders() copies into the book's scratch buffer, so
                // EmitCanceled (which mutates the book's index) is safe to
                // call while iterating the snapshot — mirrors ExpireGtdOrders.
                foreach (var resting in book.SnapshotOrders())
                {
                    if (resting.Tif == TimeInForce.Day)
                    {
                        EmitCanceled(book, resting, txnNanos, CancelReason.DayExpired);
                        cancelled++;
                    }
                }
            }

            // Parked Day stop / stop-limit orders sit off-book and would
            // otherwise survive to the next trading day. Collect the Day
            // stops first (read-only) so we don't mutate the per-symbol
            // bucket lists while iterating them, then remove + emit.
            List<RestingStop>? expiringStops = null;
            foreach (var bucket in _stopsBySymbol.Values)
            {
                foreach (var stop in bucket)
                {
                    if (stop.Tif == TimeInForce.Day)
                        (expiringStops ??= new List<RestingStop>()).Add(stop);
                }
            }
            if (expiringStops != null)
            {
                foreach (var stop in expiringStops)
                {
                    _stopById.Remove(stop.OrderId);
                    _stopsBySymbol[stop.SecurityId].Remove(stop);
                    // The downstream cancel encoding inherits the existing
                    // parked-stop simplification (stop price carried as the
                    // cancel price; OrdType reported as Limit) shared with the
                    // client-cancel path — only OrdStatus differs (EXPIRED).
                    _sink.OnStopOrderCanceled(new StopOrderCanceledEvent(
                        SecurityId: stop.SecurityId,
                        OrderId: stop.OrderId,
                        Side: stop.Side,
                        StopPxMantissa: stop.StopPxMantissa,
                        RemainingQuantityAtCancel: stop.Quantity,
                        TransactTimeNanos: txnNanos,
                        RptSeq: NextRptSeq(),
                        Memo: stop.Memo,
                        Reason: CancelReason.DayExpired));
                    cancelled++;
                }
            }
            return cancelled;
        }
        finally { ExitDispatch(); }
    }

    /// <summary>
    /// GAP-26 / issue #498: emits a daily Good-Till restatement event for
    /// every surviving resting GTC order and every unexpired GTD order
    /// (<c>ExpireDate &gt; <paramref name="currentDate"/></c>, a B3
    /// <c>LocalMktDate</c> = days since the Unix epoch) across the channel's
    /// books, plus every parked GTC stop order (stops are off-book and only
    /// accept Day or GTC, so an unexpired-GTD stop cannot exist). Past-or-equal
    /// date GTD orders are intentionally skipped here —
    /// <see cref="ExpireGtdOrders"/> runs first at the boundary and cancels
    /// them, so restating one would contradict its <c>ER_Cancel</c>. The
    /// engine stays clockless (ADR 0009): the boundary date is an explicit
    /// argument.
    ///
    /// <para>A restatement does <em>not</em> mutate the book or the stop
    /// collections: no order is removed, no price level changes, and no
    /// <c>RptSeq</c> is consumed. It is a private notification to the owning
    /// session only. Returns the number of orders restated.</para>
    /// </summary>
    public int RestateGtOrders(ushort currentDate, ulong txnNanos)
    {
        EnterDispatch();
        try
        {
            int restated = 0;
            foreach (var book in _booksById.Values)
            {
                // SnapshotOrders() copies into the book's scratch buffer.
                // Restatement is read-only (no book mutation), so the
                // snapshot is purely defensive — it mirrors ExpireGtdOrders
                // and keeps the iteration robust if the sink ever grows a
                // side-effect.
                foreach (var resting in book.SnapshotOrders())
                {
                    bool isGtc = resting.Tif == TimeInForce.Gtc;
                    bool isUnexpiredGtd = resting.Tif == TimeInForce.Gtd
                        && resting.ExpireDate != 0
                        && resting.ExpireDate > currentDate;
                    if (!isGtc && !isUnexpiredGtd) continue;

                    // New trading day: cumulative executed qty resets, so the
                    // open quantity (displayed + hidden for icebergs) is both
                    // the leaves and the order quantity on the restatement.
                    long open = resting.RemainingQuantity + resting.HiddenQuantity;
                    _sink.OnOrderRestated(new OrderRestatedEvent(
                        SecurityId: book.SecurityId,
                        OrderId: resting.OrderId,
                        Side: resting.Side,
                        PriceMantissa: resting.PriceMantissa,
                        OpenQuantity: open,
                        Tif: resting.Tif,
                        ExpireDate: resting.ExpireDate,
                        TransactTimeNanos: txnNanos,
                        OrdType: OrderType.Limit,
                        StopPxMantissa: 0,
                        Memo: resting.Memo,
                        InvestorId: resting.InvestorId));
                    restated++;
                }
            }

            // Parked GTC stop / stop-limit orders survive the day off-book and
            // must be restated too (#507 review). Iterating _stopsBySymbol is
            // read-only — restatement never triggers, cancels, or re-parks.
            foreach (var bucket in _stopsBySymbol.Values)
            {
                foreach (var stop in bucket)
                {
                    if (stop.Tif != TimeInForce.Gtc) continue;
                    bool isStopLoss = stop.StopType == OrderType.StopLoss;
                    _sink.OnOrderRestated(new OrderRestatedEvent(
                        SecurityId: stop.SecurityId,
                        OrderId: stop.OrderId,
                        // StopLoss has no resting limit price (it becomes a
                        // Market on trigger); StopLimit echoes its limit price.
                        PriceMantissa: isStopLoss ? 0 : stop.LimitPriceMantissa,
                        Side: stop.Side,
                        OpenQuantity: stop.Quantity,
                        Tif: stop.Tif,
                        ExpireDate: 0,
                        TransactTimeNanos: txnNanos,
                        OrdType: stop.StopType,
                        StopPxMantissa: stop.StopPxMantissa,
                        Memo: stop.Memo,
                        InvestorId: stop.InvestorId));
                    restated++;
                }
            }
            return restated;
        }
        finally { ExitDispatch(); }
    }

    private static bool MatchesMassCancelFilter(LimitOrderBook book, RestingOrder resting, MassCancelCommand command)
    {
        if (command.SecurityId != 0 && book.SecurityId != command.SecurityId) return false;
        if (command.SideFilter.HasValue && resting.Side != command.SideFilter.Value) return false;
        if (command.OrdTagIdFilter != 0 && resting.OrdTagId != command.OrdTagIdFilter) return false;
        if (command.AssetFilter is { Length: > 0 } asset
            && !string.Equals(resting.Asset, asset, StringComparison.Ordinal)) return false;
        if (command.InvestorIdFilter.HasValue && resting.InvestorId != command.InvestorIdFilter) return false;
        return true;
    }

    private static string DeriveAsset(string symbol)
    {
        int len = 0;
        while (len < symbol.Length && len < 6 && char.IsLetter(symbol[len]))
            len++;
        if (len == 0) len = Math.Min(symbol.Length, 6);
        return symbol[..len].ToUpperInvariant();
    }

    private static string OrderAsset(NewOrderCommand cmd, InstrumentTradingRules rules)
        => string.IsNullOrWhiteSpace(cmd.Asset) ? DeriveAsset(rules.Instrument.Symbol) : cmd.Asset.Trim().ToUpperInvariant();

    public void Replace(ReplaceOrderCommand cmd)
    {
        _currentMemo = cmd.Memo;
        EnterDispatch();
        try
        {
            if (cmd.PreTradeRejectReason is { } preTradeRejectReason)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, preTradeRejectReason, cmd.EnteredAtNanos); return; }

            if (cmd.UnsupportedOrderCharacteristic)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnsupportedOrderCharacteristic, cmd.EnteredAtNanos); return; }

            if (!_rulesById.TryGetValue(cmd.SecurityId, out var rules))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnknownInstrument, cmd.EnteredAtNanos); return; }
            // Issue #322: replace under an administrative halt is rejected
            // before mutating the resting order. Cancel remains allowed.
            if (_haltById.ContainsKey(cmd.SecurityId))
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.InstrumentHalted, cmd.EnteredAtNanos); return; }
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

            // GAP-23 / #499: resolve the effective GTD ExpireDate from the
            // replace and the resting order. A wire ExpireDate (NewExpireDate
            // non-null) overrides; otherwise an order that stays GTD keeps
            // its current ExpireDate. A transition away from GTD clears it.
            ushort effectiveExpireDate;
            if (effectiveTif == TimeInForce.Gtd)
            {
                effectiveExpireDate = cmd.NewExpireDate
                    ?? (resting.Tif == TimeInForce.Gtd ? resting.ExpireDate : (ushort)0);
            }
            else
            {
                effectiveExpireDate = 0;
            }

            // Phase gating — same rules as a brand-new order. A replace that
            // lands during a trading halt must be rejected before mutating
            // the resting order; if the new TIF is incompatible with the
            // current phase the replace is treated like a new order would be.
            var phase = _phaseById[cmd.SecurityId];
            // GTD validation mirrors the new-order path: a GTD result needs a
            // non-zero ExpireDate; an explicit non-zero ExpireDate on a
            // non-GTD result is contradictory.
            if (effectiveTif == TimeInForce.Gtd)
            {
                if (effectiveExpireDate == 0)
                { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
                // #516: decode freezes the host's market date into the command
                // so WAL replay preserves the same stale-GTD replace decision.
                if (cmd.CurrentMarketDate is { } today && effectiveExpireDate < today)
                { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.UnsupportedOrderCharacteristic, cmd.EnteredAtNanos); return; }
            }
            else if (cmd.NewExpireDate is { } suppliedExpire && suppliedExpire != 0)
            { Reject(cmd.ClOrdId, cmd.SecurityId, cmd.OrderId, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
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
                // Issue #451: spec §7.4 allows InvestorID mutation via OCRR
                // (Add ✓, Change ✓). Apply before emitting events so the
                // resting order — and subsequent MassCancel filtering — see
                // the new identifier. Null means preserve.
                if (cmd.NewInvestorId is { } newInvestorKept)
                {
                    resting.InvestorId = newInvestorKept;
                }
                // GAP-23 / #499: a priority-preserving replace may amend the
                // GTD ExpireDate in place (effectiveTif == resting.Tif here,
                // so this only changes the date, never the TIF). For non-GTD
                // orders effectiveExpireDate is 0, leaving the field untouched.
                resting.ExpireDate = effectiveExpireDate;
                uint rptSeq = NextRptSeq();
                _sink.OnOrderQuantityReduced(new OrderQuantityReducedEvent(
                    SecurityId: book.SecurityId,
                    OrderId: resting.OrderId,
                    Side: resting.Side,
                    PriceMantissa: resting.PriceMantissa,
                    NewRemainingQuantity: cmd.NewQuantity,
                    InsertTimestampNanos: resting.InsertTimestampNanos,
                    TransactTimeNanos: cmd.EnteredAtNanos,
                    RptSeq: rptSeq,
                    Memo: cmd.Memo));
                // Issue #251: priority-kept Replace must also surface as
                // an ExecutionReport_Modify on the requesting session.
                // The book mutation is announced via the UMDF UPDATE
                // emitted above; this second event is the per-session ack
                // hook the integration layer translates into ER_Modify
                // (no additional UMDF frame, so the same RptSeq is reused
                // for ER_Modify's MDEntry correlation).
                _sink.OnOrderModified(new OrderModifiedEvent(
                    SecurityId: book.SecurityId,
                    OrderId: resting.OrderId,
                    Side: resting.Side,
                    NewPriceMantissa: resting.PriceMantissa,
                    NewRemainingQuantity: cmd.NewQuantity,
                    TransactTimeNanos: cmd.EnteredAtNanos,
                    RptSeq: rptSeq,
                    Memo: cmd.Memo,
                    // Issue #459: echo post-replace InvestorId on
                    // ER_Modify so clients can reconcile the working
                    // order's identity. resting.InvestorId has already
                    // been mutated above when cmd.NewInvestorId was set,
                    // so this is the canonical post-replace value.
                    InvestorId: resting.InvestorId));
                RecomputeAuctionTopIfApplicable(cmd.SecurityId, cmd.EnteredAtNanos);
                return;
            }

            // Priority lost: emit DEL of the resting order, then submit the
            // replacement as a fresh aggressor (which may cross or rest).
            var side = resting.Side;
            EmitCanceled(book, resting, cmd.EnteredAtNanos, CancelReason.ReplaceLostPriority, cmd.Memo);

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
                EnteredAtNanos: cmd.EnteredAtNanos)
            {
                MaxFloor = resting.MaxFloor > 0 ? (ulong)resting.MaxFloor : 0UL,
                OrdTagId = resting.OrdTagId,
                Asset = resting.Asset,
                InvestorId = cmd.NewInvestorId ?? resting.InvestorId,
                ExpireDate = effectiveExpireDate,
                Memo = cmd.Memo,
            };

            // Issue #228 / #229 (Onda M): in auction phases the replacement
            // must rest like a fresh accumulation order, not aggress. The
            // phase gate above guarantees GoodForAuction (Reserved) /
            // AtClose (FinalClosingCall), both Limit-only.
            if (phase == TradingPhase.Reserved || phase == TradingPhase.FinalClosingCall)
            {
                RestForAuction(replacement, rules, book);
                return;
            }
            ExecuteAggressor(replacement, rules, book, emitUnmatchedIocClose: false);
        }
        finally { ExitDispatch(); }
    }

    private void ExecuteAggressor(NewOrderCommand cmd, InstrumentTradingRules rules, LimitOrderBook book,
        bool emitUnmatchedIocClose)
        => ExecuteAggressorWithOrderId(cmd, rules, book, _nextOrderId++, emitUnmatchedIocClose);

    /// <summary>
    /// Issue #228 (Onda M · M1): rest the order on the book without any
    /// continuous-matching attempt. Called only when the instrument is in
    /// an auction phase (<see cref="TradingPhase.Reserved"/> for opening
    /// call, <see cref="TradingPhase.FinalClosingCall"/> for closing call).
    /// Emits the same <see cref="OrderAcceptedEvent"/> that the regular
    /// rest path emits, so UMDF <c>Order_MBO_50</c> add events are still
    /// published — consumers see the auction book build up. Iceberg
    /// (<see cref="NewOrderCommand.MaxFloor"/>) is honored so the visible
    /// slice on the book matches the continuous-Open semantics from #211.
    /// </summary>
    private void RestForAuction(NewOrderCommand cmd, InstrumentTradingRules rules, LimitOrderBook book)
    {
        long visible = cmd.Quantity;
        long hidden = 0;
        if (cmd.MaxFloor != 0 && (long)cmd.MaxFloor < cmd.Quantity)
        {
            visible = (long)cmd.MaxFloor;
            hidden = cmd.Quantity - visible;
        }
        var resting = new RestingOrder
        {
            OrderId = _nextOrderId++,
            ClOrdId = cmd.ClOrdId,
            Side = cmd.Side,
            PriceMantissa = cmd.PriceMantissa,
            EnteringFirm = cmd.EnteringFirm,
            OrdTagId = cmd.OrdTagId,
            Asset = OrderAsset(cmd, rules),
            InvestorId = cmd.InvestorId,
            InsertTimestampNanos = cmd.EnteredAtNanos,
            RemainingQuantity = visible,
            Tif = cmd.Tif,
            MaxFloor = (long)cmd.MaxFloor,
            HiddenQuantity = hidden,
            ExpireDate = cmd.ExpireDate,
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

        RecomputeAuctionTopIfApplicable(book.SecurityId, cmd.EnteredAtNanos);
    }

    /// <summary>
    /// Issue #229 (Onda M · M2): recompute the Theoretical Opening Price
    /// and auction imbalance for <paramref name="securityId"/>, and emit
    /// <see cref="AuctionTopChangedEvent"/> only when the indicative
    /// state actually changed since the last emission. Caller MUST be on
    /// the dispatch thread and MUST only call from a code path that has
    /// already validated <paramref name="securityId"/> against
    /// <see cref="_booksById"/>. Hook points: post-rest in
    /// <see cref="RestForAuction"/>, post-cancel in
    /// <see cref="EmitCanceled"/> when phase is auction, and post-replace
    /// when the priority-keep branch fires.
    /// <para>
    /// The TOP algorithm is a single linear walk over the union of buy
    /// and sell price levels in best-first order, accumulating the
    /// crossable buy and sell totals at each candidate price. The
    /// matchable volume at price P is min(cumBuy@P, cumSell@P) where
    /// cumBuy@P is the total buy quantity with price &gt;= P, and
    /// cumSell@P is the total sell quantity with price &lt;= P. The TOP
    /// is the price that maximizes matchable volume; ties are broken by
    /// smallest |cumBuy - cumSell|, then by the lower price (the
    /// reference-price tiebreak from the spec is deferred to a later
    /// wave that wires the per-instrument seed through HostConfig).
    /// </para>
    /// </summary>
    private void RecomputeAuctionTopIfApplicable(long securityId, ulong txnNanos)
    {
        var phase = _phaseById[securityId];
        if (phase != TradingPhase.Reserved && phase != TradingPhase.FinalClosingCall)
            return;

        var book = _booksById[securityId];
        var newState = ComputeAuctionTop(book);

        if (_auctionTopById.TryGetValue(securityId, out var prev) && prev == newState)
            return;
        _auctionTopById[securityId] = newState;

        _sink.OnAuctionTopChanged(new AuctionTopChangedEvent(
            SecurityId: securityId,
            HasTop: newState.HasTop,
            TopPriceMantissa: newState.TopPriceMantissa,
            TopQuantity: newState.TopQuantity,
            HasImbalance: newState.HasImbalance,
            ImbalanceSide: newState.ImbalanceSide,
            ImbalanceQuantity: newState.ImbalanceQuantity,
            TransactTimeNanos: txnNanos,
            RptSeq: NextRptSeq()));
    }

    private AuctionTopState ComputeAuctionTop(LimitOrderBook book)
    {
        // Snapshot both sides into reusable scratch buffers
        // (round-2 perf #15). Bids are enumerated highest-first, asks
        // lowest-first (each is best-first per book).
        var bids = _auctionBidsScratch;
        var asks = _auctionAsksScratch;
        bids.Clear();
        asks.Clear();
        foreach (var lvl in book.EnumerateLevels(Side.Buy))
            bids.Add(lvl);
        foreach (var lvl in book.EnumerateLevels(Side.Sell))
            asks.Add(lvl);

        long buyTotal = 0;
        for (int i = 0; i < bids.Count; i++) buyTotal += bids[i].Qty;
        long sellTotal = 0;
        for (int i = 0; i < asks.Count; i++) sellTotal += asks[i].Qty;

        // No crossing: best bid < best ask, or one side empty.
        bool anyCross = bids.Count > 0 && asks.Count > 0 && bids[0].Px >= asks[0].Px;
        if (!anyCross)
        {
            if (buyTotal == 0 && sellTotal == 0)
                return default;
            // Imbalance is "all of the populated side" — there is no TOP
            // and the entire one-sided book is the residual.
            if (buyTotal > sellTotal)
                return new AuctionTopState(false, 0, 0, true, Side.Buy, buyTotal - sellTotal);
            if (sellTotal > buyTotal)
                return new AuctionTopState(false, 0, 0, true, Side.Sell, sellTotal - buyTotal);
            // Both sides equal nonzero with no crossing — extremely rare
            // (would require one side empty, contradicting the totals).
            return new AuctionTopState(false, 0, 0, false, Side.Buy, 0);
        }

        // Sweepline (round-2 perf #9): walk the union of bid + ask
        // prices once in ascending order. cumBuy(P) = qty with bid >= P
        // is monotonically non-increasing as P grows; cumSell(P) =
        // qty with ask <= P is monotonically non-decreasing. Bids are
        // already sorted highest-first (so we walk them tail-to-head as
        // a "next-bid-to-drop" pointer); asks are already sorted
        // lowest-first (so we walk them head-to-tail as a
        // "next-ask-to-add" pointer).
        var candidates = _auctionCandidatesScratch;
        candidates.Clear();
        // Build sorted-ascending union without a SortedSet: merge the
        // two already-sorted sources (bids descending → reverse iter
        // gives ascending; asks ascending) with dedupe.
        int bi = bids.Count - 1;
        int ai = 0;
        long lastAdded = long.MinValue;
        while (bi >= 0 || ai < asks.Count)
        {
            long bpx = bi >= 0 ? bids[bi].Px : long.MaxValue;
            long apx = ai < asks.Count ? asks[ai].Px : long.MaxValue;
            long pick;
            if (bpx < apx) { pick = bpx; bi--; }
            else if (apx < bpx) { pick = apx; ai++; }
            else { pick = bpx; bi--; ai++; }
            if (pick != lastAdded)
            {
                candidates.Add(pick);
                lastAdded = pick;
            }
        }

        // Two-pointer sweep over candidates ascending.
        // bidPtr: index into bids (sorted desc) — tracks the smallest
        //   bid price already included in cumBuy. Decremented (toward
        //   lower-priced bids excluded) as P rises past their price.
        // askPtr: index into asks (sorted asc) — tracks the next ask
        //   to include in cumSell. Incremented as P rises past it.
        long cumBuy = buyTotal;
        long cumSell = 0;
        int bidPtr = bids.Count - 1; // points at lowest-priced bid still in cumBuy
        int askPtr = 0;              // points at next ask to add to cumSell

        long bestMatched = -1;
        long bestImbalance = long.MaxValue;
        long bestPx = 0;
        long bestCumBuy = 0;
        long bestCumSell = 0;

        for (int ci = 0; ci < candidates.Count; ci++)
        {
            long px = candidates[ci];
            // Drop bids strictly below px (they no longer satisfy bid >= px).
            while (bidPtr >= 0 && bids[bidPtr].Px < px)
            {
                cumBuy -= bids[bidPtr].Qty;
                bidPtr--;
            }
            // Add asks <= px (they satisfy ask <= px).
            while (askPtr < asks.Count && asks[askPtr].Px <= px)
            {
                cumSell += asks[askPtr].Qty;
                askPtr++;
            }
            long matched = Math.Min(cumBuy, cumSell);
            if (matched <= 0) continue;
            long imbalance = Math.Abs(cumBuy - cumSell);
            if (matched > bestMatched
                || (matched == bestMatched && imbalance < bestImbalance)
                || (matched == bestMatched && imbalance == bestImbalance && px < bestPx))
            {
                bestMatched = matched;
                bestImbalance = imbalance;
                bestPx = px;
                bestCumBuy = cumBuy;
                bestCumSell = cumSell;
            }
        }

        if (bestMatched <= 0)
            return default;

        long resid = bestCumBuy - bestCumSell;
        bool hasImb = resid != 0;
        Side imbSide = resid > 0 ? Side.Buy : Side.Sell;
        long imbQty = Math.Abs(resid);
        return new AuctionTopState(true, bestPx, bestMatched, hasImb, imbSide, imbQty);
    }


    /// <summary>
    /// True when the order should walk the book ignoring its own price
    /// (Market and MarketWithLeftover #215). The two differ only in the
    /// post-walk handling of any remainder (Market drops; MWL rests at
    /// last-trade price).
    /// </summary>
    private static bool IsMarketLike(OrderType t)
        => t == OrderType.Market || t == OrderType.MarketWithLeftover;

    private void ExecuteAggressorWithOrderId(NewOrderCommand cmd, InstrumentTradingRules rules,
        LimitOrderBook book, long aggressorOrderIdForTrades, bool emitUnmatchedIocClose)
    {
        long aggressorRemaining = cmd.Quantity;
        bool isMarket = IsMarketLike(cmd.Type);
        bool isMwl = cmd.Type == OrderType.MarketWithLeftover;
        long limitPx = cmd.PriceMantissa;
        bool stpAggressorCanceled = false;
        long lastTradePx = 0;
        bool anyTrade = false;
        try
        {

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
                        RptSeq: NextRptSeq(),
                        AggressorMemo: cmd.Memo,
                        RestingMemo: maker.Memo));

                    aggressorRemaining -= tradeQty;
                    maker.RemainingQuantity -= tradeQty;
                    level.TotalQuantity -= tradeQty;
                    lastTradePx = tradePx;
                    anyTrade = true;

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
                            // visible/hidden counters + insert timestamp,
                            // re-insert at tail of same level. Mutating
                            // `maker` in place avoids allocating a fresh
                            // RestingOrder on every iceberg replenish hot
                            // path; identity is preserved across the
                            // Remove/Insert pair.
                            book.Remove(maker);
                            maker.RemainingQuantity = newVisible;
                            maker.HiddenQuantity = newHidden;
                            maker.InsertTimestampNanos = cmd.EnteredAtNanos;
                            book.Insert(maker);
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
                if (isMwl && anyTrade)
                {
                    // #215 MWL: leftover rests as Day Limit at last-execution
                    // price. Validation guaranteed Day TIF + Price=0 at submit
                    // time; here we anchor the resting price to the last trade
                    // and fall through to the standard resting code path.
                    limitPx = lastTradePx;
                }
                else
                {
                    // Market remainder → no resting (already validated MarketNoLiquidity
                    // upfront, but if the book emptied mid-walk we just stop here without
                    // further events for the aggressor — there is no resting order to
                    // cancel and we never emitted Accepted).
                    return;
                }
            }

            if (cmd.Tif == TimeInForce.IOC || cmd.Tif == TimeInForce.FOK)
            {
                // FOK reaches here only via the pre-check (impossible for it to
                // partially fill); IOC remainder is silently dropped from the
                // public-book perspective (no MBO event for an order that
                // never rested). Issue #357: when this branch is reached on a
                // *new* IOC/FOK submission (not via Replace or stop trigger)
                // and STP did not pre-cancel the aggressor, emit a closure
                // event so the originating session receives an
                // ExecutionReport_Cancel acknowledging the terminal state.
                // We only signal the zero-fills case here; partial-fill
                // remainders are still implicitly closed by the last
                // ER_Trade carrying LeavesQty=0 in cum/leaves accounting.
                if (emitUnmatchedIocClose && !stpAggressorCanceled && !anyTrade)
                {
                    _sink.OnOrderCanceled(new OrderCanceledEvent(
                        SecurityId: book.SecurityId,
                        OrderId: aggressorOrderIdForTrades,
                        Side: cmd.Side,
                        PriceMantissa: cmd.PriceMantissa,
                        RemainingQuantityAtCancel: aggressorRemaining,
                        TransactTimeNanos: cmd.EnteredAtNanos,
                        Reason: CancelReason.IocUnmatched,
                        RptSeq: NextRptSeq(),
                        Memo: cmd.Memo));
                }
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
                OrdTagId = cmd.OrdTagId,
                Asset = OrderAsset(cmd, rules),
                InvestorId = cmd.InvestorId,
                InsertTimestampNanos = cmd.EnteredAtNanos,
                RemainingQuantity = visible,
                Tif = cmd.Tif,
                MaxFloor = (long)cmd.MaxFloor,
                HiddenQuantity = hidden,
                ExpireDate = cmd.ExpireDate,
                Memo = cmd.Memo,
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
                RptSeq: NextRptSeq(),
                Memo: resting.Memo));
        }
        finally
        {
            // Issue #214: after the aggressor finishes (whether it
            // fully filled, partially rested, IOC-dropped, or got
            // STP-cancelled), trigger any parked stops whose threshold
            // was crossed by the trades we emitted. Triggered stops are
            // re-routed via ExecuteAggressorWithOrderId, which itself
            // may produce new trades and cascade further triggers.
            if (anyTrade)
                TriggerStopsAfterTrade(cmd.SecurityId, lastTradePx, cmd.EnteredAtNanos, rules, book);
        }
    }

    private void EmitCanceled(LimitOrderBook book, RestingOrder o, ulong txn, CancelReason reason, byte[]? requestMemo = null)
    {
        var side = o.Side;
        long px = o.PriceMantissa;
        long qty = o.RemainingQuantity;
        long oid = o.OrderId;
        var memo = requestMemo ?? o.Memo;
        book.Remove(o);
        _sink.OnOrderCanceled(new OrderCanceledEvent(
            SecurityId: book.SecurityId,
            OrderId: oid,
            Side: side,
            PriceMantissa: px,
            RemainingQuantityAtCancel: qty,
            TransactTimeNanos: txn,
            Reason: reason,
            RptSeq: NextRptSeq(),
            Memo: memo));
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

    // ===== Issue #214: Stop / Stop-limit =====
    //
    // Stop orders are parked off-book in _stopsBySymbol until a trade
    // prints at or through the stop price. On trigger, the engine
    // re-routes the parked order through the normal aggression path
    // (Market for StopLoss, Limit for StopLimit) reusing the parked
    // OrderId so client correlation survives. The trigger predicate is:
    //   * Buy stops fire when last-trade price >= StopPx
    //   * Sell stops fire when last-trade price <= StopPx
    //
    // Triggering must happen post-trade-emission (so RptSeq ordering is
    // last-trade < trigger-events) and after the per-trade book mutation
    // (so triggered Markets see the post-fill book).
    private sealed class RestingStop
    {
        public long OrderId { get; init; }
        public required string ClOrdId { get; init; }
        public Side Side { get; init; }
        public OrderType StopType { get; init; }
        public TimeInForce Tif { get; init; }
        public long StopPxMantissa { get; init; }
        public long LimitPriceMantissa { get; init; }
        public long Quantity { get; init; }
        public uint EnteringFirm { get; init; }
        public ulong EnteredAtNanos { get; init; }
        public long SecurityId { get; init; }
        public byte[] Memo { get; init; } = [];

        // Issue #453: on-behalf-of identifiers must survive both the
        // park-then-trigger transition (so the triggered residual
        // remains mass-cancellable by OrdTagID/InvestorID) and a
        // snapshot+restore round-trip.
        public byte OrdTagId { get; init; }
        public InvestorId? InvestorId { get; init; }
    }

    private void SubmitStop(NewOrderCommand cmd, InstrumentTradingRules rules)
    {
        // Stops must be Day or Gtc; IOC/FOK/AtClose/GoodForAuction make
        // no sense for an order that may sit off-book indefinitely.
        if (cmd.Tif != TimeInForce.Day && cmd.Tif != TimeInForce.Gtc)
        { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }

        // StopPx must be > 0, in band, on tick.
        if (cmd.StopPxMantissa <= 0)
        { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceNonPositive, cmd.EnteredAtNanos); return; }
        if (cmd.StopPxMantissa < rules.MinPriceMantissa || cmd.StopPxMantissa > rules.MaxPriceMantissa)
        { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceOutOfBand, cmd.EnteredAtNanos); return; }
        if (cmd.StopPxMantissa % rules.TickSizeMantissa != 0)
        { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceNotOnTick, cmd.EnteredAtNanos); return; }

        // StopLimit also requires a regular limit Price; StopLoss must
        // not carry one (it becomes a Market on trigger).
        if (cmd.Type == OrderType.StopLimit)
        {
            if (cmd.PriceMantissa <= 0)
            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceNonPositive, cmd.EnteredAtNanos); return; }
            if (cmd.PriceMantissa < rules.MinPriceMantissa || cmd.PriceMantissa > rules.MaxPriceMantissa)
            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceOutOfBand, cmd.EnteredAtNanos); return; }
            if (cmd.PriceMantissa % rules.TickSizeMantissa != 0)
            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.PriceNotOnTick, cmd.EnteredAtNanos); return; }
        }
        else // StopLoss
        {
            if (cmd.PriceMantissa != 0)
            { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }
        }

        // MaxFloor + Stop is unsupported (iceberg of an off-book order
        // is meaningless).
        if (cmd.MaxFloor != 0)
        { Reject(cmd.ClOrdId, cmd.SecurityId, 0, RejectReason.InvalidField, cmd.EnteredAtNanos); return; }

        long oid = _nextOrderId++;
        var stop = new RestingStop
        {
            OrderId = oid,
            ClOrdId = cmd.ClOrdId,
            Side = cmd.Side,
            StopType = cmd.Type,
            Tif = cmd.Tif,
            StopPxMantissa = cmd.StopPxMantissa,
            LimitPriceMantissa = cmd.PriceMantissa,
            Quantity = cmd.Quantity,
            EnteringFirm = cmd.EnteringFirm,
            EnteredAtNanos = cmd.EnteredAtNanos,
            SecurityId = cmd.SecurityId,
            Memo = cmd.Memo,
            OrdTagId = cmd.OrdTagId,
            InvestorId = cmd.InvestorId,
        };
        _stopsBySymbol[cmd.SecurityId].Add(stop);
        _stopById.Add(oid, stop);
        _sink.OnStopOrderAccepted(new StopOrderAcceptedEvent(
            SecurityId: cmd.SecurityId,
            OrderId: oid,
            ClOrdId: cmd.ClOrdId,
            Side: cmd.Side,
            StopType: cmd.Type,
            Tif: cmd.Tif,
            StopPxMantissa: cmd.StopPxMantissa,
            LimitPriceMantissa: cmd.PriceMantissa,
            Quantity: cmd.Quantity,
            EnteringFirm: cmd.EnteringFirm,
            InsertTimestampNanos: cmd.EnteredAtNanos,
            RptSeq: NextRptSeq(),
            Memo: cmd.Memo));
    }

    /// <summary>
    /// Called from <see cref="ExecuteAggressor"/> immediately after a
    /// trade is emitted and the resting maker is mutated. Walks the
    /// per-instrument stop list, collects stops whose trigger predicate
    /// is satisfied by the supplied trade price, removes them from the
    /// parked list, and re-routes each through the normal matching
    /// path. The triggered order reuses the parked OrderId so client
    /// ER_New / ER_Trade frames keep correlation. We intentionally
    /// snapshot the to-trigger list before re-execution because each
    /// triggered order may itself produce trades that trigger more
    /// stops; recursion-by-natural-loop in <see cref="ExecuteAggressor"/>
    /// handles the cascade.
    /// </summary>
    private void TriggerStopsAfterTrade(long securityId, long tradePxMantissa, ulong txnNanos,
        InstrumentTradingRules rules, LimitOrderBook book)
    {
        if (!_stopsBySymbol.TryGetValue(securityId, out var stops) || stops.Count == 0) return;

        List<RestingStop>? toFire = null;
        for (int i = stops.Count - 1; i >= 0; i--)
        {
            var s = stops[i];
            bool fires = s.Side == Side.Buy
                ? tradePxMantissa >= s.StopPxMantissa
                : tradePxMantissa <= s.StopPxMantissa;
            if (!fires) continue;
            (toFire ??= new List<RestingStop>()).Add(s);
            stops.RemoveAt(i);
            _stopById.Remove(s.OrderId);
        }
        if (toFire is null) return;

        // Order matters: original parked order should win for ties. We
        // collected in reverse, so reverse back to original insertion
        // order before re-execution.
        toFire.Reverse();
        foreach (var s in toFire)
        {
            _sink.OnStopOrderTriggered(new StopOrderTriggeredEvent(
                SecurityId: securityId,
                OrderId: s.OrderId,
                Side: s.Side,
                StopPxMantissa: s.StopPxMantissa,
                TriggerTradePriceMantissa: tradePxMantissa,
                TransactTimeNanos: txnNanos,
                RptSeq: NextRptSeq()));
            ExecuteTriggeredStop(s, rules, book, txnNanos);
        }
    }

    /// <summary>
    /// Internal re-execution path for a triggered stop. Builds a
    /// synthetic <see cref="NewOrderCommand"/> equivalent to the
    /// triggered shape (Market IOC for StopLoss, Limit Day/Gtc for
    /// StopLimit) and routes it through the same code path as a fresh
    /// aggressor — except that the OrderId is pre-allocated to the
    /// parked stop's id (preserving client correlation) and the
    /// reentrancy guard is bypassed since we are already inside a
    /// dispatch turn.
    /// </summary>
    private void ExecuteTriggeredStop(RestingStop s, InstrumentTradingRules rules,
        LimitOrderBook book, ulong txnNanos)
    {
        // Build the equivalent NewOrderCommand. ClOrdId carries the
        // original; ER_Trade / ER_New downstream will reference it.
        var triggered = new NewOrderCommand(
            ClOrdId: s.ClOrdId,
            SecurityId: s.SecurityId,
            Side: s.Side,
            Type: s.StopType == OrderType.StopLoss ? OrderType.Market : OrderType.Limit,
            Tif: s.StopType == OrderType.StopLoss ? TimeInForce.IOC : s.Tif,
            PriceMantissa: s.StopType == OrderType.StopLoss ? 0L : s.LimitPriceMantissa,
            Quantity: s.Quantity,
            EnteringFirm: s.EnteringFirm,
            EnteredAtNanos: txnNanos)
        { Memo = s.Memo, OrdTagId = s.OrdTagId, InvestorId = s.InvestorId };

        // Triggered StopLoss sees an empty opposite side → no fills,
        // silently dropped. We do NOT emit MarketNoLiquidity reject
        // here because the stop was already accepted and reporting a
        // late reject would confuse the client; the order simply
        // expires on trigger.
        if (triggered.Type == OrderType.Market && book.BestLevel(LimitOrderBook.Opposite(triggered.Side)) is null)
            return;

        ExecuteAggressorWithOrderId(triggered, rules, book, s.OrderId, emitUnmatchedIocClose: false);
    }

    private void Reject(string clOrdId, long securityId, long orderId, RejectReason r, ulong txn)
        => _sink.OnReject(new RejectEvent(clOrdId, securityId, orderId, r, txn, _currentMemo));

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
    /// Issue #169 / #384 (single-thread invariant audit).
    /// </summary>
    public void BindToDispatchThread(Thread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        // SingleWriterGuard.BindToCurrentThread always binds to the
        // calling thread; the public API takes an explicit Thread for
        // back-compat. Production callers always pass Thread.CurrentThread
        // from within the dispatch loop, so the two are identical; the
        // assert below catches any (unlikely) future caller that tries
        // to bind a different thread.
        System.Diagnostics.Debug.Assert(
            thread == Thread.CurrentThread,
            $"BindToDispatchThread must be called from the thread being bound "
            + $"(arg={thread.ManagedThreadId}, "
            + $"actual={Thread.CurrentThread.ManagedThreadId})");
        _writerGuard.BindToCurrentThread();
    }

    /// <summary>
    /// Test-only seam for direct-drive dispatcher probes that intentionally
    /// process queued work on xUnit continuation threads rather than a live
    /// dispatcher loop. Production dispatchers must bind once via
    /// <see cref="BindToDispatchThread"/>.
    /// </summary>
    public void RebindOwnerForTesting() => _writerGuard.RebindForReplay();

    /// <summary>
    /// Asserts the calling thread owns the engine. Latches the owner
    /// thread on first call if no explicit binding was made (so unit
    /// tests that drive the engine directly remain consistent across
    /// their lifetime). Compiled out in Release.
    /// Issue #169 / #384 (single-thread invariant).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOnOwnerThread() => _writerGuard.AssertOwnedByCurrentThread();
}
