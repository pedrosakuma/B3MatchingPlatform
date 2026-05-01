using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

public class ExecutionReportEncoderTests
{
    [Fact]
    public void EncodeNew_WritesHeaderAndCoreFields()
    {
        var buf = new byte[ExecutionReportEncoder.ExecReportNewTotal];
        int n = ExecutionReportEncoder.EncodeExecReportNew(buf,
            sessionId: 42, msgSeqNum: 1, sendingTimeNanos: 1_000_000_000UL,
            side: Side.Buy, clOrdIdValue: 99, secondaryOrderId: 555,
            securityId: 11_22, orderId: 7777,
            execId: 100UL, transactTimeNanos: 1_000_000_001UL,
            ordType: OrderType.Limit, tif: TimeInForce.Day,
            orderQty: 10, priceMantissa: 12_3450L);

        Assert.Equal(ExecutionReportEncoder.ExecReportNewTotal, n);
        // SBE header at offset SOFH(4)..SOFH+8: BlockLength=144, TemplateId=200, SchemaId=1, Version=2
        var hdr = buf.AsSpan(EntryPointFrameReader.SofhSize, EntryPointFrameReader.SbeHeaderSize);
        Assert.Equal((ushort)ExecutionReportEncoder.ExecReportNewBlock, MemoryMarshal.Read<ushort>(hdr.Slice(0, 2)));
        Assert.Equal((ushort)EntryPointFrameReader.TidExecutionReportNew, MemoryMarshal.Read<ushort>(hdr.Slice(2, 2)));
        Assert.Equal((ushort)1, MemoryMarshal.Read<ushort>(hdr.Slice(4, 2)));
        Assert.Equal((ushort)2, MemoryMarshal.Read<ushort>(hdr.Slice(6, 2)));

        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal((uint)42, MemoryMarshal.Read<uint>(body.Slice(0, 4)));        // SessionId
        Assert.Equal((uint)1, MemoryMarshal.Read<uint>(body.Slice(4, 4)));         // MsgSeqNum
        Assert.Equal(1_000_000_000UL, MemoryMarshal.Read<ulong>(body.Slice(8, 8)));// SendingTime
        Assert.Equal((byte)'1', body[18]);                                          // Side=Buy
        Assert.Equal((byte)'0', body[19]);                                          // OrdStatus=New
        Assert.Equal(99UL, MemoryMarshal.Read<ulong>(body.Slice(20, 8)));          // ClOrdID
        Assert.Equal(555L, MemoryMarshal.Read<long>(body.Slice(28, 8)));           // SecondaryOrderID
        Assert.Equal(1122L, MemoryMarshal.Read<long>(body.Slice(36, 8)));          // SecurityID
        Assert.Equal(7777L, MemoryMarshal.Read<long>(body.Slice(44, 8)));          // OrderID (overlaps SecExchange)
        Assert.Equal(100UL, MemoryMarshal.Read<ulong>(body.Slice(56, 8)));         // ExecID
        Assert.Equal(ulong.MaxValue, MemoryMarshal.Read<ulong>(body.Slice(72, 8))); // MarketSegmentReceivedTime null
        Assert.Equal(long.MinValue, MemoryMarshal.Read<long>(body.Slice(80, 8)));  // ProtectionPrice null
        Assert.Equal((byte)'2', body[92]);                                          // OrdType=Limit
        Assert.Equal((byte)'0', body[93]);                                          // TIF=Day
        Assert.Equal(10L, MemoryMarshal.Read<long>(body.Slice(96, 8)));            // OrderQty
        Assert.Equal(12_3450L, MemoryMarshal.Read<long>(body.Slice(104, 8)));      // Price
        Assert.Equal(long.MinValue, MemoryMarshal.Read<long>(body.Slice(112, 8))); // StopPx null
    }

    [Fact]
    public void EncodeTrade_WritesCoreFields()
    {
        var buf = new byte[ExecutionReportEncoder.ExecReportTradeTotal];
        int n = ExecutionReportEncoder.EncodeExecReportTrade(buf,
            sessionId: 1, msgSeqNum: 2, sendingTimeNanos: 0UL,
            side: Side.Sell, clOrdIdValue: 77, secondaryOrderId: 0,
            securityId: 999, orderId: 1234,
            lastQty: 5, lastPxMantissa: 50_0000L,
            execId: 10UL, transactTimeNanos: 0UL,
            leavesQty: 0, cumQty: 5,
            aggressor: true, tradeId: 4242, contraBroker: 8,
            tradeDate: 19000, orderQty: 5);

        Assert.Equal(ExecutionReportEncoder.ExecReportTradeTotal, n);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal((byte)'2', body[18]);          // Side=Sell
        Assert.Equal((byte)'2', body[19]);          // OrdStatus=Filled (leaves==0)
        Assert.Equal(5L, MemoryMarshal.Read<long>(body.Slice(48, 8)));   // LastQty
        Assert.Equal(50_0000L, MemoryMarshal.Read<long>(body.Slice(56, 8))); // LastPx
        Assert.Equal(0L, MemoryMarshal.Read<long>(body.Slice(80, 8)));   // LeavesQty
        Assert.Equal(5L, MemoryMarshal.Read<long>(body.Slice(88, 8)));   // CumQty
        Assert.Equal((byte)1, body[96]);                                  // AggressorIndicator
        Assert.Equal((byte)'F', body[97]);                                // ExecType=Trade
        Assert.Equal(4242u, MemoryMarshal.Read<uint>(body.Slice(100, 4))); // TradeID
        Assert.Equal(1234L, MemoryMarshal.Read<long>(body.Slice(108, 8))); // OrderID
        Assert.Equal((ushort)19000, MemoryMarshal.Read<ushort>(body.Slice(116, 2))); // TradeDate
        Assert.Equal((ushort)65535, MemoryMarshal.Read<ushort>(body.Slice(144, 2))); // CrossedIndicator null
        Assert.Equal(5L, MemoryMarshal.Read<long>(body.Slice(146, 8)));  // OrderQty
    }

