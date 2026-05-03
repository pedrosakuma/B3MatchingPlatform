using System.Runtime.InteropServices;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;

namespace B3.Umdf.WireEncoder.Tests;

/// <summary>
/// Roundtrip tests: encode with <see cref="UmdfWireEncoder"/>, decode with
/// the SBE-generated readers, and assert every field matches.
/// </summary>
public class UmdfWireEncoderTests
{
    private const int FrameOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;

    [Fact]
    public void PacketHeader_Roundtrip()
    {
        Span<byte> buf = stackalloc byte[16];
        int n = UmdfWireEncoder.WritePacketHeader(buf, channelNumber: 84, sequenceVersion: 1,
            sequenceNumber: 12345, sendingTimeNanos: 0xDEAD_BEEF_CAFE_F00DUL);
        Assert.Equal(16, n);

        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(buf);
        Assert.Equal((byte)84, hdr.ChannelNumber);
        Assert.Equal((ushort)1, hdr.SequenceVersion);
        Assert.Equal(12345u, hdr.SequenceNumber);
        Assert.Equal(0xDEAD_BEEF_CAFE_F00DUL, hdr.SendingTime);
    }

    [Fact]
    public void PatchPacketHeader_OnlyTouchesSeqAndTime()
    {
        Span<byte> buf = stackalloc byte[16];
        UmdfWireEncoder.WritePacketHeader(buf, 84, 7, 1, 0);
        UmdfWireEncoder.PatchPacketHeader(buf, 999, 12345UL);

        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(buf);
        Assert.Equal((byte)84, hdr.ChannelNumber);
        Assert.Equal((ushort)7, hdr.SequenceVersion);
        Assert.Equal(999u, hdr.SequenceNumber);
        Assert.Equal(12345UL, hdr.SendingTime);
    }

