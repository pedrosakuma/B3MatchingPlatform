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
    private static readonly InboundFatFingerOptions TightGuardrails = new()
    {
        MaxOrderQty = 1_000,
        MaxPriceMantissa = 1_000_000,
    };

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
        ulong maxFloor = 0UL,
        ushort expireDate = 0)
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
        MemoryMarshal.Write(s.Slice(105, 2), in expireDate);
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
        ulong maxFloor = 0UL,
        ushort investorPrefix = 0,
        uint investorDocument = 0,
        ushort expireDate = 0)
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
        MemoryMarshal.Write(s.Slice(122, 2), in expireDate);
        MemoryMarshal.Write(s.Slice(136, 2), in investorPrefix);
        MemoryMarshal.Write(s.Slice(138, 4), in investorDocument);
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

    // GAP-23 / #499: GTD with a non-zero ExpireDate is now accepted and the
    // ExpireDate is plumbed through to the command.
    [Fact]
    public void NewOrderSingle_GtdWithExpireDate_AcceptedAndPlumbed()
    {
        var body = BuildNewOrderSingleV2(tif: (byte)'6', expireDate: 0x1234);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(TimeInForce.Gtd, cmd.Tif);
        Assert.Equal((ushort)0x1234, cmd.ExpireDate);
    }

    // GAP-23 / #499: GTD without an ExpireDate is rejected as unsupported
    // (BMR-eligible) rather than terminating the session.
    // #504: a GTD whose ExpireDate is strictly before the current trading
    // day is rejected at entry as an unsupported (BMR-eligible) feature,
    // using the host-supplied market-date provider.
    private static InboundFatFingerOptions MarketDate(ushort today) => new()
    {
        CurrentMarketDateProvider = () => today,
    };

    [Fact]
    public void NewOrderSingle_GtdWithPastExpireDate_RejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2(tif: (byte)'6', expireDate: 19_999);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg, MarketDate(20_000));

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.NotNull(cmd);
        Assert.True(cmd.UnsupportedOrderCharacteristic);
        Assert.Contains("ExpireDate", msg);
    }

    [Fact]
    public void NewOrderSingle_GtdExpiringToday_Accepted()
    {
        // ExpireDate == today: valid *through* today, rests until tonight's
        // close (the EOD sweep cancels it then). Not rejected at entry.
        var body = BuildNewOrderSingleV2(tif: (byte)'6', expireDate: 20_000);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg, MarketDate(20_000));

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal((ushort)20_000, cmd.ExpireDate);
    }

    [Fact]
    public void NewOrderSingle_GtdFutureExpireDate_Accepted()
    {
        var body = BuildNewOrderSingleV2(tif: (byte)'6', expireDate: 20_001);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _, MarketDate(20_000));

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal((ushort)20_001, cmd.ExpireDate);
    }

    [Fact]
    public void NewOrderSingle_GtdPastExpireDate_ProviderReturnsNull_Accepted()
    {
        // A null provider result (e.g. date out of LocalMktDate range) skips
        // the check: legacy "accept, swept next day" behavior is preserved.
        var opts = new InboundFatFingerOptions { CurrentMarketDateProvider = () => null };
        var body = BuildNewOrderSingleV2(tif: (byte)'6', expireDate: 19_999);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _, opts);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal((ushort)19_999, cmd.ExpireDate);
    }

    [Fact]
    public void NewOrderSingle_GtdPastExpireDate_NoProvider_Accepted()
    {
        // No options / no provider wired: the guard is inert (the engine is
        // clockless and the daily sweep removes the order).
        var body = BuildNewOrderSingleV2(tif: (byte)'6', expireDate: 19_999);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal((ushort)19_999, cmd.ExpireDate);
    }

    [Fact]
    public void NewOrderSingle_NonGtd_MarketDateProvider_NotConsulted()
    {
        // A Day order carries no ExpireDate and must be unaffected by the
        // market-date guard even when a provider is present.
        var body = BuildNewOrderSingleV2(tif: (byte)'0', expireDate: 0);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _, MarketDate(20_000));

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(TimeInForce.Day, cmd.Tif);
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
    public void NewOrderSingle_StopPxOnNonStopRejectsAsUnsupported()
    {
        // #214: StopPx on a non-stop order is still a decoder-level
        // unsupported feature.
        var body = BuildNewOrderSingleV2(stopPx: 11_0000L);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Stop", msg);
    }

    [Theory]
    [InlineData((byte)'3', OrderType.StopLoss)]
    [InlineData((byte)'4', OrderType.StopLimit)]
    public void NewOrderSingle_AcceptsStopOrders(byte ordTypeByte, OrderType expected)
    {
        // #214: StopLoss/StopLimit accepted with StopPx populated. The
        // decoder forwards StopPx to the engine; engine validates band/
        // tick/MaxFloor/MinQty/TIF rules.
        long pricePx = expected == OrderType.StopLimit ? 10_0000L : PriceNull;
        var body = BuildNewOrderSingleV2(ordType: ordTypeByte, stopPx: 11_0000L,
            price: pricePx);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd.Type);
        Assert.Equal(11_0000L, cmd.StopPxMantissa);
    }

    [Theory]
    [InlineData((byte)'3')]
    [InlineData((byte)'4')]
    public void NewOrderSingle_StopWithoutStopPxRejectsAsUnsupported(byte ordTypeByte)
    {
        // #214: Stop ord types require StopPx.
        var body = BuildNewOrderSingleV2(ordType: ordTypeByte, stopPx: PriceNull);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Stop", msg);
    }

    [Fact]
    public void NewOrderSingle_AcceptsMwl()
    {
        // #215: MARKET_WITH_LEFTOVER_AS_LIMIT (FIX 'K') decoded with
        // engine Price=0 (resting price derived from last trade at runtime).
        var body = BuildNewOrderSingleV2(ordType: (byte)'K', price: PriceNull, tif: (byte)'0');

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.NotNull(cmd);
        Assert.Equal(OrderType.MarketWithLeftover, cmd.Type);
        Assert.Equal(0L, cmd.PriceMantissa);
    }

    [Fact]
    public void NewOrderSingle_AcceptsMaxFloor()
    {
        // #211: iceberg accepted on NewOrderSingle. The decoder forwards
        // MaxFloor to the engine; engine validates lot/multiple/Type/TIF.
        var body = BuildNewOrderSingleV2(maxFloor: 10UL, qty: 100L);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(10UL, cmd.MaxFloor);
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
    public void NewOrderSingle_QtyOverLimitReturnsErRejectCommand()
    {
        var body = BuildNewOrderSingleV2(qty: 1_001);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg, TightGuardrails);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal(RejectReason.QuantityExceedsLimit, cmd.PreTradeRejectReason);
        Assert.Equal(99u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("OrderQty", msg);
    }

    [Fact]
    public void NewOrderSingle_PriceOverAbsoluteMaxReturnsErRejectCommand()
    {
        var body = BuildNewOrderSingleV2(price: 1_000_001);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg, TightGuardrails);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, cmd.PreTradeRejectReason);
        Assert.Equal(16u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("Price", msg);
    }

    [Fact]
    public void NewOrderSingle_BoundaryValuesAtLimitsAreAccepted()
    {
        var body = BuildNewOrderSingleV2(qty: 1_000, price: 1_000_000);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg, TightGuardrails);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(1_000, cmd.Quantity);
        Assert.Equal(1_000_000, cmd.PriceMantissa);
    }

    [Fact]
    public void NewOrderSingle_PriceBandEnabledInBandPassesOutOfBandRejects()
    {
        var options = TightGuardrails with
        {
            MaxPriceMantissa = 2_000_000,
            PriceBandPercent = 10m,
            LastTradePriceProvider = _ => 1_000_000,
        };

        var inBand = InboundMessageDecoder.TryDecodeNewOrderSingle(
            BuildNewOrderSingleV2(price: 1_100_000), 1, 0, out var accepted, out _, out var inBandMsg, options);
        var outOfBand = InboundMessageDecoder.TryDecodeNewOrderSingle(
            BuildNewOrderSingleV2(price: 1_100_001), 1, 0, out var rejected, out _, out var outOfBandMsg, options);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, inBand);
        Assert.Null(inBandMsg);
        Assert.Equal(1_100_000, accepted.PriceMantissa);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outOfBand);
        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, rejected.PreTradeRejectReason);
        Assert.Contains("Price", outOfBandMsg);
    }

    [Fact]
    public void NewOrderSingle_PriceBandDisabledOrNoReferencePasses()
    {
        var disabled = TightGuardrails with
        {
            MaxPriceMantissa = 2_000_000,
            PriceBandPercent = null,
            LastTradePriceProvider = _ => 1_000_000,
        };
        var noReference = TightGuardrails with
        {
            MaxPriceMantissa = 2_000_000,
            PriceBandPercent = 10m,
            LastTradePriceProvider = _ => null,
        };

        var disabledOutcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            BuildNewOrderSingleV2(price: 1_500_000), 1, 0, out _, out _, out var disabledMsg, disabled);
        var noReferenceOutcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            BuildNewOrderSingleV2(price: 1_500_000), 1, 0, out _, out _, out var noReferenceMsg, noReference);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, disabledOutcome);
        Assert.Null(disabledMsg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, noReferenceOutcome);
        Assert.Null(noReferenceMsg);
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

    /// <summary>
    /// #241: B3.EntryPoint.Client 0.8.0 has no public surface to set
    /// RoutingInstruction so it always emits the .NET enum default 0.
    /// 0 is not a schema-defined non-null value (1..4) and must be
    /// treated as synonymous with NULL — accept silently, do not reject.
    /// </summary>
    [Theory]
    [InlineData((byte)0)]   // EPC 0.8.0 SDK default
    [InlineData((byte)255)] // explicit NULL
    public void NewOrderSingle_RoutingInstructionDefaultOrNull_IsAccepted(byte routing)
    {
        var body = BuildNewOrderSingleV2(routing: routing);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
    }

    /// <summary>
    /// Same default-vs-null acceptance for the Cancel/Replace path —
    /// EPC 0.8.0's Replace surface has the same gap as the new-order
    /// surface, so a Replace on an existing order also carries
    /// routing=0.
    /// </summary>
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void OrderCancelReplace_RoutingInstructionDefaultOrNull_IsAccepted(byte routing)
    {
        var body = BuildOrderCancelReplaceV2(routing: routing);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 1, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
    }

    [Fact]
    public void OrderCancelReplace_QtyOverLimitReturnsErRejectCommand()
    {
        var body = BuildOrderCancelReplaceV2(qty: 1_001);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 1, out var cmd, out _, out _, out var msg, TightGuardrails);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal(RejectReason.QuantityExceedsLimit, cmd.PreTradeRejectReason);
        Assert.Equal(99u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("NewQuantity", msg);
    }

    [Fact]
    public void OrderCancelReplace_PriceOverAbsoluteMaxReturnsErRejectCommand()
    {
        var body = BuildOrderCancelReplaceV2(price: 1_000_001);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 1, out var cmd, out _, out _, out var msg, TightGuardrails);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, cmd.PreTradeRejectReason);
        Assert.Equal(16u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("Price", msg);
    }

    [Theory]
    [InlineData((byte)'W')] // RLP
    [InlineData((byte)'P')] // PEGGED_MIDPOINT
    public void NewOrderSingle_UnsupportedOrdTypeReturnsEngineRejectCommand(byte ordTypeByte)
    {
        var body = BuildNewOrderSingleV2(ordType: ordTypeByte);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 1, 0, out var cmd, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.True(cmd.UnsupportedOrderCharacteristic);
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

    // GAP-23 / #499: an ExpireDate on a non-GTD order is rejected as
    // unsupported (the field is only meaningful with GTD time-in-force).
    [Fact]
    public void NewOrderSingle_ExpireDateWithoutGtd_RejectsAsUnsupported()
    {
        var body = BuildNewOrderSingleV2(expireDate: 0x1234);

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

    // Issue #451: B3 Binary EntryPoint Messaging Guidelines §7.4 allows
    // InvestorID mutation via OCRR (Add ✓, Change ✓). A non-zero composite
    // on the OCRR body must surface on ReplaceOrderCommand.NewInvestorId so
    // the engine can apply it to the resting order.
    [Fact]
    public void OrderCancelReplace_InvestorIdNonZero_PopulatesNewInvestorId()
    {
        var body = BuildOrderCancelReplaceV2(investorPrefix: 7, investorDocument: 123_456);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.NotNull(cmd.NewInvestorId);
        Assert.Equal((ushort)7, cmd.NewInvestorId!.Value.Prefix);
        Assert.Equal(123_456u, cmd.NewInvestorId.Value.Document);
    }

    // Spec §7.4 footer: "Fields that are not sent will be considered the
    // same as the original order." An all-zero composite is the wire NULL
    // sentinel → command carries null → engine preserves resting value.
    [Fact]
    public void OrderCancelReplace_InvestorIdAllZero_LeavesNewInvestorIdNull()
    {
        var body = BuildOrderCancelReplaceV2(); // defaults leave composite all-zero

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(cmd.NewInvestorId);
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
    public void OrderCancelReplace_MarketAccepted()
    {
        // #204: Market replace is supported; price is ignored by the engine
        // for market orders, so the decoder zero-fills it.
        var body = BuildOrderCancelReplaceV2(ordType: (byte)'1', tif: (byte)'3');

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(OrderType.Market, cmd.NewOrdType);
        Assert.Equal(TimeInForce.IOC, cmd.NewTif);
        Assert.Equal(0L, cmd.NewPriceMantissa);
    }

    [Theory]
    [InlineData((byte)'3', TimeInForce.IOC)]
    [InlineData((byte)'4', TimeInForce.FOK)]
    public void OrderCancelReplace_NonDayTifAccepted(byte tifByte, TimeInForce expected)
    {
        // #204: every TIF the engine accepts on a NewOrderSingle is now
        // valid on replace too. The decoder propagates it as the optional
        // ReplaceOrderCommand.NewTif so the engine can apply it on the
        // priority-loss re-entry path.
        var body = BuildOrderCancelReplaceV2(tif: tifByte);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(expected, cmd.NewTif);
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

    // GAP-23 / #499: a replace now plumbs ExpireDate through as
    // NewExpireDate and defers GTD pairing validation to the engine (the
    // wire TimeInForce may be omitted/preserved on a replace).
    [Fact]
    public void OrderCancelReplace_ExpireDate_PopulatesNewExpireDate()
    {
        var body = BuildOrderCancelReplaceV2(expireDate: 0x1234);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal((ushort)0x1234, cmd.NewExpireDate);
    }

    // A replace with no ExpireDate on the wire leaves NewExpireDate null
    // (preserve the resting order's existing expiry).
    [Fact]
    public void OrderCancelReplace_NoExpireDate_LeavesNewExpireDateNull()
    {
        var body = BuildOrderCancelReplaceV2(expireDate: 0);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(cmd.NewExpireDate);
    }

    [Fact]
    public void OrderCancelReplace_PopulatesCurrentMarketDateFromProvider()
    {
        var body = BuildOrderCancelReplaceV2(expireDate: 19_999);
        int calls = 0;
        var opts = new InboundFatFingerOptions
        {
            CurrentMarketDateProvider = () =>
            {
                calls++;
                return 20_000;
            },
        };

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out var msg, opts);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal((ushort)20_000, cmd.CurrentMarketDate);
        Assert.Equal(1, calls);
    }

    // ---------------- #238: V6 trailer (StrategyID + TradingSubAccount) ----------------

    private static byte[] BuildNewOrderSingleV6(
        int strategyId = 0, uint tradingSubAccount = 0)
    {
        var v2 = BuildNewOrderSingleV2();
        // V6 root is 133 bytes: V2 root (125) + strategyID(int32) + tradingSubAccount(uint32).
        var body = new byte[133];
        v2.CopyTo(body, 0);
        Span<byte> s = body;
        MemoryMarshal.Write(s.Slice(125, 4), in strategyId);
        MemoryMarshal.Write(s.Slice(129, 4), in tradingSubAccount);
        return body;
    }

    private static byte[] BuildOrderCancelReplaceV6(
        int strategyId = 0, uint tradingSubAccount = 0)
    {
        var v2 = BuildOrderCancelReplaceV2();
        var body = new byte[150];
        v2.CopyTo(body, 0);
        Span<byte> s = body;
        MemoryMarshal.Write(s.Slice(142, 4), in strategyId);
        MemoryMarshal.Write(s.Slice(146, 4), in tradingSubAccount);
        return body;
    }

    [Fact]
    public void NewOrderSingle_V6_NullTrailer_HappyPath()
    {
        var body = BuildNewOrderSingleV6();

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 7, 1_000_000UL, out var cmd, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(OrderType.Limit, cmd.Type);
    }

    [Fact]
    public void NewOrderSingle_V6_StrategyID_Rejected()
    {
        var body = BuildNewOrderSingleV6(strategyId: 42);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 7, 1_000_000UL, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("StrategyID", msg);
    }

    [Fact]
    public void NewOrderSingle_V6_TradingSubAccount_Rejected()
    {
        var body = BuildNewOrderSingleV6(tradingSubAccount: 99u);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, 7, 1_000_000UL, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("TradingSubAccount", msg);
    }

    [Fact]
    public void OrderCancelReplace_V6_NullTrailer_HappyPath()
    {
        var body = BuildOrderCancelReplaceV6();

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out var cmd, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(42L, cmd.OrderId);
    }

    [Fact]
    public void OrderCancelReplace_V6_StrategyID_Rejected()
    {
        var body = BuildOrderCancelReplaceV6(strategyId: 7);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("StrategyID", msg);
    }

    [Fact]
    public void OrderCancelReplace_V6_TradingSubAccount_Rejected()
    {
        var body = BuildOrderCancelReplaceV6(tradingSubAccount: 12345u);

        var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
            body, 0UL, out _, out _, out _, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("TradingSubAccount", msg);
    }
}
