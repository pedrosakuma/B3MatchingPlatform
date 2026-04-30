namespace B3.Exchange.Matching.Tests;

using static TestFactory;

public class ValidationTests
{
    [Fact]
    public void UnknownInstrument_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", 999L, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.UnknownInstrument, sink.Rejects[0].Reason);
    }

    [Fact]
    public void ZeroQuantity_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 0, 11, 1000));
        Assert.Equal(RejectReason.QuantityNonPositive, sink.Rejects[0].Reason);
    }

    [Fact]
    public void OffLot_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 150, 11, 1000));
        Assert.Equal(RejectReason.QuantityNotMultipleOfLot, sink.Rejects[0].Reason);
    }

    [Fact]
    public void OffTick_Rejects()
    {
        var eng = NewEngine(out var sink);
        // 10.005 = mantissa 100050 — between two ticks of 0.01 (100 mantissa)
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, 100050, 100, 11, 1000));
        Assert.Equal(RejectReason.PriceNotOnTick, sink.Rejects[0].Reason);
    }

    [Fact]
    public void OutOfBand_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(2_000m), 100, 11, 1000));
        Assert.Equal(RejectReason.PriceOutOfBand, sink.Rejects[0].Reason);
    }

    [Fact]
    public void MarketDay_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Market, TimeInForce.Day, 0, 100, 11, 1000));
        Assert.Equal(RejectReason.MarketNotImmediateOrCancel, sink.Rejects[0].Reason);
    }

    [Fact]
    public void MarketEmptyBook_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Market, TimeInForce.IOC, 0, 100, 11, 1000));
        Assert.Equal(RejectReason.MarketNoLiquidity, sink.Rejects[0].Reason);
    }
}
