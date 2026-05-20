using B3.Umdf.WireEncoder;

namespace B3.Umdf.WireEncoder.Tests;

/// <summary>
/// Verifies that each <see cref="UmdfFrameBuilder"/> method reserves the
/// correct byte count (FramingHeaderSize + SbeMessageHeaderSize +
/// XxxBlockLength), calls the encoder, and commits the exact bytes written.
/// For the Update and Delete variants the MdUpdateAction byte at the known
/// offset is also asserted so size-accounting drift is caught early.
/// </summary>
public class UmdfFrameBuilderTests
{
    /// <summary>
    /// Minimal <see cref="IUmdfFrameSink"/> that records the last reserve size
    /// and committed byte count. Buffer is large enough for any single frame.
    /// </summary>
    private sealed class RecordingSink : IUmdfFrameSink
    {
        public readonly byte[] Buffer = new byte[512];
        public int ReservedSize;
        public int CommittedBytes;

        Span<byte> IUmdfFrameSink.Reserve(int size)
        {
            ReservedSize = size;
            return Buffer.AsSpan();
        }

        void IUmdfFrameSink.Commit(int written) => CommittedBytes = written;
    }

    private static RecordingSink MakeSink() => new();

    private static int OrderFrameSize =>
        WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.OrderBlockLength;

    private static int DeleteOrderFrameSize =>
        WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.DeleteOrderBlockLength;

    [Fact]
    public void WriteOrderAdded_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderAdded(sink,
            securityId: 1L, orderId: 2L, mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            priceMantissa: 100_0000L, quantity: 10L, rptSeq: 1u,
            insertTimestampNanos: 0ul);

        Assert.Equal(OrderFrameSize, sink.ReservedSize);
        Assert.True(sink.CommittedBytes > 0);
        Assert.Equal(sink.ReservedSize, sink.CommittedBytes);
    }

    [Fact]
    public void WriteOrderAdded_MdUpdateActionIsNew()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderAdded(sink,
            securityId: 1L, orderId: 2L, mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            priceMantissa: 100_0000L, quantity: 10L, rptSeq: 1u,
            insertTimestampNanos: 0ul);

        int actionOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OrderBodyMdUpdateActionOffset;
        Assert.Equal(0x00, sink.Buffer[actionOffset]); // NEW
    }

    [Fact]
    public void WriteOrderUpdate_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderUpdate(sink,
            securityId: 1L, orderId: 2L, mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            priceMantissa: 100_0000L, newRemainingQuantity: 5L, rptSeq: 2u,
            insertTimestampNanos: 0ul);

        Assert.Equal(OrderFrameSize, sink.ReservedSize);
        Assert.True(sink.CommittedBytes > 0);
        Assert.Equal(sink.ReservedSize, sink.CommittedBytes);
    }

    [Fact]
    public void WriteOrderUpdate_PatchesActionByteToChange()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderUpdate(sink,
            securityId: 1L, orderId: 2L, mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            priceMantissa: 100_0000L, newRemainingQuantity: 5L, rptSeq: 2u,
            insertTimestampNanos: 0ul);

        int actionOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OrderBodyMdUpdateActionOffset;
        Assert.Equal(0x01, sink.Buffer[actionOffset]); // CHANGE / UPDATE
    }

    [Fact]
    public void WriteOrderDeleted_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderDeleted(sink,
            securityId: 1L, orderId: 2L, mdEntryType: UmdfWireEncoder.MdEntryTypeOffer,
            size: 10L, rptSeq: 3u, transactTimeNanos: 0ul, priceMantissa: 50_0000L);

        Assert.Equal(DeleteOrderFrameSize, sink.ReservedSize);
        Assert.True(sink.CommittedBytes > 0);
        Assert.Equal(sink.ReservedSize, sink.CommittedBytes);
    }

    [Fact]
    public void WriteOrderBookSideEmpty_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderBookSideEmpty(sink, securityId: 1L, transactTimeNanos: 0ul);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.EmptyBookBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteOrderMassCanceled_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOrderMassCanceled(sink,
            securityId: 1L, mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            rptSeq: 4u, transactTimeNanos: 0ul);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.MassDeleteOrdersBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteTradingPhaseChanged_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteTradingPhaseChanged(sink,
            securityId: 1L, securityTradingStatus: 2, rptSeq: 5u, transactTimeNanos: 0ul);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteInstrumentHalted_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteInstrumentHalted(sink,
            securityId: 1L, securityTradingStatus: 2, rptSeq: 6u, transactTimeNanos: 0ul);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteInstrumentHalted_WritesHaltEventByte()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteInstrumentHalted(sink,
            securityId: 1L, securityTradingStatus: 2, rptSeq: 6u, transactTimeNanos: 0ul);

        int eventOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBodySecurityTradingEventOffset;
        Assert.Equal(UmdfFrameBuilder.SecurityTradingEventHalt, sink.Buffer[eventOffset]);
    }

    [Fact]
    public void WriteInstrumentResumed_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteInstrumentResumed(sink,
            securityId: 1L, securityTradingStatus: 2, rptSeq: 7u, transactTimeNanos: 0ul);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteInstrumentResumed_WritesResumeEventByte()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteInstrumentResumed(sink,
            securityId: 1L, securityTradingStatus: 2, rptSeq: 7u, transactTimeNanos: 0ul);

        int eventOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SecurityStatusBodySecurityTradingEventOffset;
        Assert.Equal(UmdfFrameBuilder.SecurityTradingEventResume, sink.Buffer[eventOffset]);
    }

    [Fact]
    public void WriteTheoreticalOpeningPrice_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteTheoreticalOpeningPrice(sink,
            securityId: 1L, hasTop: true, priceMantissa: 100_0000L, quantity: 50L,
            tradeDate: 0, mdEntryTimestampNanos: 0ul, rptSeq: 8u);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.TheoreticalOpeningPriceBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteAuctionImbalance_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteAuctionImbalance(sink,
            securityId: 1L, hasImbalance: true,
            imbalanceCondition: UmdfWireEncoder.ImbalanceConditionMoreBuyers,
            imbalanceQty: 100L, mdEntryTimestampNanos: 0ul, rptSeq: 9u);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.AuctionImbalanceBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteOpeningPrice_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteOpeningPrice(sink,
            securityId: 1L, priceMantissa: 100_0000L,
            tradeDate: 0, mdEntryTimestampNanos: 0ul, rptSeq: 10u);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.OpeningPriceBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteClosingPrice_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteClosingPrice(sink,
            securityId: 1L, priceMantissa: 100_0000L,
            tradeDate: 0, mdEntryTimestampNanos: 0ul, rptSeq: 11u);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.ClosingPriceBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }

    [Fact]
    public void WriteTrade_ReservesAndCommitsCorrectSize()
    {
        var sink = MakeSink();
        UmdfFrameBuilder.WriteTrade(sink,
            securityId: 1L, priceMantissa: 100_0000L, quantity: 10L,
            tradeId: 42u, tradeDate: 0, transactTimeNanos: 0ul, rptSeq: 12u,
            buyerFirm: 11u, sellerFirm: 22u);

        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.TradeBlockLength;
        Assert.Equal(expected, sink.ReservedSize);
        Assert.Equal(expected, sink.CommittedBytes);
    }
}
