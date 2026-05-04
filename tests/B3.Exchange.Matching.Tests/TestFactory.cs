using B3.Exchange.Instruments;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Matching.Tests;

internal static class TestFactory
{
    public const long PetrSecId = 900_000_000_001L;

    public static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = PetrSecId,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000.00m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    public static MatchingEngine NewEngine(out RecordingSink sink)
    {
        sink = new RecordingSink();
        return new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance);
    }

    public static long Px(decimal p) => (long)(p * 10_000m);
}

internal sealed class RecordingSink : IMatchingEventSink
{
    public List<object> Events { get; } = new();
    public List<TradeEvent> Trades => Events.OfType<TradeEvent>().ToList();
    public List<RejectEvent> Rejects => Events.OfType<RejectEvent>().ToList();
    public List<OrderAcceptedEvent> Accepted => Events.OfType<OrderAcceptedEvent>().ToList();
    public List<OrderCanceledEvent> Canceled => Events.OfType<OrderCanceledEvent>().ToList();
    public List<OrderFilledEvent> Filled => Events.OfType<OrderFilledEvent>().ToList();
    public List<OrderQuantityReducedEvent> QtyReduced => Events.OfType<OrderQuantityReducedEvent>().ToList();
    public List<OrderModifiedEvent> Modified => Events.OfType<OrderModifiedEvent>().ToList();
    public List<OrderMassCanceledEvent> MassCanceled => Events.OfType<OrderMassCanceledEvent>().ToList();
    public List<OrderBookSideEmptyEvent> BookSideEmpty => Events.OfType<OrderBookSideEmptyEvent>().ToList();
    public List<TradingPhaseChangedEvent> PhaseChanges => Events.OfType<TradingPhaseChangedEvent>().ToList();
    public List<IcebergReplenishedEvent> Replenishments => Events.OfType<IcebergReplenishedEvent>().ToList();
    public List<StopOrderAcceptedEvent> StopAccepted => Events.OfType<StopOrderAcceptedEvent>().ToList();
    public List<StopOrderTriggeredEvent> StopTriggered => Events.OfType<StopOrderTriggeredEvent>().ToList();
    public List<StopOrderCanceledEvent> StopCanceled => Events.OfType<StopOrderCanceledEvent>().ToList();
    public List<AuctionTopChangedEvent> AuctionTops => Events.OfType<AuctionTopChangedEvent>().ToList();
    public List<AuctionPrintEvent> AuctionPrints => Events.OfType<AuctionPrintEvent>().ToList();
    public void Clear() => Events.Clear();
    public void OnOrderAccepted(in OrderAcceptedEvent e) => Events.Add(e);
    public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e) => Events.Add(e);
    public void OnOrderModified(in OrderModifiedEvent e) => Events.Add(e);
    public void OnOrderCanceled(in OrderCanceledEvent e) => Events.Add(e);
    public void OnOrderFilled(in OrderFilledEvent e) => Events.Add(e);
    public void OnTrade(in TradeEvent e) => Events.Add(e);
    public void OnReject(in RejectEvent e) => Events.Add(e);
    public void OnOrderMassCanceled(in OrderMassCanceledEvent e) => Events.Add(e);
    public void OnOrderBookSideEmpty(in OrderBookSideEmptyEvent e) => Events.Add(e);
    public void OnTradingPhaseChanged(in TradingPhaseChangedEvent e) => Events.Add(e);
    public void OnIcebergReplenished(in IcebergReplenishedEvent e) => Events.Add(e);
    public void OnStopOrderAccepted(in StopOrderAcceptedEvent e) => Events.Add(e);
    public void OnStopOrderTriggered(in StopOrderTriggeredEvent e) => Events.Add(e);
    public void OnStopOrderCanceled(in StopOrderCanceledEvent e) => Events.Add(e);
    public void OnAuctionTopChanged(in AuctionTopChangedEvent e) => Events.Add(e);
    public void OnAuctionPrint(in AuctionPrintEvent e) => Events.Add(e);
}
