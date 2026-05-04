using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Unit tests for #GAP-15 — full NewOrderSingle (102) and
/// OrderCancelReplaceRequest (104) decoders. Validates the supported
/// happy-path subset, the BMR-eligible "unsupported feature" branch, and
/// the DECODING_ERROR (terminate) branch.
/// </summary>
public class NewOrderSingleAndReplaceDecoderTests
{
    private const long PriceNull = long.MinValue;
    private const byte RoutingNull = 255;

    private static byte[] BuildNewOrderSingleV2(
        ulong clOrdId = 99UL,
        long secId = 12345L,
        byte side = (byte)'1',
        byte ordType = (byte)'2',
        byte tif = (byte)'0',
        byte routing = RoutingNull,
        long qty = 100L,
        long price = 12_3400L,
        long stopPx = PriceNull,
        ulong minQty = 0UL,
        ulong maxFloor = 0UL)
    {
        var body = new byte[125];
        Span<byte> s = body;
        MemoryMarshal.Write(s.Slice(20, 8), in clOrdId);
        MemoryMarshal.Write(s.Slice(48, 8), in secId);
        s[56] = side;
        s[57] = ordType;
        s[58] = tif;
        s[59] = routing;
        MemoryMarshal.Write(s.Slice(60, 8), in qty);
        MemoryMarshal.Write(s.Slice(68, 8), in price);
        MemoryMarshal.Write(s.Slice(76, 8), in stopPx);
        MemoryMarshal.Write(s.Slice(84, 8), in minQty);
        MemoryMarshal.Write(s.Slice(92, 8), in maxFloor);
        return body;
    }

    private static byte[] BuildOrderCancelReplaceV2(
        ulong clOrdId = 200UL,
        long secId = 12345L,
        byte side = (byte)'1',
        byte ordType = (byte)'2',
        byte tif = (byte)'0',
        byte routing = RoutingNull,
        long qty = 100L,
        long price = 12_3400L,
        ulong orderId = 42UL,
        ulong origClOrdId = 99UL,
        long stopPx = PriceNull,
        ulong minQty = 0UL,
        ulong maxFloor = 0UL)
    {
        var body = new byte[142];
        Span<byte> s = body;
        MemoryMarshal.Write(s.Slice(20, 8), in clOrdId);
        MemoryMarshal.Write(s.Slice(48, 8), in secId);
        s[56] = side;
        s[57] = ordType;
        s[58] = tif;
        s[59] = routing;
        MemoryMarshal.Write(s.Slice(60, 8), in qty);
        MemoryMarshal.Write(s.Slice(68, 8), in price);
        MemoryMarshal.Write(s.Slice(76, 8), in orderId);
        MemoryMarshal.Write(s.Slice(84, 8), in origClOrdId);
        MemoryMarshal.Write(s.Slice(92, 8), in stopPx);
        MemoryMarshal.Write(s.Slice(100, 8), in minQty);
        MemoryMarshal.Write(s.Slice(108, 8), in maxFloor);
        return body;
    }

    // ---------------- NewOrderSingle (102) ----------------

    [Fact]
    public void NewOrderSingle_LimitDay_HappyPath()
    {
        var body = BuildNewOrderSingleV2(clOrdId: 1234UL, qty: 50, price: 12_3450L);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, enteringFirm: 7, enteredAtNanos: 1_000_000UL,
            out var cmd, out var clOrd, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(1234UL, clOrd);
        Assert.Equal(Side.Buy, cmd.Side);
        Assert.Equal(OrderType.Limit, cmd.Type);
        Assert.Equal(TimeInForce.Day, cmd.Tif);
        Assert.Equal(50L, cmd.Quantity);
        Assert.Equal(12_3450L, cmd.PriceMantissa);
        Assert.Equal(7u, cmd.EnteringFirm);
    }

    [Theory]
    [InlineData((byte)'0', TimeInForce.Day)]
    [InlineData((byte)'3', TimeInForce.IOC)]
    [InlineData((byte)'4', TimeInForce.FOK)]
    [InlineData((byte)'1', TimeInForce.Gtc)]
    [InlineData((byte)'6', TimeInForce.Gtd)]
    [InlineData((byte)'7', TimeInForce.AtClose)]
    [InlineData((byte)'A', TimeInForce.GoodForAuction)]
    public void NewOrderSingle_AcceptsSupportedTif(byte tifByte, TimeInForce expected)
    {
        var body = BuildNewOrderSingleV2(tif: tifByte);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(expected, cmd.Tif);
    }

    [Fact]
    public void NewOrderSingle_MarketDropsPrice()
    {
        var body = BuildNewOrderSingleV2(ordType: (byte)'1', price: PriceNull);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(OrderType.Market, cmd.Type);
        Assert.Equal(0L, cmd.PriceMantissa);
    }

