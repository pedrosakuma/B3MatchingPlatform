using B3.Exchange.SyntheticTrader;

namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Replays a parsed script against one or more <see cref="EntryPointClient"/>
/// instances keyed by session name, translating each <see cref="ScriptEvent"/>
/// into a NewOrder or Cancel frame and respecting per-event scheduling via the
/// injected <see cref="IClock"/>. The runner is single-threaded — events are
/// emitted in (atMs, file-order) sequence, never in parallel — so the
/// resulting tape is deterministic for a given script.
///
/// Each session keeps its own <c>clOrdId → orderId</c> map populated from
/// inbound ER_New / ER_Trade so a script can target a resting order with
/// <c>{"kind":"cancel","clOrdId":...,"origClOrdId":...}</c> without the
/// author having to know the engine-assigned <c>orderId</c>. Order-id
/// namespaces are kept per-session because the gateway already namespaces
/// orders by <c>(firm, clOrdId)</c>: clOrdId reuse across sessions is fine
/// and resolves to the right resting order on cancel.
/// </summary>
public sealed class ReplayRunner
{
    private readonly IReadOnlyDictionary<string, EntryPointClient> _sessions;
    private readonly string _defaultSession;
    private readonly IClock _clock;
    private readonly CaptureWriter _capture;
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarn;
    private readonly Dictionary<string, Dictionary<ulong, long>> _clOrdToOrderId = new();
    private readonly object _mapGate = new();

    public int EventsSubmitted { get; private set; }

    /// <summary>
    /// Single-session constructor (back-compat). Treats the client as the
    /// implicit "default" session and ignores any <c>session</c> field on
    /// script events.
    /// </summary>
    public ReplayRunner(EntryPointClient client, IClock clock, CaptureWriter capture,
        Action<string>? logInfo = null, Action<string>? logWarn = null)
        : this(
            new Dictionary<string, EntryPointClient> { ["default"] = client ?? throw new ArgumentNullException(nameof(client)) },
            "default", clock, capture, logInfo, logWarn)
    {
    }

    /// <summary>
    /// Multi-session constructor. <paramref name="defaultSession"/> must be
    /// a key in <paramref name="sessions"/> and is used as the routing
    /// target whenever a <see cref="ScriptEvent"/> omits <c>session</c>.
    /// </summary>
    public ReplayRunner(
        IReadOnlyDictionary<string, EntryPointClient> sessions,
        string defaultSession,
        IClock clock,
        CaptureWriter capture,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(defaultSession);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(capture);
        if (sessions.Count == 0)
            throw new ArgumentException("at least one session required", nameof(sessions));
        if (!sessions.ContainsKey(defaultSession))
            throw new ArgumentException(
                $"defaultSession '{defaultSession}' is not a registered session", nameof(defaultSession));
        _sessions = sessions;
        _defaultSession = defaultSession;
        _clock = clock;
        _capture = capture;
        _logInfo = logInfo;
        _logWarn = logWarn;

        foreach (var (name, client) in sessions)
        {
            _clOrdToOrderId[name] = new Dictionary<ulong, long>();
            // Capture loop variable for the closures.
            var sessionName = name;
            client.OnNew += er =>
            {
                lock (_mapGate) _clOrdToOrderId[sessionName][er.ClOrdId] = er.OrderId;
                _capture.WriteEr("new", er.ClOrdId, er.SecurityId, er.OrderId,
                    side: er.Side == OrderSide.Buy ? "buy" : "sell",
                    session: sessionName);
            };
            client.OnTrade += er =>
            {
                lock (_mapGate) _clOrdToOrderId[sessionName][er.ClOrdId] = er.OrderId;
                _capture.WriteEr("trade", er.ClOrdId, er.SecurityId, er.OrderId,
                    lastQty: er.LastQty, lastPxMantissa: er.LastPxMantissa,
                    leavesQty: er.LeavesQty, cumQty: er.CumQty,
                    side: er.Side == OrderSide.Buy ? "buy" : "sell",
                    session: sessionName);
            };
            client.OnCancel += er =>
            {
                _capture.WriteEr("cancel", er.ClOrdId, er.SecurityId, er.OrderId,
                    side: er.Side == OrderSide.Buy ? "buy" : "sell",
                    session: sessionName);
            };
            client.OnReject += er =>
            {
                _capture.WriteEr("reject", er.ClOrdId, er.SecurityId, er.OrderId,
                    origClOrdId: er.OrigClOrdId, ordRejReason: er.RejectReason,
                    session: sessionName);
            };
            client.OnDisconnect += reason => _capture.WriteEvent("disconnect", reason, session: sessionName);
        }
    }

