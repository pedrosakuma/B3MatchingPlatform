using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

public class AuditRecordCodecTests
{
    private static PostTradeRecord Sample() => new(
        TradeId: 42, TransactTimeNanos: 1_700_000_000_000_000_000UL,
        SecurityId: 900_000_000_001L, AggressorSide: Side.Sell,
        Quantity: 250, PriceMantissa: 12_345_678L,
        BuyClOrdId: 111UL, SellClOrdId: 222UL,
        BuyFirm: 7, SellFirm: 8,
        BuyOrderId: 1001, SellOrderId: 1002);

    [Fact]
    public void Encode_Then_TryDecode_RoundTripsAllFields()
    {
        var src = Sample();
        Span<byte> buf = stackalloc byte[AuditRecordCodec.RecordSize];
        int n = AuditRecordCodec.Encode(buf, in src);
        Assert.Equal(AuditRecordCodec.RecordSize, n);

        Assert.True(AuditRecordCodec.TryDecode(buf, out var dst));
        Assert.Equal(src, dst);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_OnTruncatedBuffer()
    {
        var src = Sample();
        var buf = new byte[AuditRecordCodec.RecordSize];
        AuditRecordCodec.Encode(buf, in src);
        Assert.False(AuditRecordCodec.TryDecode(buf.AsSpan(0, buf.Length - 1), out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalse_OnCorruptedBody()
    {
        var src = Sample();
        var buf = new byte[AuditRecordCodec.RecordSize];
        AuditRecordCodec.Encode(buf, in src);
        // Flip a single body bit (somewhere past the crc field).
        buf[20] ^= 0x01;
        Assert.False(AuditRecordCodec.TryDecode(buf, out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalse_OnWrongRecordLen()
    {
        var src = Sample();
        var buf = new byte[AuditRecordCodec.RecordSize];
        AuditRecordCodec.Encode(buf, in src);
        // Pretend an older schema wrote a shorter record.
        buf[0] = 0; buf[1] = 0; buf[2] = 0; buf[3] = 0;
        Assert.False(AuditRecordCodec.TryDecode(buf, out _));
    }

    [Fact]
    public void FileHeader_RoundTripsChannelAndDate()
    {
        var buf = new byte[AuditRecordCodec.FileHeaderSize];
        var date = new DateOnly(2026, 5, 18);
        AuditRecordCodec.WriteFileHeader(buf, channelNumber: 84, tradeDate: date);
        var (ch, d) = AuditRecordCodec.ReadFileHeader(buf);
        Assert.Equal((byte)84, ch);
        Assert.Equal(date, d);
    }

    [Fact]
    public void FileHeader_ReadFails_OnWrongMagic()
    {
        var buf = new byte[AuditRecordCodec.FileHeaderSize];
        AuditRecordCodec.WriteFileHeader(buf, 1, new DateOnly(2026, 1, 1));
        buf[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => AuditRecordCodec.ReadFileHeader(buf));
    }

    [Fact]
    public void FileHeader_ReadFails_OnUnknownSchemaVersion()
    {
        var buf = new byte[AuditRecordCodec.FileHeaderSize];
        AuditRecordCodec.WriteFileHeader(buf, 1, new DateOnly(2026, 1, 1));
        buf[4] = 99; buf[5] = 0;
        Assert.Throws<InvalidDataException>(() => AuditRecordCodec.ReadFileHeader(buf));
    }
}
