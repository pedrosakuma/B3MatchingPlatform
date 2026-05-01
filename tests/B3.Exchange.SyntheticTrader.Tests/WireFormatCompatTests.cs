using B3.Exchange.EntryPoint;
using B3.Exchange.Matching;

namespace B3.Exchange.SyntheticTrader.Tests;

/// <summary>
/// Wire-format compatibility tests. The synthetic trader's
/// <see cref="EntryPointClient"/> hand-duplicates the SBE field offsets used
/// by the host's <c>InboundMessageDecoder</c> / <c>ExecutionReportEncoder</c>.
/// These tests round-trip every supported template through both sides and
/// assert byte-for-byte field equality, so any future drift in either set
/// of offsets fails CI instead of silently breaking the synth trader at
/// runtime.
/// </summary>
public class WireFormatCompatTests
{
    private const int SbeHeaderSize = EntryPointFrameReader.WireHeaderSize;

    [Fact]
    public void SimpleNewOrder_RoundTripsThroughHostInboundDecoder()
    {
        Span<byte> frame = stackalloc byte[SbeHeaderSize + 82];
        const ulong clOrd = 0xDEADBEEFCAFEBABEUL;
        const long secId = 900_000_000_001L;
        const long qty = 1234;
        const long px = 12_345_6789L;
        int written = EntryPointClient.EncodeNewOrder(frame, clOrd, secId,
            OrderSide.Sell, OrderTypeIntent.Limit, OrderTifIntent.IOC, qty, px);
        Assert.Equal(frame.Length, written);

        // Validate the SBE header reads back exactly.
        Assert.True(EntryPointFrameReader.TryParseInboundHeader(frame.Slice(0, SbeHeaderSize), out var info, out _));
        Assert.Equal(EntryPointFrameReader.TidSimpleNewOrder, info.TemplateId);
        Assert.Equal((ushort)2, info.Version);
        Assert.Equal(82, info.BodyLength);

        // Decode the body via the host's decoder.
        var body = frame.Slice(SbeHeaderSize);
        Assert.True(InboundMessageDecoder.TryDecodeNewOrder(body, enteringFirm: 7, enteredAtNanos: 0,
            out var cmd, out var clOrdOut, out var err), $"decode failed: {err}");
        Assert.Equal(clOrd, clOrdOut);
        Assert.Equal(clOrd.ToString(), cmd.ClOrdId);
        Assert.Equal(secId, cmd.SecurityId);
        Assert.Equal(Side.Sell, cmd.Side);
        Assert.Equal(OrderType.Limit, cmd.Type);
        Assert.Equal(TimeInForce.IOC, cmd.Tif);
        Assert.Equal(qty, cmd.Quantity);
        Assert.Equal(px, cmd.PriceMantissa);
        Assert.Equal(7u, cmd.EnteringFirm);
    }

    [Theory]
    [InlineData(OrderSide.Buy, OrderTypeIntent.Limit, OrderTifIntent.Day)]
    [InlineData(OrderSide.Sell, OrderTypeIntent.Limit, OrderTifIntent.FOK)]
    [InlineData(OrderSide.Buy, OrderTypeIntent.Market, OrderTifIntent.IOC)]
    public void SimpleNewOrder_AllSideTypeTif_DecodeMatches(OrderSide side, OrderTypeIntent type, OrderTifIntent tif)
    {
        Span<byte> frame = stackalloc byte[SbeHeaderSize + 82];
        EntryPointClient.EncodeNewOrder(frame, clOrdId: 42, securityId: 1, side: side, type: type, tif: tif,
            qty: 100, priceMantissa: type == OrderTypeIntent.Market ? long.MinValue : 100_0000);
        var body = frame.Slice(SbeHeaderSize);
        Assert.True(InboundMessageDecoder.TryDecodeNewOrder(body, 1, 0, out var cmd, out _, out _));
        Assert.Equal(side == OrderSide.Buy ? Side.Buy : Side.Sell, cmd.Side);
        Assert.Equal(type == OrderTypeIntent.Market ? OrderType.Market : OrderType.Limit, cmd.Type);
        Assert.Equal(tif switch
        {
            OrderTifIntent.Day => TimeInForce.Day,
            OrderTifIntent.IOC => TimeInForce.IOC,
            _ => TimeInForce.FOK,
        }, cmd.Tif);
    }

    [Fact]
    public void OrderCancelRequest_RoundTripsThroughHostInboundDecoder()
    {
        Span<byte> frame = stackalloc byte[SbeHeaderSize + 76];
        const ulong clOrd = 0x0102030405060708UL;
        const ulong origClOrd = 0x1111222233334444UL;
        const long secId = 900_000_000_002L;
        const ulong orderId = 0x55_66_77_88_99_AA_BB_CCUL;
        int written = EntryPointClient.EncodeCancel(frame, clOrd, secId, orderId, origClOrd, OrderSide.Sell);
        Assert.Equal(frame.Length, written);

        Assert.True(EntryPointFrameReader.TryParseInboundHeader(frame.Slice(0, SbeHeaderSize), out var info, out _));
        Assert.Equal(EntryPointFrameReader.TidOrderCancelRequest, info.TemplateId);
        Assert.Equal(76, info.BodyLength);

        var body = frame.Slice(SbeHeaderSize);
        Assert.True(InboundMessageDecoder.TryDecodeCancel(body, enteredAtNanos: 0,
            out var cmd, out var clOrdOut, out var origClOrdOut, out var err), $"decode failed: {err}");
        Assert.Equal(clOrd, clOrdOut);
        Assert.Equal(origClOrd, origClOrdOut);
        Assert.Equal(secId, cmd.SecurityId);
        Assert.Equal((long)orderId, cmd.OrderId);
        Assert.Equal(clOrd.ToString(), cmd.ClOrdId);
    }

