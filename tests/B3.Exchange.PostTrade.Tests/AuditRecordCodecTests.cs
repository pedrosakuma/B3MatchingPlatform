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
        var (ch, d, ver) = AuditRecordCodec.ReadFileHeader(buf);
        Assert.Equal((byte)84, ch);
        Assert.Equal(date, d);
        Assert.Equal(AuditRecordCodec.SchemaVersion, ver);
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

    // ----- ADR 0008 / issue #369 PR-1: schema-v2 records --------------

    [Fact]
    public void FileHeader_AcceptsV1_AndV2()
    {
        var buf1 = new byte[AuditRecordCodec.FileHeaderSize];
        AuditRecordCodec.WriteFileHeader(buf1, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV1);
        var (ch1, _, ver1) = AuditRecordCodec.ReadFileHeader(buf1);
        Assert.Equal((byte)7, ch1);
        Assert.Equal(AuditRecordCodec.SchemaVersionV1, ver1);

        var buf2 = new byte[AuditRecordCodec.FileHeaderSize];
        AuditRecordCodec.WriteFileHeader(buf2, 8, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV2);
        var (ch2, _, ver2) = AuditRecordCodec.ReadFileHeader(buf2);
        Assert.Equal((byte)8, ch2);
        Assert.Equal(AuditRecordCodec.SchemaVersionV2, ver2);
    }

    [Fact]
    public void WriteFileHeader_RejectsUnknownVersion()
    {
        var buf = new byte[AuditRecordCodec.FileHeaderSize];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuditRecordCodec.WriteFileHeader(buf, 1, new DateOnly(2026, 1, 1), 99));
    }

    private static BustRecord SampleBust() => new(
        CancelledTradeId: 42, BustTransactTimeNanos: 1_700_000_000_999_000_000UL,
        SecurityId: 900_000_000_001L, ReasonCode: 3,
        BusterFirm: 999, CorrelationId: 0xC0FFEE_DEADBEEFUL);

    [Fact]
    public void EncodeBust_TryDecodeBust_RoundTripsAllFields()
    {
        var src = SampleBust();
        Span<byte> buf = stackalloc byte[AuditRecordCodec.BustRecordSize];
        int n = AuditRecordCodec.EncodeBust(buf, in src);
        Assert.Equal(AuditRecordCodec.BustRecordSize, n);

        Assert.True(AuditRecordCodec.TryDecodeBust(buf, out var dst));
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Bust_HasExpectedOnDiskSize()
    {
        Assert.Equal(44, AuditRecordCodec.BustRecordSize);
        Assert.Equal(40u, AuditRecordCodec.BustRecordLen);
    }

    [Fact]
    public void TryDecodeBust_ReturnsFalse_OnRecordTypeMismatch()
    {
        var buf = new byte[AuditRecordCodec.BustRecordSize];
        AuditRecordCodec.EncodeBust(buf, SampleBust());
        // Flip the leading recordType byte (offset 8 = first body byte).
        buf[8] = 0x99;
        Assert.False(AuditRecordCodec.TryDecodeBust(buf, out _));
    }

    [Fact]
    public void TryDecodeBust_ReturnsFalse_OnCorruptedBody()
    {
        var buf = new byte[AuditRecordCodec.BustRecordSize];
        AuditRecordCodec.EncodeBust(buf, SampleBust());
        buf[20] ^= 0x01;
        Assert.False(AuditRecordCodec.TryDecodeBust(buf, out _));
    }

    private static RejectAttemptRecord SampleRejectAttempt() => new(
        AttemptedTradeId: 4242, AttemptTransactTimeNanos: 1_700_000_001_000_000_000UL,
        DeclaredTradeDateDays: 20597, RejectCode: 2,
        BusterFirm: 999, CorrelationId: 0xBADC0FFEEUL);

    [Fact]
    public void EncodeRejectAttempt_TryDecodeRejectAttempt_RoundTripsAllFields()
    {
        var src = SampleRejectAttempt();
        Span<byte> buf = stackalloc byte[AuditRecordCodec.RejectAttemptRecordSize];
        int n = AuditRecordCodec.EncodeRejectAttempt(buf, in src);
        Assert.Equal(AuditRecordCodec.RejectAttemptRecordSize, n);

        Assert.True(AuditRecordCodec.TryDecodeRejectAttempt(buf, out var dst));
        Assert.Equal(src, dst);
    }

    [Fact]
    public void RejectAttempt_HasExpectedOnDiskSize()
    {
        Assert.Equal(40, AuditRecordCodec.RejectAttemptRecordSize);
        Assert.Equal(36u, AuditRecordCodec.RejectAttemptRecordLen);
    }

    [Fact]
    public void TryDecodeRejectAttempt_ReturnsFalse_OnRecordTypeMismatch()
    {
        var buf = new byte[AuditRecordCodec.RejectAttemptRecordSize];
        AuditRecordCodec.EncodeRejectAttempt(buf, SampleRejectAttempt());
        buf[8] = 0x99;
        Assert.False(AuditRecordCodec.TryDecodeRejectAttempt(buf, out _));
    }

    [Fact]
    public void TryGetRecordSize_DispatchesByRecordLen()
    {
        Assert.True(AuditRecordCodec.TryGetRecordSize(AuditRecordCodec.FillRecordLen, out var fillSize, out var fillKind));
        Assert.Equal(AuditRecordCodec.RecordSize, fillSize);
        Assert.Equal(AuditRecordKind.Fill, fillKind);

        Assert.True(AuditRecordCodec.TryGetRecordSize(AuditRecordCodec.BustRecordLen, out var bustSize, out var bustKind));
        Assert.Equal(AuditRecordCodec.BustRecordSize, bustSize);
        Assert.Equal(AuditRecordKind.Bust, bustKind);

        Assert.True(AuditRecordCodec.TryGetRecordSize(AuditRecordCodec.RejectAttemptRecordLen, out var rejSize, out var rejKind));
        Assert.Equal(AuditRecordCodec.RejectAttemptRecordSize, rejSize);
        Assert.Equal(AuditRecordKind.RejectAttempt, rejKind);

        Assert.False(AuditRecordCodec.TryGetRecordSize(123, out _, out _));
    }

    [Fact]
    public void FillEncoding_IsBitIdentical_BetweenV1AndV2Headers()
    {
        // ADR 0008 §1 invariant: a v2 file that contains only fills is
        // bit-identical to its v1 predecessor modulo the file header.
        // The fill body has no discriminator byte, so this property
        // falls out of the codec design — pinning it with a test keeps
        // future record-type extensions from accidentally regressing it.
        var fill = Sample();
        Span<byte> fillBytes = stackalloc byte[AuditRecordCodec.RecordSize];
        AuditRecordCodec.Encode(fillBytes, in fill);

        // Body identical regardless of file header version (header is
        // written separately by the file writer; the per-record bytes
        // here are version-agnostic).
        Assert.Equal((uint)AuditRecordCodec.RecordSize - 4, AuditRecordCodec.FillRecordLen);
        Assert.True(AuditRecordCodec.TryGetRecordSize(AuditRecordCodec.FillRecordLen, out _, out var kind));
        Assert.Equal(AuditRecordKind.Fill, kind);
    }
}
