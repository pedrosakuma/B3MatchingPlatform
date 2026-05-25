using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

public class InboundMessageDecoderTests
{
    private static readonly InboundFatFingerOptions TightGuardrails = new()
    {
        MaxOrderQty = 1_000,
        MaxPriceMantissa = 1_000_000,
    };

    private static byte[] BuildSimpleNewOrder(
        ulong clOrdId = 12345UL,
        long secId = 99887766L,
        long qty = 100L,
        long price = 12_3450L,
        byte ordTagId = 0,
        ushort investorPrefix = 0,
        uint investorDocument = 0)
    {
        var body = new byte[82];
        Span<byte> s = body;
        s[18] = ordTagId;
        MemoryMarshal.Write(s.Slice(20, 8), in clOrdId);
        MemoryMarshal.Write(s.Slice(48, 8), in secId);
        s[56] = (byte)'1';
        s[57] = (byte)'2';
        s[58] = (byte)'0';
        MemoryMarshal.Write(s.Slice(60, 8), in qty);
        MemoryMarshal.Write(s.Slice(68, 8), in price);
        MemoryMarshal.Write(s.Slice(76, 2), in investorPrefix);
        MemoryMarshal.Write(s.Slice(78, 4), in investorDocument);
        return body;
    }

    private static byte[] BuildSimpleModifyOrder(
        ulong clOrdId = 99UL,
        long secId = 555L,
        long qty = 200L,
        long price = 100_000L,
        ulong orderId = 42UL,
        ulong origClOrdId = 7UL)
    {
        var body = new byte[98];
        Span<byte> s = body;
        MemoryMarshal.Write(s.Slice(20, 8), in clOrdId);
        MemoryMarshal.Write(s.Slice(48, 8), in secId);
        MemoryMarshal.Write(s.Slice(60, 8), in qty);
        MemoryMarshal.Write(s.Slice(68, 8), in price);
        MemoryMarshal.Write(s.Slice(76, 8), in orderId);
        MemoryMarshal.Write(s.Slice(84, 8), in origClOrdId);
        return body;
    }

    [Fact]
    public void DecodesSimpleNewOrderLimitDay()
    {
        ulong clOrdId = 12345UL;
        long secId = 99887766L;
        long qty = 100L;
        long price = 12_3450L;
        var body = BuildSimpleNewOrder(clOrdId, secId, qty, price);

        var ok = InboundMessageDecoder.TryDecodeNewOrder(body, enteringFirm: 7, enteredAtNanos: 1_000_000UL,
            out var cmd, out var clOrdValue, out var err);

        Assert.True(ok, err);
        Assert.Equal(clOrdId, clOrdValue);
        Assert.Equal(clOrdId.ToString(), cmd.ClOrdId);
        Assert.Equal(secId, cmd.SecurityId);
        Assert.Equal(Side.Buy, cmd.Side);
        Assert.Equal(OrderType.Limit, cmd.Type);
        Assert.Equal(TimeInForce.Day, cmd.Tif);
        Assert.Equal(qty, cmd.Quantity);
        Assert.Equal(price, cmd.PriceMantissa);
        Assert.Equal(7u, cmd.EnteringFirm);
        Assert.Equal((byte)0, cmd.OrdTagId);
        Assert.Null(cmd.InvestorId);
    }

    [Fact]
    public void SimpleNewOrder_PopulatesOrdTagIdAndInvestorId()
    {
        // #449 — SimpleNewOrder must propagate ordTagID (id 35505) and
        // investorID (id 35508) so mass-cancel-on-behalf filters work.
        var body = BuildSimpleNewOrder(ordTagId: 99, investorPrefix: 7, investorDocument: 123456789);

        var ok = InboundMessageDecoder.TryDecodeNewOrder(body, enteringFirm: 7, enteredAtNanos: 1_000_000UL,
            out var cmd, out _, out var err);

        Assert.True(ok, err);
        Assert.Equal((byte)99, cmd.OrdTagId);
        Assert.NotNull(cmd.InvestorId);
        Assert.Equal((ushort)7, cmd.InvestorId!.Value.Prefix);
        Assert.Equal(123456789u, cmd.InvestorId!.Value.Document);
    }

    [Fact]
    public void SimpleNewOrder_AllZeroInvestorIdDecodesAsNull()
    {
        var body = BuildSimpleNewOrder(ordTagId: 0, investorPrefix: 0, investorDocument: 0);

        var ok = InboundMessageDecoder.TryDecodeNewOrder(body, enteringFirm: 7, enteredAtNanos: 1_000_000UL,
            out var cmd, out _, out var err);

        Assert.True(ok, err);
        Assert.Equal((byte)0, cmd.OrdTagId);
        Assert.Null(cmd.InvestorId);
    }

