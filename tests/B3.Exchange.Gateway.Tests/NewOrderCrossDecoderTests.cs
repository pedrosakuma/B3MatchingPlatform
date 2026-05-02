using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Unit tests for #GAP-16 — NewOrderCross (template 106) decoder.
/// Validates the supported subset (Limit cross, two sides, no STP /
/// CrossType / CrossPrioritization / MaxSweepQty extras), the
/// UnsupportedFeature branch (caller emits BMR(33003)), and the
/// DecodeError branch (caller terminates with DECODING_ERROR).
/// </summary>
public class NewOrderCrossDecoderTests
{
    private const long PriceNull = long.MinValue;
    private const uint FirmNull = 0xFFFFFFFFu;
    private const uint SessionFirm = 4242u;

    /// <summary>
    /// Builds a NewOrderCross body (root 84B + NoSides group + DeskID + Memo)
    /// matching the V6 schema. Both legs share the same OrderQty / Price
    /// taken from the root block; the per-side overrides only carry
    /// EnteringFirm + ClOrdID + Account + TradingSubAccount per the schema.
    /// </summary>
    private static byte[] BuildCross(
        ulong crossId = 7777UL,
        long secId = 12345L,
        long qty = 100L,
        long price = 100_0000L,
        byte ordType = 0, // null = implicit Limit
        byte crossType = 255,
        byte crossPri = 255,
        ulong maxSweepQty = 0UL,
        byte side0 = (byte)'1', byte side1 = (byte)'2',
        ulong clOrd0 = 1001UL, ulong clOrd1 = 1002UL,
        uint firm0 = FirmNull, uint firm1 = FirmNull,
        byte numInGroup = 2,
        ushort groupBlockLength = 22,
        byte deskIdLen = 0, byte memoLen = 0,
        int? trailingExtraBytes = null)
    {
        int sidesCount = numInGroup;
        int sidesBytes = sidesCount * 22;
        int extra = trailingExtraBytes ?? 0;
        int total = 84 + 3 + sidesBytes + 1 + deskIdLen + 1 + memoLen + extra;
        var body = new byte[total];
        Span<byte> s = body;

        // Root block.
        s[18] = ordType;
        MemoryMarshal.Write(s.Slice(20, 8), in crossId);
        MemoryMarshal.Write(s.Slice(48, 8), in secId);
        ulong qtyU = (ulong)qty;
        MemoryMarshal.Write(s.Slice(56, 8), in qtyU);
        MemoryMarshal.Write(s.Slice(64, 8), in price);
        ushort crossedIndNull = 65535;
        MemoryMarshal.Write(s.Slice(72, 2), in crossedIndNull);
        s[74] = crossType;
        s[75] = crossPri;
        MemoryMarshal.Write(s.Slice(76, 8), in maxSweepQty);

        // NoSides group header (3-byte: ushort blockLength + byte numInGroup).
        int cursor = 84;
        MemoryMarshal.Write(s.Slice(cursor, 2), in groupBlockLength);
        s[cursor + 2] = numInGroup;
        cursor += 3;

        // Sides entries.
        if (sidesCount >= 1)
        {
            s[cursor + 0] = side0;
            MemoryMarshal.Write(s.Slice(cursor + 6, 4), in firm0);
            MemoryMarshal.Write(s.Slice(cursor + 10, 8), in clOrd0);
            cursor += 22;
        }
        if (sidesCount >= 2)
        {
            s[cursor + 0] = side1;
            MemoryMarshal.Write(s.Slice(cursor + 6, 4), in firm1);
            MemoryMarshal.Write(s.Slice(cursor + 10, 8), in clOrd1);
            cursor += 22;
        }

        // varData: DeskID (length-prefixed) + Memo (length-prefixed).
        s[cursor++] = deskIdLen;
        cursor += deskIdLen;
        s[cursor++] = memoLen;
        cursor += memoLen;

        return body;
    }

