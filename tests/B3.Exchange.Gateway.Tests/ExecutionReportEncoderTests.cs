using B3.EntryPoint.Wire;
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
        // SBE header at offset SOFH(4)..SOFH+8: BlockLength=176, TemplateId=200, SchemaId=1, Version=6 (#248)
        var hdr = buf.AsSpan(EntryPointFrameReader.SofhSize, EntryPointFrameReader.SbeHeaderSize);
        Assert.Equal((ushort)ExecutionReportEncoder.ExecReportNewBlock, MemoryMarshal.Read<ushort>(hdr.Slice(0, 2)));
        Assert.Equal((ushort)EntryPointFrameReader.TidExecutionReportNew, MemoryMarshal.Read<ushort>(hdr.Slice(2, 2)));
        Assert.Equal((ushort)1, MemoryMarshal.Read<ushort>(hdr.Slice(4, 2)));
        Assert.Equal((ushort)6, MemoryMarshal.Read<ushort>(hdr.Slice(6, 2)));

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
            rejectReason: 5u, transactTimeNanos: 0UL);

        Assert.Equal(ExecutionReportEncoder.ExecReportRejectTotal, n);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(7UL, MemoryMarshal.Read<ulong>(body.Slice(20, 8)));            // ClOrdID
        Assert.Equal(333L, MemoryMarshal.Read<long>(body.Slice(36, 8)));            // SecurityID
        Assert.Equal(5u, MemoryMarshal.Read<uint>(body.Slice(44, 4)));              // OrdRejReason (uint32, overlaps SecExchange)
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

    // ====== #49 / #GAP-11: receivedTime (tag 35544) round-trip ======
    //
    // ER_New / ER_Modify / ER_Cancel were bumped to V3 to expose the optional
    // `receivedTime` trailing field. Tests below lock both the populated and
    // null sentinel paths and the V3 trailing optional null sentinels that
    // body.Clear() alone cannot satisfy (they default to a non-zero "null"
    // value per SBE schema).

    [Fact]
    public void EncodeNew_V3ReceivedTime_PopulatedAndNullSentinelsRoundTrip()
    {
        const ulong received = 1_700_000_000_123_456_789UL;
        var buf = new byte[ExecutionReportEncoder.ExecReportNewTotal];
        int n = ExecutionReportEncoder.EncodeExecReportNew(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 1, secondaryOrderId: 0,
            securityId: 1, orderId: 1, execId: 0, transactTimeNanos: 0,
            ordType: OrderType.Limit, tif: TimeInForce.Day,
            orderQty: 10, priceMantissa: 100_0000L,
            receivedTimeNanos: received);
        Assert.Equal(ExecutionReportEncoder.ExecReportNewTotal, n);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(received, MemoryMarshal.Read<ulong>(body.Slice(144, 8)));    // ReceivedTime
        Assert.Equal((byte)255, body[162]);                                       // CrossType null
        Assert.Equal((byte)255, body[163]);                                       // CrossPrioritization null
        Assert.Equal((byte)255, body[164]);                                       // MmProtectionReset null

        var bufNull = new byte[ExecutionReportEncoder.ExecReportNewTotal];
        ExecutionReportEncoder.EncodeExecReportNew(bufNull,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 1, secondaryOrderId: 0,
            securityId: 1, orderId: 1, execId: 0, transactTimeNanos: 0,
            ordType: OrderType.Limit, tif: TimeInForce.Day,
            orderQty: 10, priceMantissa: 100_0000L);
        var bodyNull = bufNull.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(ulong.MaxValue, MemoryMarshal.Read<ulong>(bodyNull.Slice(144, 8))); // null sentinel
    }

    [Fact]
    public void EncodeModify_V3ReceivedTime_PopulatedAndNullSentinelsRoundTrip()
    {
        const ulong received = 1_700_000_000_222_222_222UL;
        var buf = new byte[ExecutionReportEncoder.ExecReportModifyTotal];
        ExecutionReportEncoder.EncodeExecReportModify(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 22, origClOrdIdValue: 21, secondaryOrderId: 0,
            securityId: 7, orderId: 1234, execId: 0UL, transactTimeNanos: 0UL,
            leavesQty: 50, cumQty: 25, orderQty: 75, priceMantissa: 99_0000L,
            receivedTimeNanos: received);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(received, MemoryMarshal.Read<ulong>(body.Slice(160, 8)));    // ReceivedTime
        Assert.Equal((byte)255, body[178]);                                       // MmProtectionReset null

        var bufNull = new byte[ExecutionReportEncoder.ExecReportModifyTotal];
        ExecutionReportEncoder.EncodeExecReportModify(bufNull,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 22, origClOrdIdValue: 21, secondaryOrderId: 0,
            securityId: 7, orderId: 1234, execId: 0UL, transactTimeNanos: 0UL,
            leavesQty: 50, cumQty: 25, orderQty: 75, priceMantissa: 99_0000L);
        var bodyNull = bufNull.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(ulong.MaxValue, MemoryMarshal.Read<ulong>(bodyNull.Slice(160, 8)));
    }

    [Fact]
    public void EncodeCancel_V3ReceivedTime_PopulatedAndNullSentinelsRoundTrip()
    {
        const ulong received = 1_700_000_000_333_333_333UL;
        var buf = new byte[ExecutionReportEncoder.ExecReportCancelTotal];
        ExecutionReportEncoder.EncodeExecReportCancel(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 1, origClOrdIdValue: 0, secondaryOrderId: 0,
            securityId: 7, orderId: 1, execId: 0, transactTimeNanos: 0,
            cumQty: 0, orderQty: 100, priceMantissa: 100_0000L,
            receivedTimeNanos: received);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(received, MemoryMarshal.Read<ulong>(body.Slice(156, 8)));    // ReceivedTime

        var bufNull = new byte[ExecutionReportEncoder.ExecReportCancelTotal];
        ExecutionReportEncoder.EncodeExecReportCancel(bufNull,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 1, origClOrdIdValue: 0, secondaryOrderId: 0,
            securityId: 7, orderId: 1, execId: 0, transactTimeNanos: 0,
            cumQty: 0, orderQty: 100, priceMantissa: 100_0000L);
        var bodyNull = bufNull.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(ulong.MaxValue, MemoryMarshal.Read<ulong>(bodyNull.Slice(156, 8)));
    }

    // ====== #248: ER encoders bumped to V6 schema layout ======
    //
    // The B3.EntryPoint.Client SDK's InboundDecoder hard-casts inbound
    // payloads to the latest schema struct via MemoryMarshal.AsRef and
    // ignores the SBE header `version` field, so any payload smaller than
    // the V6 BlockLength tears down the inbound loop. These tests lock the
    // V6 BlockLengths + the new trailing-field null sentinels.

    [Fact]
    public void EncodeNew_V6BlockLength_TradingSubAccountNull()
    {
        Assert.Equal(176, ExecutionReportEncoder.ExecReportNewBlock);
        var buf = new byte[ExecutionReportEncoder.ExecReportNewTotal];
        ExecutionReportEncoder.EncodeExecReportNew(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 1, secondaryOrderId: 0,
            securityId: 1, orderId: 1, execId: 0, transactTimeNanos: 0,
            ordType: OrderType.Limit, tif: TimeInForce.Day,
            orderQty: 10, priceMantissa: 100_0000L);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(0u, MemoryMarshal.Read<uint>(body.Slice(168, 4))); // StrategyID null
        Assert.Equal(0u, MemoryMarshal.Read<uint>(body.Slice(172, 4))); // TradingSubAccount null
    }

    [Fact]
    public void EncodeModify_V6BlockLength_ExecRestatementReasonAndTradingSubAccountNull()
    {
        Assert.Equal(188, ExecutionReportEncoder.ExecReportModifyBlock);
        var buf = new byte[ExecutionReportEncoder.ExecReportModifyTotal];
        ExecutionReportEncoder.EncodeExecReportModify(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 22, origClOrdIdValue: 21, secondaryOrderId: 0,
            securityId: 7, orderId: 1234, execId: 0UL, transactTimeNanos: 0UL,
            leavesQty: 50, cumQty: 25, orderQty: 75, priceMantissa: 99_0000L);
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal((byte)255, body[178]);                            // MmProtectionReset null
        Assert.Equal((byte)255, body[179]);                            // ExecRestatementReason null (V6 inserted)
        Assert.Equal(0, MemoryMarshal.Read<int>(body.Slice(180, 4)));  // StrategyID null
        Assert.Equal(0u, MemoryMarshal.Read<uint>(body.Slice(184, 4))); // TradingSubAccount null
    }

    [Fact]
    public void EncodeCancel_V6Header_VersionIs6()
    {
        Assert.Equal(182, ExecutionReportEncoder.ExecReportCancelBlock);
        var buf = new byte[ExecutionReportEncoder.ExecReportCancelTotal];
        ExecutionReportEncoder.EncodeExecReportCancel(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            side: Side.Buy, clOrdIdValue: 1, origClOrdIdValue: 0, secondaryOrderId: 0,
            securityId: 7, orderId: 1, execId: 0, transactTimeNanos: 0,
            cumQty: 0, orderQty: 100, priceMantissa: 100_0000L);
        var hdr = buf.AsSpan(EntryPointFrameReader.SofhSize, EntryPointFrameReader.SbeHeaderSize);
        Assert.Equal((ushort)6, MemoryMarshal.Read<ushort>(hdr.Slice(6, 2)));
    }

    [Fact]
    public void EncodeTrade_V6BlockLength_TrailingNulls()
    {
        Assert.Equal(174, ExecutionReportEncoder.ExecReportTradeBlock);
        var buf = new byte[ExecutionReportEncoder.ExecReportTradeTotal];
        ExecutionReportEncoder.EncodeExecReportTrade(buf,
            sessionId: 1, msgSeqNum: 2, sendingTimeNanos: 0UL,
            side: Side.Sell, clOrdIdValue: 77, secondaryOrderId: 0,
            securityId: 999, orderId: 1234,
            lastQty: 5, lastPxMantissa: 50_0000L,
            execId: 10UL, transactTimeNanos: 0UL,
            leavesQty: 0, cumQty: 5,
            aggressor: true, tradeId: 4242, contraBroker: 8,
            tradeDate: 19000, orderQty: 5);
        var hdr = buf.AsSpan(EntryPointFrameReader.SofhSize, EntryPointFrameReader.SbeHeaderSize);
        Assert.Equal((ushort)6, MemoryMarshal.Read<ushort>(hdr.Slice(6, 2)));
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal((byte)255, body[154]);                            // TradingSessionID null
        Assert.Equal((byte)255, body[155]);                            // TradingSessionSubID null
        Assert.Equal((byte)255, body[156]);                            // SecurityTradingStatus null
        Assert.Equal((byte)255, body[157]);                            // CrossType null
        Assert.Equal((byte)255, body[158]);                            // CrossPrioritization null
        Assert.Equal(0, MemoryMarshal.Read<int>(body.Slice(160, 4)));  // StrategyID null
        Assert.Equal(0u, MemoryMarshal.Read<uint>(body.Slice(170, 4))); // TradingSubAccount null
    }

    [Fact]
    public void EncodeReject_V6BlockLength_ReceivedTimeAndTrailingNulls()
    {
        Assert.Equal(164, ExecutionReportEncoder.ExecReportRejectBlock);
        const ulong received = 1_700_000_000_999_999_999UL;
        var buf = new byte[ExecutionReportEncoder.ExecReportRejectTotal];
        ExecutionReportEncoder.EncodeExecReportReject(buf,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            clOrdIdValue: 7, origClOrdIdValue: 0, securityId: 333, orderIdOrZero: 0,
            rejectReason: 5u, transactTimeNanos: 0UL, receivedTimeNanos: received);
        var hdr = buf.AsSpan(EntryPointFrameReader.SofhSize, EntryPointFrameReader.SbeHeaderSize);
        Assert.Equal((ushort)6, MemoryMarshal.Read<ushort>(hdr.Slice(6, 2)));
        var body = buf.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(received, MemoryMarshal.Read<ulong>(body.Slice(138, 8))); // ReceivedTime
        Assert.Equal(0, MemoryMarshal.Read<int>(body.Slice(156, 4)));          // StrategyID null
        Assert.Equal(0u, MemoryMarshal.Read<uint>(body.Slice(160, 4)));        // TradingSubAccount null

        var bufNull = new byte[ExecutionReportEncoder.ExecReportRejectTotal];
        ExecutionReportEncoder.EncodeExecReportReject(bufNull,
            sessionId: 1, msgSeqNum: 1, sendingTimeNanos: 0UL,
            clOrdIdValue: 7, origClOrdIdValue: 0, securityId: 333, orderIdOrZero: 0,
            rejectReason: 5u, transactTimeNanos: 0UL);
        var bodyNullR = bufNull.AsSpan(EntryPointFrameReader.WireHeaderSize);
        Assert.Equal(ulong.MaxValue, MemoryMarshal.Read<ulong>(bodyNullR.Slice(138, 8)));
    }
}
