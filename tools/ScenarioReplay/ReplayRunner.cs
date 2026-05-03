using B3.Exchange.SyntheticTrader;

namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Replays a parsed script against an <see cref="EntryPointClient"/>,
/// translating each <see cref="ScriptEvent"/> into a NewOrder or Cancel
/// frame and respecting per-event scheduling via the injected
/// <see cref="IClock"/>. The runner is single-threaded — events are emitted
/// in (atMs, file-order) sequence, never in parallel — so the resulting
/// tape is deterministic for a given script.
///
/// Keeps a <c>clOrdId → orderId</c> map populated from inbound ER_New /
/// ER_Trade so a script can target a resting order with
/// <c>{"kind":"cancel","clOrdId":...,"origClOrdId":...}</c> without the
/// author having to know the engine-assigned <c>orderId</c>.
/// </summary>
public sealed class ReplayRunner
{
    private readonly EntryPointClient _client;
    private readonly IClock _clock;
    private readonly CaptureWriter _capture;
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarn;
    private readonly Dictionary<ulong, long> _clOrdToOrderId = new();
    private readonly object _mapGate = new();

    public int EventsSubmitted { get; private set; }

    public ReplayRunner(EntryPointClient client, IClock clock, CaptureWriter capture,
        Action<string>? logInfo = null, Action<string>? logWarn = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(capture);
        _client = client;
        _clock = clock;
        _capture = capture;
        _logInfo = logInfo;
        _logWarn = logWarn;

        _client.OnNew += er =>
        {
            lock (_mapGate) _clOrdToOrderId[er.ClOrdId] = er.OrderId;
            _capture.WriteEr("new", er.ClOrdId, er.SecurityId, er.OrderId,
                side: er.Side == OrderSide.Buy ? "buy" : "sell");
        };
        _client.OnTrade += er =>
        {
            lock (_mapGate) _clOrdToOrderId[er.ClOrdId] = er.OrderId;
            _capture.WriteEr("trade", er.ClOrdId, er.SecurityId, er.OrderId,
                lastQty: er.LastQty, lastPxMantissa: er.LastPxMantissa,
                leavesQty: er.LeavesQty, cumQty: er.CumQty,
                side: er.Side == OrderSide.Buy ? "buy" : "sell");
        };
        _client.OnCancel += er =>
        {
            _capture.WriteEr("cancel", er.ClOrdId, er.SecurityId, er.OrderId,
                side: er.Side == OrderSide.Buy ? "buy" : "sell");
        };
        _client.OnReject += er =>
        {
            _capture.WriteEr("reject", er.ClOrdId, er.SecurityId, er.OrderId,
                origClOrdId: er.OrigClOrdId, ordRejReason: er.RejectReason);
        };
        _client.OnDisconnect += reason => _capture.WriteEvent("disconnect", reason);
    }

    public async Task RunAsync(IReadOnlyList<ScriptEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        _capture.WriteEvent("script_start", $"events={events.Count}");
        foreach (var ev in events)
        {
            ct.ThrowIfCancellationRequested();
            await _clock.DelayUntilAsync(ev.AtMs, ct).ConfigureAwait(false);
            if (!_client.IsOpen)
            {
                _logWarn?.Invoke($"client closed before line {ev.LineNumber}; aborting script");
                _capture.WriteEvent("aborted", $"client_closed_before_line_{ev.LineNumber}");
                throw new InvalidOperationException("EntryPointClient closed mid-replay");
            }
            Submit(ev);
        }
        _capture.WriteEvent("script_end", $"submitted={EventsSubmitted}");
        _logInfo?.Invoke($"script complete; events submitted={EventsSubmitted}");
    }

    private void Submit(ScriptEvent ev)
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
                    if (!_client.SendNewOrder(ev.ClOrdId, ev.SecurityId, side, type, tif, ev.Quantity, ev.PriceMantissa))
                        throw new InvalidOperationException($"line {ev.LineNumber}: failed to enqueue NewOrder (client closed?)");
                    EventsSubmitted++;
                    _capture.WriteEvent("submit_new", $"clOrdId={ev.ClOrdId} sec={ev.SecurityId} side={ev.Side} qty={ev.Quantity} px={ev.PriceMantissa} tif={ev.Tif}");
                    break;
                }
            case ScriptEventKind.Cancel:
                {
                    long orderId;
                    lock (_mapGate) _clOrdToOrderId.TryGetValue(ev.OrigClOrdId, out orderId);
                    if (!_client.SendCancel(ev.ClOrdId, ev.SecurityId, (ulong)orderId, ev.OrigClOrdId, side))
                        throw new InvalidOperationException($"line {ev.LineNumber}: failed to enqueue Cancel (client closed?)");
                    EventsSubmitted++;
                    _capture.WriteEvent("submit_cancel", $"clOrdId={ev.ClOrdId} origClOrdId={ev.OrigClOrdId} sec={ev.SecurityId} resolvedOrderId={orderId}");
                    break;
                }
        }
    }
}