    [Fact]
    public void Success_LimitCross_TwoSidesSameFirm_ProducesBuyAndSellLegs()
    {
        var body = BuildCross();
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, enteredAtNanos: 1_000UL,
            out var cross, out ulong crossId, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(7777UL, crossId);
        Assert.Equal(7777UL, cross.CrossId);

        Assert.Equal(Side.Buy, cross.Buy.Side);
        Assert.Equal(Side.Sell, cross.Sell.Side);
        Assert.Equal(12345L, cross.Buy.SecurityId);
        Assert.Equal(12345L, cross.Sell.SecurityId);
        Assert.Equal(100L, cross.Buy.Quantity);
        Assert.Equal(100L, cross.Sell.Quantity);
        Assert.Equal(100_0000L, cross.Buy.PriceMantissa);
        Assert.Equal(100_0000L, cross.Sell.PriceMantissa);
        Assert.Equal(OrderType.Limit, cross.Buy.Type);
        Assert.Equal(OrderType.Limit, cross.Sell.Type);
        Assert.Equal(SessionFirm, cross.Buy.EnteringFirm);
        Assert.Equal(SessionFirm, cross.Sell.EnteringFirm);
        Assert.Equal(1_000UL, cross.Buy.EnteredAtNanos);
        Assert.Equal(1_000UL, cross.Sell.EnteredAtNanos);
        Assert.Equal(1001UL, cross.BuyClOrdIdValue);
        Assert.Equal(1002UL, cross.SellClOrdIdValue);
    }

    [Fact]
    public void Success_SidesInSellBuyOrder_StillProducesCanonicalBuyFirstLegs()
    {
        // Send sell-then-buy in the wire: decoder should canonicalize so
        // cross.Buy is always the buy leg regardless of group order.
        var body = BuildCross(
            side0: (byte)'2', side1: (byte)'1',
            clOrd0: 9001UL, clOrd1: 9002UL);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out var cross, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(9002UL, cross.BuyClOrdIdValue);
        Assert.Equal(9001UL, cross.SellClOrdIdValue);
    }

    [Theory]
    [InlineData((byte)1, "exactly 2 sides")]   // only one side
    [InlineData((byte)3, "exactly 2 sides")]   // three sides
    public void UnsupportedFeature_NumInGroupNotTwo(byte numInGroup, string expectedFragment)
    {
        var body = BuildCross(numInGroup: numInGroup);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains(expectedFragment, msg);
    }

    [Fact]
    public void UnsupportedFeature_DuplicateSides()
    {
        var body = BuildCross(side0: (byte)'1', side1: (byte)'1');
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("one Buy and one Sell", msg);
    }

    [Fact]
    public void UnsupportedFeature_NonNullCrossType()
    {
        var body = BuildCross(crossType: 1); // any non-null
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("CrossType", msg);
    }

    [Fact]
    public void UnsupportedFeature_NonNullCrossPrioritization()
    {
        var body = BuildCross(crossPri: 0);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("CrossPrioritization", msg);
    }

    [Fact]
    public void UnsupportedFeature_NonZeroMaxSweepQty()
    {
        var body = BuildCross(maxSweepQty: 50UL);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("MaxSweepQty", msg);
    }

    [Fact]
    public void UnsupportedFeature_MarketOrdType()
    {
        var body = BuildCross(ordType: (byte)'1');
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("OrdType", msg);
    }

    [Fact]
    public void UnsupportedFeature_PriceNull()
    {
        var body = BuildCross(price: PriceNull);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Price", msg);
    }

    [Fact]
    public void UnsupportedFeature_PerSideFirmDiffersFromSession()
    {
        var body = BuildCross(firm0: 9999u, firm1: FirmNull);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("EnteringFirm", msg);
    }

    [Fact]
    public void Success_PerSideFirmEqualsSessionFirm()
    {
        var body = BuildCross(firm0: SessionFirm, firm1: SessionFirm);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out var cross, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(SessionFirm, cross.Buy.EnteringFirm);
    }

    [Fact]
    public void DecodeError_GroupBlockLengthMismatch()
    {
        var body = BuildCross(groupBlockLength: 23);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("blockLength", msg);
    }

    [Fact]
    public void DecodeError_TrailingBytesAfterVarData()
    {
        var body = BuildCross(trailingExtraBytes: 5);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("trailing", msg);
    }

    [Fact]
    public void DecodeError_VarDataTruncated()
    {
        // Build a cross whose deskID declares 10 bytes of payload but
        // truncate the buffer so the declared length overruns it. The
        // shared validator (EntryPointVarData.ValidateDetailed) classifies
        // an over-running length prefix as a structural protocol error
        // (DECODING_ERROR), distinct from an over-length payload (which
        // is a §4.10 BMR — see UnsupportedFeature_DeskIdTooLong).
        var body = BuildCross(deskIdLen: 10);
        var truncated = body.AsSpan(0, body.Length - 5).ToArray();
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            truncated, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("deskID", msg);
    }
}
