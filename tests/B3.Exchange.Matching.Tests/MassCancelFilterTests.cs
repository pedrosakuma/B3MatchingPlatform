using B3.Exchange.Instruments;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Matching.Tests;

using static TestFactory;

public class MassCancelFilterTests
{
    private const long ValeSecId = 900_000_000_002L;

    [Fact]
    public void MassCancel_FiltersByOrdTagId()
    {
        var eng = NewMultiInstrumentEngine(out var sink);
        long keep = Rest(eng, sink, "keep", PetrSecId, Side.Buy, ordTagId: 1);
        long cancel = Rest(eng, sink, "cancel", PetrSecId, Side.Sell, ordTagId: 2);
        sink.Clear();

        int n = eng.MassCancel(new[] { keep, cancel }, new MassCancelCommand(0, null, 9_000)
        {
            OrdTagIdFilter = 2,
        });

        Assert.Equal(1, n);
        Assert.Equal(cancel, Assert.Single(sink.Canceled).OrderId);
    }

    [Fact]
    public void MassCancel_FiltersByAsset()
    {
        var eng = NewMultiInstrumentEngine(out var sink);
        long petr = Rest(eng, sink, "petr", PetrSecId, Side.Buy);
        long vale = Rest(eng, sink, "vale", ValeSecId, Side.Buy);
        sink.Clear();

        int n = eng.MassCancel(new[] { petr, vale }, new MassCancelCommand(0, null, 9_000)
        {
            AssetFilter = "VALE",
        });

        Assert.Equal(1, n);
        Assert.Equal(vale, Assert.Single(sink.Canceled).OrderId);
    }

    [Fact]
    public void MassCancel_FiltersByInvestorId()
    {
        var eng = NewMultiInstrumentEngine(out var sink);
        long keep = Rest(eng, sink, "keep", PetrSecId, Side.Buy, investorId: new InvestorId(10, 20));
        long cancel = Rest(eng, sink, "cancel", PetrSecId, Side.Sell, investorId: new InvestorId(30, 40));
        sink.Clear();

        int n = eng.MassCancel(new[] { keep, cancel }, new MassCancelCommand(0, null, 9_000)
        {
            InvestorIdFilter = new InvestorId(30, 40),
        });

        Assert.Equal(1, n);
        Assert.Equal(cancel, Assert.Single(sink.Canceled).OrderId);
    }

    [Fact]
    public void MassCancel_AppliesAllFiltersTogether()
    {
        var eng = NewMultiInstrumentEngine(out var sink);
        long wrongSide = Rest(eng, sink, "wrong-side", PetrSecId, Side.Sell, ordTagId: 7, investorId: new InvestorId(1, 2));
        long wrongSecurity = Rest(eng, sink, "wrong-sec", ValeSecId, Side.Buy, ordTagId: 7, investorId: new InvestorId(1, 2));
        long wrongTag = Rest(eng, sink, "wrong-tag", PetrSecId, Side.Buy, ordTagId: 8, investorId: new InvestorId(1, 2));
        long wrongInvestor = Rest(eng, sink, "wrong-investor", PetrSecId, Side.Buy, ordTagId: 7, investorId: new InvestorId(9, 9));
        long match = Rest(eng, sink, "match", PetrSecId, Side.Buy, ordTagId: 7, investorId: new InvestorId(1, 2));
        sink.Clear();

        int n = eng.MassCancel(new[] { wrongSide, wrongSecurity, wrongTag, wrongInvestor, match },
            new MassCancelCommand(PetrSecId, Side.Buy, 9_000)
            {
                OrdTagIdFilter = 7,
                AssetFilter = "PETR",
                InvestorIdFilter = new InvestorId(1, 2),
            });

        Assert.Equal(1, n);
        Assert.Equal(match, Assert.Single(sink.Canceled).OrderId);
    }

    private static MatchingEngine NewMultiInstrumentEngine(out RecordingSink sink)
    {
        sink = new RecordingSink();
        var vale = Petr4 with { Symbol = "VALE3", SecurityId = ValeSecId, Isin = "BRVALEACNOR0" };
        return new MatchingEngine(new[] { Petr4, vale }, sink, NullLogger<MatchingEngine>.Instance);
    }

    private static long Rest(MatchingEngine eng, RecordingSink sink, string clOrdId, long securityId, Side side,
        byte ordTagId = 0, InvestorId? investorId = null)
    {
        long price = side == Side.Buy ? Px(10m) : Px(10.10m);
        eng.Submit(new NewOrderCommand(clOrdId, securityId, side, OrderType.Limit, TimeInForce.Day, price, 100, 11, 1_000)
        {
            OrdTagId = ordTagId,
            InvestorId = investorId,
        });
        return sink.Accepted[^1].OrderId;
    }
}