    [Fact]
    public void NewOrderSingle_StopPxRejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2(stopPx: 11_0000L);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Stop", msg);
    }

    [Fact]
    public void NewOrderSingle_MaxFloorRejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2(maxFloor: 10UL);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Iceberg", msg);
    }

    [Fact]
    public void NewOrderSingle_AcceptsMinQty()
    {
        var body = BuildNewOrderSingleV2(minQty: 5UL, qty: 100L);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(5UL, cmd.MinQty);
    }

    [Fact]
    public void NewOrderSingle_RoutingInstructionRejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2(routing: 1);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("RoutingInstruction", msg);
    }

    [Theory]
    [InlineData((byte)'3')] // Stop
    [InlineData((byte)'4')] // StopLimit
    [InlineData((byte)'W')] // RLP
    [InlineData((byte)'K')] // MARKET_WITH_LEFTOVER_AS_LIMIT
    [InlineData((byte)'P')] // PEGGED_MIDPOINT
    public void NewOrderSingle_UnsupportedOrdTypeReturnsBmr(byte ordTypeByte)
    {
        var body = BuildNewOrderSingleV2(ordType: ordTypeByte);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("OrdType", msg);
    }

    // GTC/GTD/AtTheClose/GoodForAuction are all decoder-accepted now (#202).
    // Engine-side semantic gating (GTD plumbing, phase enforcement) is
    // covered by MatchingEngine tests rather than the decoder.

    [Theory]
    [InlineData((byte)'5')] // not in schema
    [InlineData((byte)'X')] // not in schema
    [InlineData((byte)0)]   // null on a required field
    public void NewOrderSingle_InvalidOrdTypeByteTerminates(byte ordTypeByte)
    {
        var body = BuildNewOrderSingleV2(ordType: ordTypeByte);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("OrdType", msg);
    }

    [Theory]
    [InlineData((byte)'2')] // OPG — not in schema
    [InlineData((byte)'5')] // not in schema
    [InlineData((byte)0)]   // null on required TIF
    public void NewOrderSingle_InvalidTifByteTerminates(byte tifByte)
    {
        var body = BuildNewOrderSingleV2(tif: tifByte);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("TimeInForce", msg);
    }

    [Fact]
    public void NewOrderSingle_InvalidSideTerminates()
    {
        var body = BuildNewOrderSingleV2(side: 0xAB);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("Side", msg);
    }

    [Fact]
    public void NewOrderSingle_MmProtectionResetRejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2();
        body[19] = 1;

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("MMProtectionReset", msg);
    }

    [Fact]
    public void NewOrderSingle_SelfTradePreventionRejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2();
        body[47] = 1; // CANCEL_AGGRESSOR_ORDER

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("SelfTradePrevention", msg);
    }

    [Fact]
    public void NewOrderSingle_ExpireDateRejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2();
        ushort expire = 0x1234;
        MemoryMarshal.Write(((Span<byte>)body).Slice(105, 2), in expire);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("ExpireDate", msg);
    }

    // ---------------- OrderCancelReplaceRequest (104) ----------------

    [Fact]
    public void OrderCancelReplace_HappyPath_ByOrderId()
    {
        var body = BuildOrderCancelReplaceV2(clOrdId: 555UL, orderId: 42UL, origClOrdId: 0UL,
            qty: 25, price: 50_0000L);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 1_000UL, out var cmd, out var clOrd, out var origClOrd, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(555UL, clOrd);
        Assert.Equal(0UL, origClOrd);
        Assert.Equal(42L, cmd.OrderId);
        Assert.Equal(25L, cmd.NewQuantity);
        Assert.Equal(50_0000L, cmd.NewPriceMantissa);
    }

    [Fact]
    public void OrderCancelReplace_HappyPath_ByOrigClOrdId()
    {
        var body = BuildOrderCancelReplaceV2(clOrdId: 555UL, orderId: 0UL, origClOrdId: 99UL);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out var origClOrd, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(99UL, origClOrd);
    }

    [Fact]
    public void OrderCancelReplace_TifNullAccepted()
    {
        var body = BuildOrderCancelReplaceV2(tif: 0); // schema NULL = '\0'

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
    }

    [Fact]
    public void OrderCancelReplace_StopPxRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2(stopPx: 50_0000L);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Stop", msg);
    }

    [Fact]
    public void OrderCancelReplace_MaxFloorRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2(maxFloor: 10UL);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Iceberg", msg);
    }

    [Fact]
    public void OrderCancelReplace_MinQtyRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2(minQty: 5UL);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Minimum-fill", msg);
    }

    [Fact]
    public void OrderCancelReplace_RequiresOrderIdOrOrigClOrdId()
    {
        var body = BuildOrderCancelReplaceV2(orderId: 0UL, origClOrdId: 0UL);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("OrderID", msg);
    }

    [Fact]
    public void OrderCancelReplace_RequiresPrice()
    {
        var body = BuildOrderCancelReplaceV2(price: PriceNull);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("Price", msg);
    }

    [Fact]
    public void OrderCancelReplace_MarketRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2(ordType: (byte)'1');

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Market replace", msg);
    }

    [Theory]
    [InlineData((byte)'3')] // IOC
    [InlineData((byte)'4')] // FOK
    public void OrderCancelReplace_NonDayTifRejectsAsUnsupported(byte tifByte)
    {
        var body = BuildOrderCancelReplaceV2(tif: tifByte);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("TIF", msg);
    }

    [Fact]
    public void OrderCancelReplace_DayTifAccepted()
    {
        var body = BuildOrderCancelReplaceV2(tif: (byte)'0');

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
    }

    [Fact]
    public void OrderCancelReplace_MmProtectionResetRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2();
        body[19] = 1;

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("MMProtectionReset", msg);
    }

    [Fact]
    public void OrderCancelReplace_SelfTradePreventionRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2();
        body[47] = 2;

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("SelfTradePrevention", msg);
    }

    [Fact]
    public void OrderCancelReplace_ExpireDateRejectsAsUnsupported()
    {
        var body = BuildOrderCancelReplaceV2();
        ushort expire = 0x1234;
        MemoryMarshal.Write(((Span<byte>)body).Slice(122, 2), in expire);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("ExpireDate", msg);
    }
}
