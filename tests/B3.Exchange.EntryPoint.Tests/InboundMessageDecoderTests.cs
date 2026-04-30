using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;
using B3.Exchange.Matching;

namespace B3.Exchange.EntryPoint.Tests;

public class InboundMessageDecoderTests
{
    [Fact]
    public void DecodesSimpleNewOrderLimitDay()
    {
        Span<byte> body = stackalloc byte[82];
        body.Clear();

        ulong clOrdId = 12345UL;
        long secId = 99887766L;
        long qty = 100L;
        long price = 12_3450L;

        MemoryMarshal.Write(body.Slice(20, 8), in clOrdId);
        MemoryMarshal.Write(body.Slice(48, 8), in secId);
        body[56] = (byte)'1';     // Side = Buy
        body[57] = (byte)'2';     // OrdType = Limit
        body[58] = (byte)'0';     // TimeInForce = Day
        MemoryMarshal.Write(body.Slice(60, 8), in qty);
        MemoryMarshal.Write(body.Slice(68, 8), in price);

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
    }

    [Fact]
    public void RejectsCancelWithoutOrderId()
    {
        Span<byte> body = stackalloc byte[76];
        body.Clear();
        var ok = InboundMessageDecoder.TryDecodeCancel(body, 0UL, out _, out _, out _, out var err);
        Assert.False(ok);
        Assert.Contains("OrderID", err);
    }

    [Fact]
    public void RejectsReplaceWithoutOrderId()
    {
        Span<byte> body = stackalloc byte[98];
        body.Clear();
        var ok = InboundMessageDecoder.TryDecodeReplace(body, 0UL, out _, out _, out _, out var err);
        Assert.False(ok);
        Assert.Contains("OrderID", err);
    }
}
