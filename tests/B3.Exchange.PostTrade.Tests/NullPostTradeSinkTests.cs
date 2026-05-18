using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

public class NullPostTradeSinkTests
{
    [Fact]
    public void NullSink_OnTrade_DoesNotThrow_AndIsSingleton()
    {
        var a = NullPostTradeSink.Instance;
        var b = NullPostTradeSink.Instance;
        Assert.Same(a, b);

        var record = new PostTradeRecord(
            TradeId: 42, TransactTimeNanos: 1_000UL, SecurityId: 900_000_000_001L,
            AggressorSide: Side.Buy, Quantity: 100, PriceMantissa: 320_000,
            BuyClOrdId: 7UL, SellClOrdId: 9UL, BuyFirm: 7, SellFirm: 8,
            BuyOrderId: 1, SellOrderId: 2);

        a.OnTrade(record);
    }

    [Fact]
    public void PostTradeRecord_RoundTripsAllFields()
    {
        var record = new PostTradeRecord(
            TradeId: 12345, TransactTimeNanos: 9_876_543_210UL,
            SecurityId: 900_000_000_006L,
            AggressorSide: Side.Sell, Quantity: 250, PriceMantissa: 1_234_5678L,
            BuyClOrdId: 111UL, SellClOrdId: 222UL, BuyFirm: 33, SellFirm: 44,
            BuyOrderId: 555, SellOrderId: 666);

        Assert.Equal(12345u, record.TradeId);
        Assert.Equal(9_876_543_210UL, record.TransactTimeNanos);
        Assert.Equal(900_000_000_006L, record.SecurityId);
        Assert.Equal(Side.Sell, record.AggressorSide);
        Assert.Equal(250, record.Quantity);
        Assert.Equal(1_234_5678L, record.PriceMantissa);
        Assert.Equal(111UL, record.BuyClOrdId);
        Assert.Equal(222UL, record.SellClOrdId);
        Assert.Equal(33u, record.BuyFirm);
        Assert.Equal(44u, record.SellFirm);
        Assert.Equal(555, record.BuyOrderId);
        Assert.Equal(666, record.SellOrderId);
    }
}
