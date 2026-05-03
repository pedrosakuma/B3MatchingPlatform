using B3.Exchange.Matching;

namespace B3.Exchange.Bench;

/// <summary>
/// Zero-allocation matching event sink used by benchmarks. Records nothing;
/// counters are kept only so the JIT cannot dead-code-eliminate sink calls.
/// </summary>
internal sealed class NoOpSink : IMatchingEventSink
{
    public long Accepted;
    public long QuantityReduced;
    public long Canceled;
    public long Filled;
    public long Trades;
    public long Rejects;

    public void OnOrderAccepted(in OrderAcceptedEvent e) => Accepted++;
    public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e) => QuantityReduced++;
    public void OnOrderCanceled(in OrderCanceledEvent e) => Canceled++;
    public void OnOrderFilled(in OrderFilledEvent e) => Filled++;
    public void OnTrade(in TradeEvent e) => Trades++;
    public void OnReject(in RejectEvent e) => Rejects++;

    public void Reset()
    {
        Accepted = QuantityReduced = Canceled = Filled = Trades = Rejects = 0;
    }
}
