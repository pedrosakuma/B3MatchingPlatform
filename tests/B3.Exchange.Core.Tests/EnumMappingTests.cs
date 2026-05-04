using B3.Exchange.Contracts;
using ContractsExecType = B3.Exchange.Contracts.ExecType;
using ContractsOrderType = B3.Exchange.Contracts.OrderType;
using ContractsSide = B3.Exchange.Contracts.Side;
using ContractsTif = B3.Exchange.Contracts.TimeInForce;
using MatchingOrderType = B3.Exchange.Matching.OrderType;
using MatchingRejectReason = B3.Exchange.Matching.RejectReason;
using MatchingSide = B3.Exchange.Matching.Side;
using MatchingTif = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Drift detection (#156) for the parallel enum families that live in
/// <c>B3.Exchange.Contracts</c> (FIX wire numbering) and
/// <c>B3.Exchange.Matching</c> (engine internal numbering).
///
/// <para>The two assemblies intentionally use different numeric encodings:
/// <list type="bullet">
///   <item><description><c>Contracts.Side</c> mirrors FIX tag 54
///   (Buy=1, Sell=2); <c>Matching.Side</c> uses 0-indexed enum order
///   (Buy=0, Sell=1).</description></item>
///   <item><description><c>Contracts.OrderType</c> mirrors FIX tag 40
///   (Market=1, Limit=2); <c>Matching.OrderType</c> uses 0-indexed
///   declaration order (Limit=0, Market=1).</description></item>
///   <item><description><c>Contracts.TimeInForce</c> mirrors FIX tag 59
///   (Day=0, IOC=3, FOK=4); <c>Matching.TimeInForce</c> uses 0-indexed
///   declaration order (Day=0, IOC=1, FOK=2).</description></item>
/// </list>
/// Casting between the two families is therefore <b>unsafe</b>: an
/// adapter must translate explicitly. These tests pin the canonical
/// mapping table so that adding a new value to either side without
/// updating the other (and the adapter) breaks the build.</para>
/// </summary>
public class EnumMappingTests
{
    [Theory]
    [InlineData(MatchingSide.Buy, ContractsSide.Buy)]
    [InlineData(MatchingSide.Sell, ContractsSide.Sell)]
    public void Side_MatchingToContracts(MatchingSide matching, ContractsSide expected)
    {
        Assert.Equal(expected, MapSide(matching));
    }

    [Fact]
    public void Side_AllMatchingValuesCovered()
    {
        // Pin the set of Matching.Side values. Adding a new one without
        // extending MapSide / this test forces a build break.
        var values = Enum.GetValues<MatchingSide>();
        Assert.Equal(2, values.Length);
        foreach (var v in values)
            _ = MapSide(v); // throws on uncovered values
    }

    [Fact]
    public void Side_AllContractsValuesCovered()
    {
        var values = Enum.GetValues<ContractsSide>();
        Assert.Equal(2, values.Length);
        foreach (var v in values)
            _ = MapSideBack(v);
    }

    [Theory]
    [InlineData(MatchingOrderType.Limit, ContractsOrderType.Limit)]
    [InlineData(MatchingOrderType.Market, ContractsOrderType.Market)]
    public void OrderType_MatchingToContracts(MatchingOrderType matching, ContractsOrderType expected)
    {
        Assert.Equal(expected, MapOrderType(matching));
    }

    [Fact]
    public void OrderType_AllValuesCovered()
    {
        Assert.Equal(2, Enum.GetValues<MatchingOrderType>().Length);
        Assert.Equal(2, Enum.GetValues<ContractsOrderType>().Length);
        foreach (var v in Enum.GetValues<MatchingOrderType>()) _ = MapOrderType(v);
    }

    [Theory]
    [InlineData(MatchingTif.Day, ContractsTif.Day)]
    [InlineData(MatchingTif.IOC, ContractsTif.IOC)]
    [InlineData(MatchingTif.FOK, ContractsTif.FOK)]
    [InlineData(MatchingTif.Gtc, ContractsTif.Gtc)]
    [InlineData(MatchingTif.Gtd, ContractsTif.Gtd)]
    [InlineData(MatchingTif.AtClose, ContractsTif.AtClose)]
    [InlineData(MatchingTif.GoodForAuction, ContractsTif.GoodForAuction)]
    public void TimeInForce_MatchingToContracts(MatchingTif matching, ContractsTif expected)
    {
        Assert.Equal(expected, MapTif(matching));
    }

    [Fact]
    public void TimeInForce_AllValuesCovered()
    {
        Assert.Equal(7, Enum.GetValues<MatchingTif>().Length);
        Assert.Equal(7, Enum.GetValues<ContractsTif>().Length);
        foreach (var v in Enum.GetValues<MatchingTif>()) _ = MapTif(v);
    }

    [Fact]
    public void Side_DirectCastIsUnsafe()
    {
        // Numerical encodings differ by design. A blind cast would silently
        // misroute Buy/Sell across the boundary — pin the divergence so a
        // future "let's just (Side)cast" never compiles past CI.
        Assert.NotEqual((byte)MatchingSide.Buy, (byte)ContractsSide.Buy);
        Assert.NotEqual((byte)MatchingSide.Sell, (byte)ContractsSide.Sell);
    }

    [Fact]
    public void OrderType_DirectCastIsUnsafe()
    {
        // Limit differs (0 vs 2). Market happens to coincide at 1 — but
        // the Limit divergence already prevents a blanket "(OrderType)cast"
        // from being correct, which is the property we want to pin.
        Assert.NotEqual((byte)MatchingOrderType.Limit, (byte)ContractsOrderType.Limit);
    }

    [Fact]
    public void TimeInForce_IocAndFokDirectCastIsUnsafe()
    {
        // Day happens to coincide (both 0) but IOC and FOK do not.
        Assert.NotEqual((byte)MatchingTif.IOC, (byte)ContractsTif.IOC);
        Assert.NotEqual((byte)MatchingTif.FOK, (byte)ContractsTif.FOK);
    }

    // --- Canonical mapping tables ---------------------------------------
    // These switch expressions ARE the contract: whenever someone adds a
    // new enum value to either side they must extend the corresponding
    // table here, and the AllValuesCovered tests will throw at that
    // value because the switch is exhaustive.

    private static ContractsSide MapSide(MatchingSide s) => s switch
    {
        MatchingSide.Buy => ContractsSide.Buy,
        MatchingSide.Sell => ContractsSide.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "unmapped Matching.Side"),
    };

    private static MatchingSide MapSideBack(ContractsSide s) => s switch
    {
        ContractsSide.Buy => MatchingSide.Buy,
        ContractsSide.Sell => MatchingSide.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "unmapped Contracts.Side"),
    };

    private static ContractsOrderType MapOrderType(MatchingOrderType t) => t switch
    {
        MatchingOrderType.Limit => ContractsOrderType.Limit,
        MatchingOrderType.Market => ContractsOrderType.Market,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "unmapped Matching.OrderType"),
    };

    private static ContractsTif MapTif(MatchingTif t) => t switch
    {
        MatchingTif.Day => ContractsTif.Day,
        MatchingTif.IOC => ContractsTif.IOC,
        MatchingTif.FOK => ContractsTif.FOK,
        MatchingTif.Gtc => ContractsTif.Gtc,
        MatchingTif.Gtd => ContractsTif.Gtd,
        MatchingTif.AtClose => ContractsTif.AtClose,
        MatchingTif.GoodForAuction => ContractsTif.GoodForAuction,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "unmapped Matching.TimeInForce"),
    };

    [Fact]
    public void RejectReason_HasCanonicalOrdRejReasonMapping()
    {
        // Sanity: every Matching.RejectReason maps to some
        // Contracts.OrdRejReason. The exact mapping lives in the wire
        // encoder — this test only pins that no enum value is missing.
        foreach (var r in Enum.GetValues<MatchingRejectReason>())
        {
            var mapped = MapRejectReason(r);
            Assert.True(Enum.IsDefined(mapped),
                $"RejectReason.{r} → OrdRejReason.{mapped} not defined");
        }
    }

    private static OrdRejReason MapRejectReason(MatchingRejectReason r) => r switch
    {
        MatchingRejectReason.UnknownInstrument => OrdRejReason.UnknownSymbol,
        MatchingRejectReason.PriceOutOfBand => OrdRejReason.OrderExceedsLimit,
        MatchingRejectReason.PriceNotOnTick => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.PriceNonPositive => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.QuantityNotMultipleOfLot => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.QuantityNonPositive => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.UnknownOrderId => OrdRejReason.UnknownOrder,
        MatchingRejectReason.FokUnfillable => OrdRejReason.Other,
        MatchingRejectReason.MarketNoLiquidity => OrdRejReason.Other,
        MatchingRejectReason.MarketNotImmediateOrCancel => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.InvalidTimeInForceForMarket => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.SelfTradePrevention => OrdRejReason.Other,
        MatchingRejectReason.MarketClosed => OrdRejReason.ExchangeClosed,
        MatchingRejectReason.TimeInForceNotSupported => OrdRejReason.UnsupportedOrderCharacteristic,
        MatchingRejectReason.MinQtyNotMet => OrdRejReason.Other,
        MatchingRejectReason.InvalidField => OrdRejReason.UnsupportedOrderCharacteristic,
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, "unmapped Matching.RejectReason"),
    };

    [Fact]
    public void ExecType_FixWireValuesArePinned()
    {
        // Pin the FIX tag 150 wire codes for ExecType. Drift here would
        // silently change the wire representation of every ER.
        Assert.Equal((byte)0, (byte)ContractsExecType.New);
        Assert.Equal((byte)1, (byte)ContractsExecType.PartialFill);
        Assert.Equal((byte)2, (byte)ContractsExecType.Fill);
        Assert.Equal((byte)4, (byte)ContractsExecType.Cancelled);
        Assert.Equal((byte)5, (byte)ContractsExecType.Replaced);
        Assert.Equal((byte)8, (byte)ContractsExecType.Rejected);
        Assert.Equal((byte)12, (byte)ContractsExecType.Expired);
    }
}