    [Fact]
    public void ExecReportNew_HostEncodes_SynthTraderDecodesIdentical()
    {
        Span<byte> frame = stackalloc byte[ExecutionReportEncoder.ExecReportNewTotal];
        const ulong clOrd = 0xAABBCCDDEEFF0011UL;
        const long secId = 900_000_000_003L;
        const long orderId = 0x1234_5678_9ABC_DEF0L;
        ExecutionReportEncoder.EncodeExecReportNew(frame,
            sessionId: 1, msgSeqNum: 2, sendingTimeNanos: 3,
            side: Side.Buy, clOrdIdValue: clOrd, secondaryOrderId: 0,
            securityId: secId, orderId: orderId,
            execId: 4, transactTimeNanos: 5,
            ordType: OrderType.Limit, tif: TimeInForce.Day,
            orderQty: 100, priceMantissa: 100_0000);

        var body = frame.Slice(SbeHeaderSize);
        var er = EntryPointClient.DecodeExecReportNew(body);
        Assert.Equal(clOrd, er.ClOrdId);
        Assert.Equal(secId, er.SecurityId);
        Assert.Equal(OrderSide.Buy, er.Side);
        Assert.Equal(orderId, er.OrderId);
    }

    [Fact]
    public void ExecReportTrade_HostEncodes_SynthTraderDecodesIdentical()
    {
        Span<byte> frame = stackalloc byte[ExecutionReportEncoder.ExecReportTradeTotal];
        const ulong clOrd = 0x7777_8888_9999_AAAAUL;
        const long secId = 900_000_000_004L;
        const long orderId = 12345L;
        const long lastQty = 50;
        const long lastPx = 99_9500;
        const long leaves = 50;
        const long cum = 50;
        ExecutionReportEncoder.EncodeExecReportTrade(frame,
            sessionId: 9, msgSeqNum: 10, sendingTimeNanos: 11,
            side: Side.Sell, clOrdIdValue: clOrd, secondaryOrderId: 0,
            securityId: secId, orderId: orderId, lastQty: lastQty, lastPxMantissa: lastPx,
            execId: 12, transactTimeNanos: 13, leavesQty: leaves, cumQty: cum,
            aggressor: true, tradeId: 1000, contraBroker: 8, tradeDate: (ushort)20240, orderQty: 100);

        var body = frame.Slice(SbeHeaderSize);
        var er = EntryPointClient.DecodeExecReportTrade(body);
        Assert.Equal(clOrd, er.ClOrdId);
        Assert.Equal(secId, er.SecurityId);
        Assert.Equal(OrderSide.Sell, er.Side);
        Assert.Equal(orderId, er.OrderId);
        Assert.Equal(lastQty, er.LastQty);
        Assert.Equal(lastPx, er.LastPxMantissa);
        Assert.Equal(leaves, er.LeavesQty);
        Assert.Equal(cum, er.CumQty);
    }

    [Fact]
    public void ExecReportCancel_HostEncodes_SynthTraderDecodesIdentical()
    {
        Span<byte> frame = stackalloc byte[ExecutionReportEncoder.ExecReportCancelTotal];
        const ulong clOrd = 0xCAFE_BABE_DEAD_BEEFUL;
        const long secId = 900_000_000_005L;
        const long orderId = 67890L;
        ExecutionReportEncoder.EncodeExecReportCancel(frame,
            sessionId: 1, msgSeqNum: 2, sendingTimeNanos: 3,
            side: Side.Buy, clOrdIdValue: clOrd, origClOrdIdValue: 0, secondaryOrderId: 0,
            securityId: secId, orderId: orderId,
            execId: 4, transactTimeNanos: 5,
            cumQty: 0, orderQty: 100, priceMantissa: 100_0000);

        var body = frame.Slice(SbeHeaderSize);
        var er = EntryPointClient.DecodeExecReportCancel(body);
        Assert.Equal(clOrd, er.ClOrdId);
        Assert.Equal(secId, er.SecurityId);
        Assert.Equal(OrderSide.Buy, er.Side);
        Assert.Equal(orderId, er.OrderId);
    }

    [Fact]
    public void ExecReportReject_HostEncodes_SynthTraderDecodesIdentical()
    {
        Span<byte> frame = stackalloc byte[ExecutionReportEncoder.ExecReportRejectTotal];
        const ulong clOrd = 0x1234_5678_DEAD_BEEFUL;
        const long secId = 900_000_000_006L;
        ExecutionReportEncoder.EncodeExecReportReject(frame,
            sessionId: 1, msgSeqNum: 2, sendingTimeNanos: 3,
            clOrdIdValue: clOrd, origClOrdIdValue: 0, securityId: secId, orderIdOrZero: 0,
            rejectReason: 99, transactTimeNanos: 4);

        var body = frame.Slice(SbeHeaderSize);
        var er = EntryPointClient.DecodeExecReportReject(body);
        Assert.Equal(clOrd, er.ClOrdId);
        Assert.Equal(secId, er.SecurityId);
    }
}