    public async Task RunAsync(IReadOnlyList<ScriptEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        _capture.WriteEvent("script_start", $"events={events.Count} sessions={_sessions.Count}");
        foreach (var ev in events)
        {
            ct.ThrowIfCancellationRequested();
            await _clock.DelayUntilAsync(ev.AtMs, ct).ConfigureAwait(false);
            var sessionName = ev.Session ?? _defaultSession;
            if (!_sessions.TryGetValue(sessionName, out var client))
            {
                _logWarn?.Invoke($"line {ev.LineNumber}: unknown session '{sessionName}' (registered: {string.Join(",", _sessions.Keys)})");
                _capture.WriteEvent("aborted", $"unknown_session_{sessionName}_line_{ev.LineNumber}");
                throw new InvalidOperationException($"line {ev.LineNumber}: unknown session '{sessionName}'");
            }
            if (!client.IsOpen)
            {
                _logWarn?.Invoke($"client '{sessionName}' closed before line {ev.LineNumber}; aborting script");
                _capture.WriteEvent("aborted", $"client_closed_before_line_{ev.LineNumber}", session: sessionName);
                throw new InvalidOperationException($"EntryPointClient '{sessionName}' closed mid-replay");
            }
            Submit(client, sessionName, ev);
        }
        _capture.WriteEvent("script_end", $"submitted={EventsSubmitted}");
        _logInfo?.Invoke($"script complete; events submitted={EventsSubmitted}");
    }

    private void Submit(EntryPointClient client, string sessionName, ScriptEvent ev)
    {
        var side = ev.Side == Side.Buy ? OrderSide.Buy : OrderSide.Sell;
        switch (ev.Kind)
        {
            case ScriptEventKind.New:
                {
                    var type = ev.Type == OrderType.Market ? OrderTypeIntent.Market : OrderTypeIntent.Limit;
                    var tif = ev.Tif switch
                    {
                        Tif.IOC => OrderTifIntent.IOC,
                        Tif.FOK => OrderTifIntent.FOK,
                        _ => OrderTifIntent.Day,
                    };
                    if (!client.SendNewOrder(ev.ClOrdId, ev.SecurityId, side, type, tif, ev.Quantity, ev.PriceMantissa))
                        throw new InvalidOperationException($"line {ev.LineNumber}: failed to enqueue NewOrder on session '{sessionName}' (client closed?)");
                    EventsSubmitted++;
                    _capture.WriteEvent("submit_new",
                        $"clOrdId={ev.ClOrdId} sec={ev.SecurityId} side={ev.Side} qty={ev.Quantity} px={ev.PriceMantissa} tif={ev.Tif}",
                        session: sessionName);
                    break;
                }
            case ScriptEventKind.Cancel:
                {
                    long orderId;
                    lock (_mapGate)
                    {
                        var map = _clOrdToOrderId[sessionName];
                        map.TryGetValue(ev.OrigClOrdId, out orderId);
                    }
                    if (!client.SendCancel(ev.ClOrdId, ev.SecurityId, (ulong)orderId, ev.OrigClOrdId, side))
                        throw new InvalidOperationException($"line {ev.LineNumber}: failed to enqueue Cancel on session '{sessionName}' (client closed?)");
                    EventsSubmitted++;
                    _capture.WriteEvent("submit_cancel",
                        $"clOrdId={ev.ClOrdId} origClOrdId={ev.OrigClOrdId} sec={ev.SecurityId} resolvedOrderId={orderId}",
                        session: sessionName);
                    break;
                }
        }
    }
}