    [Fact]
    public void OrderAdded_Roundtrip()
    {
        var buf = new byte[80];
        int n = UmdfWireEncoder.WriteOrderAddedFrame(buf, securityId: 900_000_000_001L,
            secondaryOrderId: 0x12340000_00000005L, mdEntryType: UmdfWireEncoder.MdEntryTypeBid,
            priceMantissa: 100_0000L, size: 500L, rptSeq: 42, insertTimestampNanos: 1_700_000_000_000_000_000UL);
        Assert.Equal(WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.OrderBlockLength, n);

        // FramingHeader
        var msgLen = MemoryMarshal.Read<ushort>(buf.AsSpan(0, 2));
        Assert.Equal((ushort)n, msgLen);

        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.Order_MBO_50Data.TryParse(
            buf.AsSpan(FrameOffset, WireOffsets.OrderBlockLength), out var rdr));
        Assert.Equal(900_000_000_001L, (long)rdr.Data.SecurityID.Value);
        Assert.Equal(0x12340000_00000005L, (long)rdr.Data.SecondaryOrderID.Value);
        Assert.Equal(MDEntryType.BID, rdr.Data.MDEntryType);
        Assert.Equal(MDUpdateAction.NEW, rdr.Data.MDUpdateAction);
        Assert.Equal(100_0000L, rdr.Data.MDEntryPx.Mantissa);
        Assert.Equal(500L, rdr.Data.MDEntrySize.Value);
        Assert.Equal(42u, rdr.Data.RptSeq);
        Assert.Equal(1_700_000_000_000_000_000UL, rdr.Data.MDInsertTimestamp.Time);
    }

    [Fact]
    public void OrderDeleted_Roundtrip_PriceNullByDefault()
    {
        var buf = new byte[80];
        int n = UmdfWireEncoder.WriteOrderDeletedFrame(buf, securityId: 42L,
            secondaryOrderId: 0xABCDEF12345L, mdEntryType: UmdfWireEncoder.MdEntryTypeOffer,
            size: 200L, rptSeq: 99, transactTimeNanos: 500UL);
        Assert.Equal(WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.DeleteOrderBlockLength, n);

        Assert.True(DeleteOrder_MBO_51Data.TryParse(
            buf.AsSpan(FrameOffset, WireOffsets.DeleteOrderBlockLength), out var rdr));
        Assert.Equal(42L, (long)rdr.Data.SecurityID.Value);
        Assert.Equal(MDEntryType.OFFER, rdr.Data.MDEntryType);
        Assert.Equal(200L, rdr.Data.MDEntrySize.Value);
        Assert.Equal(0xABCDEF12345L, (long)rdr.Data.SecondaryOrderID.Value);
        Assert.Equal(500UL, rdr.Data.TransactTime.Time);
        Assert.Equal(99u, rdr.Data.RptSeq);
        Assert.Null(rdr.Data.MDEntryPx.Mantissa); // PriceOptional null sentinel = long.MinValue
    }

    [Fact]
    public void OrderDeleted_WithExplicitPrice_Roundtrip()
    {
        var buf = new byte[80];
        UmdfWireEncoder.WriteOrderDeletedFrame(buf, securityId: 1L, secondaryOrderId: 1L,
            mdEntryType: UmdfWireEncoder.MdEntryTypeBid, size: 1L, rptSeq: 1, transactTimeNanos: 1UL,
            priceMantissa: 12345L);
        Assert.True(DeleteOrder_MBO_51Data.TryParse(
            buf.AsSpan(FrameOffset, WireOffsets.DeleteOrderBlockLength), out var rdr));
        Assert.Equal(12345L, rdr.Data.MDEntryPx.Mantissa);
    }

    [Fact]
    public void Trade_Roundtrip()
    {
        var buf = new byte[80];
        int n = UmdfWireEncoder.WriteTradeFrame(buf, securityId: 5L,
            priceMantissa: 250_5000L, size: 100L, tradeId: 7,
            tradeDate: 9000, transactTimeNanos: 12345UL, rptSeq: 17,
            buyerFirm: 100u, sellerFirm: 200u);
        Assert.Equal(WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.TradeBlockLength, n);

        Assert.True(Trade_53Data.TryParse(buf.AsSpan(FrameOffset, WireOffsets.TradeBlockLength), out var rdr));
        Assert.Equal(5L, (long)rdr.Data.SecurityID.Value);
        Assert.Equal(250_5000L, rdr.Data.MDEntryPx.Mantissa);
        Assert.Equal(100L, rdr.Data.MDEntrySize.Value);
        Assert.Equal(7u, rdr.Data.TradeID.Value);
        Assert.Equal(100u, rdr.Data.MDEntryBuyer);
        Assert.Equal(200u, rdr.Data.MDEntrySeller);
        Assert.Equal(12345UL, rdr.Data.TransactTime.Time);
        Assert.Equal(17u, rdr.Data.RptSeq);
        Assert.Null(rdr.Data.TrdSubType); // 255 sentinel
    }

    [Fact]
    public void TradeBust_Roundtrip()
    {
        var buf = new byte[80];
        int n = UmdfWireEncoder.WriteTradeBustFrame(buf, securityId: 900_000_000_001L,
            priceMantissa: 250_5000L, size: 100L, tradeId: 7,
            tradeDate: 9000, transactTimeNanos: 12345UL, rptSeq: 17);
        Assert.Equal(WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.TradeBustBlockLength, n);

        var msgLen = MemoryMarshal.Read<ushort>(buf.AsSpan(0, 2));
        Assert.Equal((ushort)n, msgLen);

        Assert.True(TradeBust_57Data.TryParse(buf.AsSpan(FrameOffset, WireOffsets.TradeBustBlockLength), out var rdr));
        Assert.Equal(900_000_000_001L, (long)rdr.Data.SecurityID.Value);
        Assert.Equal(250_5000L, rdr.Data.MDEntryPx.Mantissa);
        Assert.Equal(100L, rdr.Data.MDEntrySize.Value);
        Assert.Equal(7u, rdr.Data.TradeID.Value);
        Assert.Equal(9000, (ushort)rdr.Data.TradeDate.Value);
        Assert.Equal(12345UL, rdr.Data.TransactTime.Time);
        Assert.Equal(17u, rdr.Data.RptSeq);
    }

    [Fact]
    public void Trade_NullableBuyerSeller_AreNull()
    {
        var buf = new byte[80];
        UmdfWireEncoder.WriteTradeFrame(buf, securityId: 1L, priceMantissa: 1L, size: 1L,
            tradeId: 1, tradeDate: 1, transactTimeNanos: 1UL, rptSeq: 1);
        Assert.True(Trade_53Data.TryParse(buf.AsSpan(FrameOffset, WireOffsets.TradeBlockLength), out var rdr));
        Assert.Null(rdr.Data.MDEntryBuyer);
        Assert.Null(rdr.Data.MDEntrySeller);
    }

    [Fact]
    public void SnapshotHeader_Roundtrip_LastRptSeqOptional()
    {
        var buf = new byte[80];
        int n = UmdfWireEncoder.WriteSnapshotHeaderFrame(buf, securityId: 7L,
            totNumReports: 3, totNumBids: 2, totNumOffers: 1, totNumStats: 0, lastRptSeq: 42);
        Assert.Equal(WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SnapHeaderBlockLength, n);

        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            buf.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var rdr));
        Assert.Equal(7L, (long)rdr.Data.SecurityID.Value);
        Assert.Equal(3u, rdr.Data.TotNumReports);
        Assert.Equal(2u, rdr.Data.TotNumBids);
        Assert.Equal(1u, rdr.Data.TotNumOffers);
        Assert.Equal((ushort)0, rdr.Data.TotNumStats);
        Assert.Equal(42u, rdr.Data.LastRptSeq);
    }

    [Fact]
    public void SnapshotHeader_NullLastRptSeq()
    {
        var buf = new byte[80];
        UmdfWireEncoder.WriteSnapshotHeaderFrame(buf, 7L, 0, 0, 0, 0, lastRptSeq: null);
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            buf.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var rdr));
        Assert.Null(rdr.Data.LastRptSeq);
    }

    [Fact]
    public void SnapshotOrders_Roundtrip_TwoEntries()
    {
        var buf = new byte[256];
        var entries = new[]
        {
            new UmdfWireEncoder.SnapshotEntry(100_0000L, 500L, 100UL, 0x1L,
                UmdfWireEncoder.MdEntryTypeBid, EnteringFirm: 11u),
            new UmdfWireEncoder.SnapshotEntry(99_0000L, 200L, 200UL, 0x2L,
                UmdfWireEncoder.MdEntryTypeOffer),
        };
        int n = UmdfWireEncoder.WriteSnapshotOrdersFrame(buf, securityId: 7L, entries);
        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
                     + WireOffsets.SnapOrdersHeaderBlockLength
                     + WireOffsets.SnapOrdersGroupSizeEncodingSize
                     + WireOffsets.SnapOrdersEntrySize * 2;
        Assert.Equal(expected, n);

        // Decode group via SBE reader
        var bodySpan = buf.AsSpan(FrameOffset);
        Assert.True(SnapshotFullRefresh_Orders_MBO_71Data.TryParse(bodySpan, out var rdr));
        Assert.Equal(7L, (long)rdr.Data.SecurityID.Value);

        // Walk the repeating group manually using the offsets the encoder used,
        // since the generated reader's group iterator API varies.
        int groupOff = WireOffsets.SnapOrdersHeaderBlockLength;
        ushort entryBlockLen = MemoryMarshal.Read<ushort>(bodySpan.Slice(groupOff, 2));
        byte numInGroup = bodySpan[groupOff + 2];
        Assert.Equal((ushort)WireOffsets.SnapOrdersEntrySize, entryBlockLen);
        Assert.Equal((byte)2, numInGroup);

        int entry0 = groupOff + 3;
        Assert.Equal(100_0000L, MemoryMarshal.Read<long>(bodySpan.Slice(entry0 + 0, 8)));
        Assert.Equal(500L, MemoryMarshal.Read<long>(bodySpan.Slice(entry0 + 8, 8)));
        Assert.Equal(11u, MemoryMarshal.Read<uint>(bodySpan.Slice(entry0 + 20, 4)));
        Assert.Equal(0x1L, MemoryMarshal.Read<long>(bodySpan.Slice(entry0 + 32, 8)));
        Assert.Equal(UmdfWireEncoder.MdEntryTypeBid, bodySpan[entry0 + 40]);

        int entry1 = entry0 + WireOffsets.SnapOrdersEntrySize;
        Assert.Equal(99_0000L, MemoryMarshal.Read<long>(bodySpan.Slice(entry1 + 0, 8)));
        Assert.Equal(0x2L, MemoryMarshal.Read<long>(bodySpan.Slice(entry1 + 32, 8)));
        Assert.Equal(UmdfWireEncoder.MdEntryTypeOffer, bodySpan[entry1 + 40]);
    }

    [Fact]
    public void SnapshotOrders_RejectsOversizedGroup()
    {
        var entries = new UmdfWireEncoder.SnapshotEntry[256];
        var buf = new byte[16 * 1024];
        var ex = Assert.Throws<ArgumentException>(() =>
            UmdfWireEncoder.WriteSnapshotOrdersFrame(buf, 1L, entries));
        Assert.Contains("at most 255", ex.Message);
    }

    [Fact]
    public void SecurityDefinition_Roundtrip_BasicFields()
    {
        var buf = new byte[512];
        int n = UmdfWireEncoder.WriteSecurityDefinitionFrame(buf, securityId: 900_000_000_001L,
            symbol: "PETR4", isin: "BRPETRACNPR6", securityTypeByte: 1, totNoRelatedSym: 3,
            securityValidityTimestamp: 1_700_000_000L, maturityDate: 0);
        int expected = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SecDefBodyTotal;
        Assert.Equal(expected, n);

        var body = buf.AsSpan(FrameOffset, WireOffsets.SecDefBlockLength);
        Assert.Equal(900_000_000_001L, MemoryMarshal.Read<long>(body.Slice(WireOffsets.SecDefSecurityIdOffset, 8)));
        Assert.Equal("BVMF", System.Text.Encoding.ASCII.GetString(body.Slice(WireOffsets.SecDefSecurityExchangeOffset, 4)));
        Assert.Equal("PETR4", System.Text.Encoding.ASCII.GetString(body.Slice(WireOffsets.SecDefSymbolOffset, 5)));
        Assert.Equal((byte)1, body[WireOffsets.SecDefSecurityTypeOffset]);
        Assert.Equal(3u, MemoryMarshal.Read<uint>(body.Slice(WireOffsets.SecDefTotNoRelatedSymOffset, 4)));
        Assert.Equal("BRPETRACNPR6", System.Text.Encoding.ASCII.GetString(body.Slice(WireOffsets.SecDefIsinNumberOffset, 12)));
    }

    [Fact]
    public void Encoder_ThrowsOnSmallBuffer()
    {
        var small = new byte[8];
        Assert.Throws<ArgumentException>(() =>
        {
            UmdfWireEncoder.WriteOrderAddedFrame(small, 1, 1, UmdfWireEncoder.MdEntryTypeBid, 1, 1, 1, 1);
        });
    }
}
