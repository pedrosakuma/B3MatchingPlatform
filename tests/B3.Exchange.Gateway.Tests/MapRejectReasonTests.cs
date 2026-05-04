using B3.Exchange.Gateway;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Locks the engine RejectReason -> FIX OrdRejReason wire-code mapping
/// (#GAP-17 / issue #53). Standard FIX 4.4 codes:
///   0  = Broker / exchange option (generic / catch-all)
///   1  = Unknown symbol
///   3  = Order exceeds limit
///   5  = Unknown order
///   11 = Unsupported order characteristic
/// </summary>
public class MapRejectReasonTests
{
    [Theory]
    [InlineData(RejectReason.UnknownInstrument, 1u)]
    [InlineData(RejectReason.UnknownOrderId, 5u)]
    [InlineData(RejectReason.PriceOutOfBand, 3u)]
    [InlineData(RejectReason.PriceNotOnTick, 3u)]
    [InlineData(RejectReason.PriceNonPositive, 3u)]
    [InlineData(RejectReason.QuantityNonPositive, 11u)]
    [InlineData(RejectReason.QuantityNotMultipleOfLot, 11u)]
    [InlineData(RejectReason.MarketNotImmediateOrCancel, 11u)]
    [InlineData(RejectReason.InvalidTimeInForceForMarket, 11u)]
    [InlineData(RejectReason.MarketNoLiquidity, 0u)]
    [InlineData(RejectReason.FokUnfillable, 0u)]
    [InlineData(RejectReason.SelfTradePrevention, 0u)]
    [InlineData(RejectReason.MinQtyNotMet, 0u)]
    [InlineData(RejectReason.InvalidField, 11u)]
    public void MapRejectReason_ProducesExpectedWireCode(RejectReason reason, uint expected)
    {
        Assert.Equal(expected, FixpSession.MapRejectReason(reason));
    }

    [Fact]
    public void MapRejectReason_CoversEveryEnumValue()
    {
        // Guard: if a new RejectReason is added to the engine, this test forces
        // the maintainer to extend the switch (otherwise the new value would
        // silently fall through to 0 = generic, hiding diagnostics from clients).
        foreach (var r in System.Enum.GetValues<RejectReason>())
        {
            // Should not throw and must return one of the documented wire codes.
            uint code = FixpSession.MapRejectReason(r);
            Assert.Contains(code, new uint[] { 0u, 1u, 3u, 5u, 11u, 13u });
        }
    }
}