    [Fact]
    public void SimpleNewOrder_QtyOverLimitReturnsErRejectCommand()
    {
        var body = BuildSimpleNewOrder(qty: 1_001);

        var ok = InboundMessageDecoder.TryDecodeNewOrder(body, 7, 1_000_000UL,
            out var cmd, out _, out var msg, TightGuardrails);

        Assert.True(ok);
        Assert.Equal(RejectReason.QuantityExceedsLimit, cmd.PreTradeRejectReason);
        Assert.Equal(99u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("OrderQty", msg);
    }

    [Fact]
    public void SimpleNewOrder_PriceOverAbsoluteMaxReturnsErRejectCommand()
    {
        var body = BuildSimpleNewOrder(price: 1_000_001);

        var ok = InboundMessageDecoder.TryDecodeNewOrder(body, 7, 1_000_000UL,
            out var cmd, out _, out var msg, TightGuardrails);

        Assert.True(ok);
        Assert.Equal(RejectReason.PriceExceedsCurrentPriceBand, cmd.PreTradeRejectReason);
        Assert.Equal(16u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("Price", msg);
    }

    [Fact]
    public void RejectsCancelWithoutOrderIdAndOrigClOrdId()
    {
        Span<byte> body = stackalloc byte[76];
        body.Clear();
        var ok = InboundMessageDecoder.TryDecodeCancel(body, 0UL, out _, out _, out _, out var err);
        Assert.False(ok);
        Assert.Contains("OrderID", err);
        Assert.Contains("OrigClOrdID", err);
    }

    [Fact]
    public void RejectsReplaceWithoutOrderIdAndOrigClOrdId()
    {
        Span<byte> body = stackalloc byte[98];
        body.Clear();
        var ok = InboundMessageDecoder.TryDecodeReplace(body, 0UL, out _, out _, out _, out var err);
        Assert.False(ok);
        Assert.Contains("OrderID", err);
        Assert.Contains("OrigClOrdID", err);
    }

    [Fact]
    public void DecodesCancelByOrigClOrdIdOnly()
    {
        Span<byte> body = stackalloc byte[76];
        body.Clear();
        ulong clOrdId = 42UL;
        long secId = 123L;
        ulong orderId = 0UL;          // not provided
        ulong origClOrdId = 7UL;     // resolution key
        MemoryMarshal.Write(body.Slice(20, 8), in clOrdId);
        MemoryMarshal.Write(body.Slice(28, 8), in secId);
        MemoryMarshal.Write(body.Slice(36, 8), in orderId);
        MemoryMarshal.Write(body.Slice(44, 8), in origClOrdId);
        body[52] = (byte)'1';

        var ok = InboundMessageDecoder.TryDecodeCancel(body, 9_000UL,
            out var cmd, out var clOrd, out var origClOrd, out var err);

        Assert.True(ok, err);
        Assert.Equal(clOrdId, clOrd);
        Assert.Equal(origClOrdId, origClOrd);
        Assert.Equal(0L, cmd.OrderId);   // unresolved at decode time
        Assert.Equal(secId, cmd.SecurityId);
    }

    [Fact]
    public void DecodesReplaceByOrigClOrdIdOnly()
    {
        ulong clOrdId = 99UL;
        long secId = 555L;
        long qty = 200L;
        long price = 100_000L;
        ulong origClOrdId = 42UL;
        var body = BuildSimpleModifyOrder(clOrdId, secId, qty, price, orderId: 0UL, origClOrdId: origClOrdId);

        var ok = InboundMessageDecoder.TryDecodeReplace(body, 9_000UL,
            out var cmd, out var clOrd, out var origClOrd, out var err);

        Assert.True(ok, err);
        Assert.Equal(clOrdId, clOrd);
        Assert.Equal(origClOrdId, origClOrd);
        Assert.Equal(0L, cmd.OrderId);
        Assert.Equal(qty, cmd.NewQuantity);
        Assert.Equal(price, cmd.NewPriceMantissa);
    }

    [Fact]
    public void SimpleModifyOrder_QtyOverLimitReturnsErRejectCommand()
    {
        var body = BuildSimpleModifyOrder(qty: 1_001);

        var ok = InboundMessageDecoder.TryDecodeReplace(body, 9_000UL,
            out var cmd, out _, out _, out var msg, TightGuardrails);

        Assert.True(ok);
        Assert.Equal(RejectReason.QuantityExceedsLimit, cmd.PreTradeRejectReason);
        Assert.Equal(99u, FixpSession.MapRejectReason(cmd.PreTradeRejectReason.GetValueOrDefault()));
        Assert.Contains("NewQuantity", msg);
    }
}
