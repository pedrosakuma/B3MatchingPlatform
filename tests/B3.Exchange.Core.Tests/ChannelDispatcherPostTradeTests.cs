using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Core.Tests;

public partial class ChannelDispatcherTests
{
    private sealed class RecordingPostTradeSink : IPostTradeSink
    {
        public List<PostTradeRecord> Records { get; } = new();
        public void OnTrade(in PostTradeRecord record) => Records.Add(record);
    }

    [Fact]
    public void PostTradeSink_Crossing_EmitsAuditRecord_WithCorrectBuySellMapping()
    {
        var pkt = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var audit = new RecordingPostTradeSink();
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            outbound: outbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            nowNanos: () => 1_000_000_000UL, tradeDate: 19_000,
            postTradeSink: audit);
        var maker = new FakeSession(outbound) { EnteringFirm = 7 };
        var taker = new FakeSession(outbound) { EnteringFirm = 8 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 111UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 222UL);
        DrainInbound(disp);

        var record = Assert.Single(audit.Records);
        Assert.Equal(Petr, record.SecurityId);
        Assert.Equal(Side.Buy, record.AggressorSide);
        Assert.Equal(100, record.Quantity);
        Assert.Equal(Px(10m), record.PriceMantissa);
        // Aggressor=Buy → taker (firm 8, clOrdId 222) is BuySide;
        // resting=Sell → maker (firm 7, clOrdId 111) is SellSide.
        Assert.Equal(8u, record.BuyFirm);
        Assert.Equal(7u, record.SellFirm);
        Assert.Equal(222UL, record.BuyClOrdId);
        Assert.Equal(111UL, record.SellClOrdId);
    }

    [Fact]
    public void PostTradeSink_NotProvided_DefaultsToNullSink_NoCrash()
    {
        // Regression guard: existing dispatcher constructions that do not
        // pass postTradeSink must continue to behave identically.
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        Assert.Single(maker.Trades);
        Assert.Single(taker.Trades);
    }
}
