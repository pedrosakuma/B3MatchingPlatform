namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Drives one or more <see cref="IStrategy"/> instances against a connected
/// <see cref="EntryPointClient"/>. Owns:
///   - per-instrument <see cref="MarketState"/> (mid, last trade, live orders);
///   - a single tick loop that calls <see cref="IStrategy.Tick"/> on each
///     strategy in turn at <c>TickInterval</c>;
///   - the ClOrdID monotonic counter and tag↔orderId/clord maps used to
///     route ER_New / ER_Trade / ER_Cancel back to the originating strategy.
///
/// Threading: ER callbacks fire on the client's recv thread; tick fires on
/// the runner thread. Both mutate the same per-instrument state, so all
/// access is funneled through a per-instrument lock. Strategies see only
/// snapshots, never the live state.
/// </summary>
public sealed class SyntheticTraderRunner : IAsyncDisposable
{
    private readonly EntryPointClient? _client;
    private readonly Dictionary<long, InstrumentRuntime> _instruments;
    private readonly Random _rng;
    private readonly TimeSpan _tickInterval;
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarn;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<ulong, OrderTracking> _byClOrd = new();
    // OrderId -> tracking. Populated on ER_New, removed on terminal events.
    // Lets cancel ERs (whose ClOrdId is the *cancel-request's*, not the
    // original order's) resolve to the originating tracking in O(1) without
    // iterating the per-strategy LiveByTag dictionaries.
    private readonly Dictionary<long, OrderTracking> _byOrderId = new();
    private long _clOrdSeq;
    private long _ticksRun;
    private long _ordersSent;
    private long _tradesObserved;
    private Task? _tickTask;

    public long TicksRun => Interlocked.Read(ref _ticksRun);
    public long OrdersSent => Interlocked.Read(ref _ordersSent);
    public long TradesObserved => Interlocked.Read(ref _tradesObserved);

    /// <summary>Test seam: number of live entries in the ClOrdID tracking map.</summary>
    internal int ByClOrdCount { get { lock (_byClOrd) return _byClOrd.Count; } }
    /// <summary>Test seam: number of live entries in the OrderId tracking map.</summary>
    internal int ByOrderIdCount { get { lock (_byOrderId) return _byOrderId.Count; } }

    public SyntheticTraderRunner(
        EntryPointClient client,
        IEnumerable<(InstrumentConfig cfg, IReadOnlyList<IStrategy> strategies)> instruments,
        Random rng,
        TimeSpan tickInterval,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null,
        CancellationToken ct = default)
        : this(instruments, rng, tickInterval, logInfo, logWarn, ct, client)
    {
    }

    /// <summary>
    /// Internal ctor for unit tests: omits the wire client so outbound sends
    /// are no-ops and no event subscriptions are wired. Tests then drive the
    /// runner via the <c>TestRaise*</c> seams below.
    /// </summary>
    internal SyntheticTraderRunner(
        IEnumerable<(InstrumentConfig cfg, IReadOnlyList<IStrategy> strategies)> instruments,
        Random rng,
        TimeSpan tickInterval,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null,
        CancellationToken ct = default,
        EntryPointClient? client = null)
    {
        _client = client;
        _rng = rng;
        _tickInterval = tickInterval;
        _logInfo = logInfo;
        _logWarn = logWarn;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _instruments = instruments.ToDictionary(
            t => t.cfg.SecurityId,
            t => new InstrumentRuntime(t.cfg, t.strategies));

        if (_client != null)
        {
            _client.OnNew += OnExecNew;
            _client.OnTrade += OnExecTrade;
            _client.OnCancel += OnExecCancel;
            _client.OnReject += OnExecReject;
        }
    }

