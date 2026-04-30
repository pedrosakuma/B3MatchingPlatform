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
    private readonly EntryPointClient _client;
    private readonly Dictionary<long, InstrumentRuntime> _instruments;
    private readonly Random _rng;
    private readonly TimeSpan _tickInterval;
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarn;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<ulong, OrderTracking> _byClOrd = new();
    private long _clOrdSeq;
    private long _ticksRun;
    private long _ordersSent;
    private long _tradesObserved;
    private Task? _tickTask;

    public long TicksRun => Interlocked.Read(ref _ticksRun);
    public long OrdersSent => Interlocked.Read(ref _ordersSent);
    public long TradesObserved => Interlocked.Read(ref _tradesObserved);

    public SyntheticTraderRunner(
        EntryPointClient client,
        IEnumerable<(InstrumentConfig cfg, IReadOnlyList<IStrategy> strategies)> instruments,
        Random rng,
        TimeSpan tickInterval,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null,
        CancellationToken ct = default)
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

        _client.OnNew += OnExecNew;
        _client.OnTrade += OnExecTrade;
        _client.OnCancel += OnExecCancel;
        _client.OnReject += OnExecReject;
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
                        intent.SecurityId, intent.Side, intent.PriceMantissa, intent.Quantity);
                    lock (_byClOrd) _byClOrd[clOrd] = tracking;
                    lock (inst.Sync)
                    {
                        perStrategy.PendingByTag[intent.ClientTag] = clOrd;
                    }
                    bool ok = _client.SendNewOrder(clOrd, intent.SecurityId, intent.Side, intent.Type, intent.Tif,
                        intent.Quantity, intent.PriceMantissa);
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
                    _client.SendCancel(clOrd, intent.SecurityId, (ulong)live.Value.OrderId, origClOrdId: 0, live.Value.Side);
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
        }
    }

    private void OnExecCancel(ExecReportCancel er)
    {
        OrderTracking? tracking;
        lock (_byClOrd) _byClOrd.TryGetValue(er.ClOrdId, out tracking);
        // Cancel ER's ClOrdID is the cancel request's clord, not the original
        // order's. Look up by orderId across live orders if not found.
        if (tracking is null && _instruments.TryGetValue(er.SecurityId, out var inst))
        {
            lock (inst.Sync)
            {
                foreach (var per in inst.PerStrategy.Select(t => t.state))
                {
                    foreach (var (tag, rec) in per.LiveByTag)
                    {
                        if (rec.OrderId == er.OrderId)
                        {
                            per.LiveByTag.Remove(tag);
                            try { per.OwnerStrategy.OnRemoved(tag); }
                            catch (Exception ex) { _logWarn?.Invoke($"strategy OnRemoved threw: {ex.Message}"); }
                            return;
                        }
                    }
                }
            }
        }
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

    private sealed record OrderTracking(
        IStrategy Strategy,
        PerStrategyState PerStrategy,
        string ClientTag,
        long SecurityId,
        OrderSide Side,
        long PriceMantissa,
        long Quantity);
}