    [Fact]
    public void EncodeCancel_WritesOrderIdAndOrigClOrd()
    {
        var buf = new byte[ExecutionReportEncoder.ExecReportCancelTotal];
        int n = ExecutionReportEncoder.EncodeExecReportCancel(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 11, origClOrdIdValue: 10, secondaryOrderId: 0,
            securityId: 1, orderId: 9999,
            execId: 0UL, transactTimeNanos: 0UL,
            cumQty: 0, orderQty: 100, priceMantissa: 12_3450L);

        Assert.Equal(ExecutionReportEncoder.ExecReportCancelTotal, n);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal((byte)'4', body[19]);                                          // OrdStatus=Canceled
        Assert.Equal(11UL, MemoryMarshal.Read<ulong>(body.Slice(20, 8)));           // ClOrdID
        Assert.Equal(0L, MemoryMarshal.Read<long>(body.Slice(44, 8)));              // CumQty (overlap)
        Assert.Equal(9999L, MemoryMarshal.Read<long>(body.Slice(80, 8)));           // OrderID
        Assert.Equal(10UL, MemoryMarshal.Read<ulong>(body.Slice(88, 8)));           // OrigClOrdID
        Assert.Equal((byte)255, body[99]);                                          // ExecRestatementReason null
        Assert.Equal(100L, MemoryMarshal.Read<long>(body.Slice(116, 8)));           // OrderQty
        Assert.Equal(12_3450L, MemoryMarshal.Read<long>(body.Slice(124, 8)));       // Price
    }

    [Fact]
    public void EncodeReject_WritesRejReasonOverlapWithSecExchange()
    {
        var buf = new byte[ExecutionReportEncoder.ExecReportRejectTotal];
        int n = ExecutionReportEncoder.EncodeExecReportReject(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            clOrdIdValue: 7, origClOrdIdValue: 0, securityId: 333, orderIdOrZero: 0,
            rejectReason: 5, transactTimeNanos: 0UL);

        Assert.Equal(ExecutionReportEncoder.ExecReportRejectTotal, n);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(7UL, MemoryMarshal.Read<ulong>(body.Slice(20, 8)));            // ClOrdID
        Assert.Equal(333L, MemoryMarshal.Read<long>(body.Slice(36, 8)));            // SecurityID
        Assert.Equal((byte)5, body[44]);                                            // OrdRejReason
    }

    [Fact]
    public void EncodeModify_WritesLeavesQtyAndOrigClOrd()
    {
        var buf = new byte[ExecutionReportEncoder.ExecReportModifyTotal];
        int n = ExecutionReportEncoder.EncodeExecReportModify(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 22, origClOrdIdValue: 21, secondaryOrderId: 0,
            securityId: 7, orderId: 1234, execId: 0UL, transactTimeNanos: 0UL,
            leavesQty: 50, cumQty: 25, orderQty: 75, priceMantissa: 99_0000L);

        Assert.Equal(ExecutionReportEncoder.ExecReportModifyTotal, n);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal((byte)'5', body[19]);                                          // OrdStatus=Replaced
        Assert.Equal(50L, MemoryMarshal.Read<long>(body.Slice(44, 8)));             // LeavesQty (overlap)
        Assert.Equal(25L, MemoryMarshal.Read<long>(body.Slice(72, 8)));             // CumQty
        Assert.Equal(1234L, MemoryMarshal.Read<long>(body.Slice(88, 8)));           // OrderID
        Assert.Equal(21UL, MemoryMarshal.Read<ulong>(body.Slice(96, 8)));           // OrigClOrdID
        Assert.Equal(75L, MemoryMarshal.Read<long>(body.Slice(120, 8)));            // OrderQty
        Assert.Equal(99_0000L, MemoryMarshal.Read<long>(body.Slice(128, 8)));       // Price
    }
}