    public void Start()
    {
        _tickTask = Task.Run(() => TickLoopAsync(_cts.Token));
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    RunTick();
                }
                catch (Exception ex)
                {
                    _logWarn?.Invoke($"tick error: {ex.GetType().Name}: {ex.Message}");
                }
                Interlocked.Increment(ref _ticksRun);
                await Task.Delay(_tickInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void RunTick()
    {
        foreach (var (secId, inst) in _instruments)
        {
            // Mid random walk first — visible to strategies on the same tick.
            DriftMid(inst);

            // Per strategy, snapshot state, run Tick, dispatch intents.
            foreach (var (strategy, perStrategy) in inst.PerStrategy)
            {
                MarketState snapshot;
                lock (inst.Sync)
                {
                    snapshot = new MarketState(
                        SecurityId: secId,
                        MidMantissa: inst.MidMantissa,
                        TickSize: inst.Cfg.TickSize,
                        LotSize: inst.Cfg.LotSize,
                        LastTradePxMantissa: inst.LastTradePx,
                        LastTradeQty: inst.LastTradeQty,
                        MyLiveOrders: perStrategy.SnapshotLiveOrders());
                }
                IEnumerable<OrderIntent> intents;
                try
                {
                    intents = strategy.Tick(in snapshot, _rng);
                }
                catch (Exception ex)
                {
                    _logWarn?.Invoke($"strategy {strategy.Name} tick threw: {ex.Message}");
                    continue;
                }
                foreach (var intent in intents) Submit(inst, perStrategy, strategy, intent);
            }
        }
    }

    private void DriftMid(InstrumentRuntime inst)
    {
        if (inst.Cfg.MidDriftProbability <= 0) return;
        if (_rng.NextDouble() >= inst.Cfg.MidDriftProbability) return;
        long step = inst.Cfg.TickSize <= 0 ? 1 : inst.Cfg.TickSize;
        long delta = _rng.Next(2) == 0 ? -step : step;
        lock (inst.Sync)
        {
            long next = inst.MidMantissa + delta;
            if (next > 0) inst.MidMantissa = next;
        }
    }

    private void Submit(InstrumentRuntime inst, PerStrategyState perStrategy, IStrategy strategy, OrderIntent intent)
    {
        switch (intent.Kind)
        {
            case OrderIntentKind.New:
                {
                    ulong clOrd = (ulong)Interlocked.Increment(ref _clOrdSeq);
                    var tracking = new OrderTracking(strategy, perStrategy, intent.ClientTag,
                        intent.SecurityId, intent.Side, intent.PriceMantissa, intent.Quantity, clOrd);
                    lock (_byClOrd) _byClOrd[clOrd] = tracking;
                    lock (inst.Sync)
                    {
                        perStrategy.PendingByTag[intent.ClientTag] = clOrd;
                    }
                    bool ok = _client?.SendNewOrder(clOrd, intent.SecurityId, intent.Side, intent.Type, intent.Tif,
                        intent.Quantity, intent.PriceMantissa) ?? false;
                    if (ok) Interlocked.Increment(ref _ordersSent);
                    break;
                }
            case OrderIntentKind.Cancel:
                {
                    LiveOrderRecord? live;
                    ulong clOrd = (ulong)Interlocked.Increment(ref _clOrdSeq);
                    lock (inst.Sync)
                    {
                        perStrategy.LiveByTag.TryGetValue(intent.CancelTag, out var rec);
                        live = rec.OrderId == 0 ? null : rec;
                    }
                    if (live is null) break;
                    _client?.SendCancel(clOrd, intent.SecurityId, (ulong)live.Value.OrderId, origClOrdId: 0, live.Value.Side);
                    break;
                }
        }
    }

    private void OnExecNew(ExecReportNew er)
    {
        OrderTracking? tracking;
        lock (_byClOrd) _byClOrd.TryGetValue(er.ClOrdId, out tracking);
        if (tracking is null) return;
        if (!_instruments.TryGetValue(tracking.SecurityId, out var inst)) return;
        tracking.OrderId = er.OrderId;
        lock (_byOrderId) _byOrderId[er.OrderId] = tracking;
        lock (inst.Sync)
        {
            tracking.PerStrategy.LiveByTag[tracking.ClientTag] = new LiveOrderRecord(
                er.OrderId, tracking.Side, tracking.PriceMantissa, tracking.Quantity);
            tracking.PerStrategy.PendingByTag.Remove(tracking.ClientTag);
        }
        try { tracking.Strategy.OnAck(tracking.ClientTag, er.OrderId); }
        catch (Exception ex) { _logWarn?.Invoke($"strategy OnAck threw: {ex.Message}"); }
    }

    private void OnExecTrade(ExecReportTrade er)
    {
        Interlocked.Increment(ref _tradesObserved);

        OrderTracking? tracking;
        lock (_byClOrd) _byClOrd.TryGetValue(er.ClOrdId, out tracking);

        if (_instruments.TryGetValue(er.SecurityId, out var inst))
        {
            lock (inst.Sync)
            {
                inst.LastTradePx = er.LastPxMantissa;
                inst.LastTradeQty = er.LastQty;
                if (tracking != null)
                {
                    if (tracking.PerStrategy.LiveByTag.TryGetValue(tracking.ClientTag, out var live))
                    {
                        if (er.LeavesQty == 0)
                            tracking.PerStrategy.LiveByTag.Remove(tracking.ClientTag);
                        else
                            tracking.PerStrategy.LiveByTag[tracking.ClientTag] =
                                live with { RemainingQty = er.LeavesQty };
                    }
                }
            }
        }
        if (tracking != null)
        {
            try
            {
                tracking.Strategy.OnFill(new FillEvent(tracking.ClientTag, er.Side,
                    er.LastPxMantissa, er.LastQty, er.LeavesQty));
                if (er.LeavesQty == 0) tracking.Strategy.OnRemoved(tracking.ClientTag);
            }
            catch (Exception ex) { _logWarn?.Invoke($"strategy OnFill threw: {ex.Message}"); }

            // Terminal: full fill drops both tracking maps. Partial fills keep
            // them — more fills (or a cancel) may still arrive for this order.
            if (er.LeavesQty == 0)
            {
                lock (_byClOrd) _byClOrd.Remove(tracking.OriginalClOrdId);
                lock (_byOrderId) _byOrderId.Remove(tracking.OrderId);
            }
        }
    }

    private void OnExecCancel(ExecReportCancel er)
    {
        // The cancel ER's ClOrdID is the cancel-request's clord, which we
        // intentionally do not register in _byClOrd (we'd otherwise leak any
        // cancel-rejects targeting it). Resolve via the OrderId index instead
        // — O(1), no enumeration of LiveByTag.
        OrderTracking? tracking;
        lock (_byOrderId) _byOrderId.TryGetValue(er.OrderId, out tracking);
        if (tracking is null) return;
        if (!_instruments.TryGetValue(tracking.SecurityId, out var inst)) return;

        lock (inst.Sync)
        {
            tracking.PerStrategy.LiveByTag.Remove(tracking.ClientTag);
        }
        try { tracking.Strategy.OnRemoved(tracking.ClientTag); }
        catch (Exception ex) { _logWarn?.Invoke($"strategy OnRemoved threw: {ex.Message}"); }

        // Terminal: drop both tracking maps for this order.
        lock (_byClOrd) _byClOrd.Remove(tracking.OriginalClOrdId);
        lock (_byOrderId) _byOrderId.Remove(tracking.OrderId);
    }

    private void OnExecReject(ExecReportReject er)
    {
        OrderTracking? tracking;
        lock (_byClOrd) _byClOrd.TryGetValue(er.ClOrdId, out tracking);
        if (tracking is null) return;
        if (_instruments.TryGetValue(tracking.SecurityId, out var inst))
        {
            lock (inst.Sync) tracking.PerStrategy.PendingByTag.Remove(tracking.ClientTag);
        }
        try { tracking.Strategy.OnRemoved(tracking.ClientTag); }
        catch (Exception ex) { _logWarn?.Invoke($"strategy OnRemoved threw: {ex.Message}"); }

        // Terminal: drop both tracking maps. _byOrderId entry only exists if
        // the order was acked before being rejected (rare race), but Remove
        // is harmless when absent.
        lock (_byClOrd) _byClOrd.Remove(tracking.OriginalClOrdId);
        if (tracking.OrderId != 0)
            lock (_byOrderId) _byOrderId.Remove(tracking.OrderId);
    }

    /// <summary>Test seam: synchronously dispatches an ER_New as if it had
    /// arrived from the wire.</summary>
    internal void TestRaiseNew(ExecReportNew er) => OnExecNew(er);
    /// <summary>Test seam: synchronously dispatches an ER_Trade.</summary>
    internal void TestRaiseTrade(ExecReportTrade er) => OnExecTrade(er);
    /// <summary>Test seam: synchronously dispatches an ER_Cancel.</summary>
    internal void TestRaiseCancel(ExecReportCancel er) => OnExecCancel(er);
    /// <summary>Test seam: synchronously dispatches an ER_Reject.</summary>
    internal void TestRaiseReject(ExecReportReject er) => OnExecReject(er);
    /// <summary>Test seam: submits an order intent through the normal
    /// <c>Submit</c> path. With a null client, the wire send is skipped but
    /// the tracking maps and PendingByTag are populated identically.</summary>
    internal void TestSubmit(IStrategy strategy, OrderIntent intent)
    {
        var inst = _instruments[intent.SecurityId];
        var per = inst.PerStrategy.First(t => ReferenceEquals(t.strategy, strategy)).state;
        Submit(inst, per, strategy, intent);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { if (_tickTask != null) await _tickTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    private sealed class InstrumentRuntime
    {
        public InstrumentConfig Cfg { get; }
        public List<(IStrategy strategy, PerStrategyState state)> PerStrategy { get; }
        public object Sync { get; } = new();
        public long MidMantissa;
        public long LastTradePx;
        public long LastTradeQty;

        public InstrumentRuntime(InstrumentConfig cfg, IReadOnlyList<IStrategy> strategies)
        {
            Cfg = cfg;
            MidMantissa = cfg.InitialMidMantissa;
            LastTradePx = cfg.InitialMidMantissa;
            PerStrategy = strategies.Select(s => (s, new PerStrategyState(s))).ToList();
        }
    }

    private sealed class PerStrategyState
    {
        public IStrategy OwnerStrategy { get; }
        // Tag -> (OrderId, Side, Price, Remaining). Populated on ER_New, mutated on Trade, removed on Cancel.
        public Dictionary<string, LiveOrderRecord> LiveByTag { get; } = new();
        // Tag -> ClOrdID for orders sent but not yet acked.
        public Dictionary<string, ulong> PendingByTag { get; } = new();

        public PerStrategyState(IStrategy ownerStrategy) { OwnerStrategy = ownerStrategy; }

        public IReadOnlyList<LiveOrder> SnapshotLiveOrders()
        {
            var list = new List<LiveOrder>(LiveByTag.Count);
            foreach (var (tag, rec) in LiveByTag)
                list.Add(new LiveOrder(tag, rec.Side, rec.PriceMantissa, rec.RemainingQty));
            return list;
        }
    }

    private readonly record struct LiveOrderRecord(long OrderId, OrderSide Side, long PriceMantissa, long RemainingQty);

    private sealed class OrderTracking
    {
        public IStrategy Strategy { get; }
        public PerStrategyState PerStrategy { get; }
        public string ClientTag { get; }
        public long SecurityId { get; }
        public OrderSide Side { get; }
        public long PriceMantissa { get; }
        public long Quantity { get; }
        // ClOrdID used when this order was first sent to the host. The cancel
        // request, if any, gets its own ClOrdID; we keep the original here so
        // terminal events (full fill / cancel / reject) can clean up the
        // _byClOrd entry without needing a reverse lookup.
        public ulong OriginalClOrdId { get; }
        // Engine-assigned OrderID, populated on ER_New. Zero before ack.
        public long OrderId { get; set; }

        public OrderTracking(IStrategy strategy, PerStrategyState perStrategy, string clientTag,
            long securityId, OrderSide side, long priceMantissa, long quantity, ulong originalClOrdId)
        {
            Strategy = strategy;
            PerStrategy = perStrategy;
            ClientTag = clientTag;
            SecurityId = securityId;
            Side = side;
            PriceMantissa = priceMantissa;
            Quantity = quantity;
            OriginalClOrdId = originalClOrdId;
        }
    }
}
