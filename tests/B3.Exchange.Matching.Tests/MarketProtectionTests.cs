using B3.Exchange.Instruments;
using Microsoft.Extensions.Logging.Abstractions;
using static B3.Exchange.Matching.Tests.TestFactory;

namespace B3.Exchange.Matching.Tests;

public class MarketProtectionTests
{
    [Theory]
    [InlineData("lower-boundary", 9.00, true)]
    [InlineData("upper-boundary", 11.00, true)]
    [InlineData("inside", 10.00, true)]
    [InlineData("below", 8.99, false)]
    [InlineData("above", 11.01, false)]
    public void Submit_Limit_EnforcesStaticPriceBandInclusively(string clOrdId, decimal price, bool accepted)
    {
        var eng = NewEngine(WithMarketProtections(lowerPriceBand: 9.00m, upperPriceBand: 11.00m), out var sink);

        eng.Submit(new NewOrderCommand(clOrdId, PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(price), 100, 11, 1000));

        if (accepted)
        {
            Assert.Single(sink.Accepted);
            Assert.Empty(sink.Rejects);
        }
        else
        {
            Assert.Empty(sink.Accepted);
            Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, Assert.Single(sink.Rejects).Reason);
        }
    }

    [Fact]
    public void Submit_StaticPriceBand_BreachThenRecovery()
    {
        var eng = NewEngine(WithMarketProtections(lowerPriceBand: 9.00m, upperPriceBand: 11.00m), out var sink);

        eng.Submit(new NewOrderCommand("bad", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(11.01m), 100, 11, 1000));
        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, Assert.Single(sink.Rejects).Reason);
        sink.Clear();

        eng.Submit(new NewOrderCommand("good", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 11, 1001));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Replace_Limit_RejectsStaticPriceBandBreachWithoutMutatingBook()
    {
        var eng = NewEngine(WithMarketProtections(lowerPriceBand: 9.00m, upperPriceBand: 11.00m), out var sink);
        eng.Submit(new NewOrderCommand("orig", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 11, 1000));
        var orderId = sink.Accepted[0].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("repl", PetrSecId, orderId, Px(11.01m), 100, 1001));

        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, Assert.Single(sink.Rejects).Reason);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Submit_WithoutStaticPriceBand_PreservesExistingPriceBoundsOnly()
    {
        var eng = NewEngine(WithMarketProtections(), out var sink);

        eng.Submit(new NewOrderCommand("no-band", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(11.01m), 100, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Submit_AuctionCollar_NoTopEstablished_SkipsCollar()
    {
        var eng = NewEngine(WithMarketProtections(auctionCollarPercent: 5.00m), out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("one-sided", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(50.00m), 100, 11, 6000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Submit_AuctionCollar_RejectsOutsideTopPercentAndAcceptsInside()
    {
        var eng = NewEngine(WithMarketProtections(auctionCollarPercent: 5.00m), out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();
        eng.Submit(new NewOrderCommand("bid", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("ask", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));
        Assert.True(sink.AuctionTops[^1].HasTop);
        sink.Clear();

        eng.Submit(new NewOrderCommand("outside", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.51m), 100, 11, 6002));
        eng.Submit(new NewOrderCommand("inside", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.50m), 100, 11, 6003));

        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, Assert.Single(sink.Rejects).Reason);
        Assert.Single(sink.Accepted);
    }

    [Fact]
    public void Submit_AuctionCollar_FinalClosingCall_RejectsOutsideTopPercentAndAcceptsInside()
    {
        var eng = NewEngine(WithMarketProtections(auctionCollarPercent: 5.00m), out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        sink.Clear();
        eng.Submit(new NewOrderCommand("bid", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.00m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("ask", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10.00m), 100, 12, 6001));
        Assert.True(sink.AuctionTops[^1].HasTop);
        sink.Clear();

        eng.Submit(new NewOrderCommand("outside", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.51m), 100, 11, 6002));
        eng.Submit(new NewOrderCommand("inside", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.50m), 100, 11, 6003));

        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, Assert.Single(sink.Rejects).Reason);
        Assert.Single(sink.Accepted);
    }

    [Fact]
    public void Submit_AuctionCollar_DoesNotApplyOutsideAuctionPhases()
    {
        var eng = NewEngine(WithMarketProtections(auctionCollarPercent: 5.00m), out var sink);

        eng.Submit(new NewOrderCommand("open", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(50.00m), 100, 11, 6000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Theory]
    [InlineData(900, true)]
    [InlineData(1000, true)]
    [InlineData(1100, false)]
    public void Submit_PerInstrumentMaxOrderQty_EnforcesInclusiveCeiling(long quantity, bool accepted)
    {
        var eng = NewEngine(WithMarketProtections(maxOrderQty: 1000), out var sink);

        eng.Submit(new NewOrderCommand($"qty-{quantity}", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), quantity, 11, 1000));

        if (accepted)
        {
            Assert.Single(sink.Accepted);
            Assert.Empty(sink.Rejects);
        }
        else
        {
            Assert.Empty(sink.Accepted);
            Assert.Equal(RejectReason.QuantityExceedsLimit, Assert.Single(sink.Rejects).Reason);
        }
    }

    [Fact]
    public void Replace_PerInstrumentMaxOrderQty_RejectsOverLimitWithoutMutatingBook()
    {
        var eng = NewEngine(WithMarketProtections(maxOrderQty: 1000), out var sink);
        eng.Submit(new NewOrderCommand("orig", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 1000, 11, 1000));
        var orderId = sink.Accepted[0].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("repl", PetrSecId, orderId, Px(10.00m), 1100, 1001));

        Assert.Equal(RejectReason.QuantityExceedsLimit, Assert.Single(sink.Rejects).Reason);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Submit_WithoutMaxOrderQty_PreservesExistingQuantityRules()
    {
        var eng = NewEngine(WithMarketProtections(), out var sink);

        eng.Submit(new NewOrderCommand("no-limit", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 1100, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Submit_PerInstrumentMaxOrderValue_RejectsOverLimit()
    {
        var eng = NewEngine(WithMarketProtections(maxOrderValue: 1_000.00m), out var sink);

        eng.Submit(new NewOrderCommand("over", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.01m), 100, 11, 1001));

        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(sink.Rejects).Reason);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Submit_PerInstrumentMaxOrderValue_AcceptsAtLimit()
    {
        var eng = NewEngine(WithMarketProtections(maxOrderValue: 1_000.00m), out var sink);

        eng.Submit(new NewOrderCommand("at", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Submit_PerInstrumentMaxOrderValue_AcceptsUnderLimit()
    {
        var eng = NewEngine(WithMarketProtections(maxOrderValue: 1_000.00m), out var sink);

        eng.Submit(new NewOrderCommand("under", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Submit_WithoutMaxOrderValue_AcceptsLargeOrder()
    {
        var eng = NewEngine(WithMarketProtections(), out var sink);

        eng.Submit(new NewOrderCommand("no-value-limit", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(100.00m), 1_000_000_000, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void Replace_PerInstrumentMaxOrderValue_RejectsOverLimitWithoutMutatingBook()
    {
        var eng = NewEngine(WithMarketProtections(maxOrderValue: 1_000.00m), out var sink);
        eng.Submit(new NewOrderCommand("orig", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 11, 1000));
        var orderId = sink.Accepted[0].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("repl", PetrSecId, orderId, Px(10.01m), 100, 1001));

        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(sink.Rejects).Reason);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Submit_PerInstrumentMaxOrderValue_RejectsInt32OverflowSizedValue()
    {
        var eng = NewEngine(WithMarketProtections(maxOrderValue: 100_000.00m), out var sink);

        eng.Submit(new NewOrderCommand("overflow-sized", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(100.00m), 3_000, 11, 1000));

        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(sink.Rejects).Reason);
        Assert.Empty(sink.Accepted);
    }

    private static MatchingEngine NewEngine(Instrument instrument, out RecordingSink sink)
    {
        sink = new RecordingSink();
        return new MatchingEngine(new[] { instrument }, sink, NullLogger<MatchingEngine>.Instance);
    }

    private static Instrument WithMarketProtections(
        decimal? lowerPriceBand = null,
        decimal? upperPriceBand = null,
        decimal? auctionCollarPercent = null,
        long? maxOrderQty = null,
        decimal? maxOrderValue = null)
        => Petr4 with
        {
            MinPrice = 0.01m,
            MaxPrice = 100.00m,
            LowerPriceBand = lowerPriceBand,
            UpperPriceBand = upperPriceBand,
            AuctionCollarPercent = auctionCollarPercent,
            MaxOrderQty = maxOrderQty,
            MaxOrderValue = maxOrderValue,
        };
}
