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
    public void EncodeBust_V3_RoundTripsDeclaredTradeDate()
    {
        // ADR 0008 PR-4: declaredTradeDate (LocalMktDate days since
        // 1970-01-01) survives a v3 encode/decode round-trip.
        var src = SampleBust() with { DeclaredTradeDateDays = 20597 };
        Span<byte> buf = stackalloc byte[AuditRecordCodec.BustRecordSize];
        AuditRecordCodec.EncodeBust(buf, in src);
        Assert.True(AuditRecordCodec.TryDecodeBust(buf, out var dst));
        Assert.Equal(20597, dst.DeclaredTradeDateDays);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void TryGetRecordSize_AcceptsBothV2AndV3BustLengths()
    {
        // ADR 0008 PR-4: schema bump V2→V3 added declaredTradeDate to
        // BustRecord (recordLen 40→44). The dispatcher and the writer's
        // recovery scan both call TryGetRecordSize per-record, so the
        // size table must keep accepting v2-on-disk records to honour
        // the per-file-header schema view.
        Assert.True(AuditRecordCodec.TryGetRecordSize(AuditRecordCodec.BustRecordV2Len, out var v2Size, out var v2Kind));
        Assert.Equal(AuditRecordCodec.BustRecordV2Size, v2Size);
        Assert.Equal(AuditRecordKind.Bust, v2Kind);
        Assert.True(AuditRecordCodec.TryGetRecordSize(AuditRecordCodec.BustRecordV3Len, out var v3Size, out var v3Kind));
        Assert.Equal(AuditRecordCodec.BustRecordV3Size, v3Size);
        Assert.Equal(AuditRecordKind.Bust, v3Kind);
    }

    [Fact]
    public void Bust_HasExpectedOnDiskSize()
    {
        Assert.Equal(48, AuditRecordCodec.BustRecordSize);
        Assert.Equal(44u, AuditRecordCodec.BustRecordLen);
    }

    [Fact]
    public void TryDecodeBust_ReturnsFalse_OnRecordTypeMismatch()
    {
        var buf = new byte[AuditRecordCodec.BustRecordSize];
        AuditRecordCodec.EncodeBust(buf, SampleBust());
        // Flip the leading recordType byte (offset 8 = first body byte)
        // and re-compute the CRC so the decoder reaches the explicit
        // recordType cross-check rather than short-circuiting on CRC.
        buf[8] = 0x99;
        RewriteBodyCrc(buf, bodyOffset: 8, bodyLen: AuditRecordCodec.BustRecordBodySize);
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
        RewriteBodyCrc(buf, bodyOffset: 8, bodyLen: AuditRecordCodec.RejectAttemptRecordBodySize);
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
        // falls out of the codec design — pinning it with two encodes
        // + a byte-vector compare keeps future record-type extensions
        // from accidentally regressing it.
        var fill = Sample();
        var fillBytesA = new byte[AuditRecordCodec.RecordSize];
        var fillBytesB = new byte[AuditRecordCodec.RecordSize];
        AuditRecordCodec.Encode(fillBytesA, in fill);
        AuditRecordCodec.Encode(fillBytesB, in fill);
        Assert.Equal(fillBytesA, fillBytesB);

        // Compose full v1 and v2 files (header + same fill) and assert
        // the bodies past the file header match byte-for-byte.
        var dateA = new DateOnly(2026, 5, 19);
        var dateB = new DateOnly(2026, 5, 19);
        var fileV1 = new byte[AuditRecordCodec.FileHeaderSize + AuditRecordCodec.RecordSize];
        var fileV2 = new byte[AuditRecordCodec.FileHeaderSize + AuditRecordCodec.RecordSize];
        AuditRecordCodec.WriteFileHeader(fileV1, 7, dateA, AuditRecordCodec.SchemaVersionV1);
        AuditRecordCodec.WriteFileHeader(fileV2, 7, dateB, AuditRecordCodec.SchemaVersionV2);
        AuditRecordCodec.Encode(fileV1.AsSpan(AuditRecordCodec.FileHeaderSize), in fill);
        AuditRecordCodec.Encode(fileV2.AsSpan(AuditRecordCodec.FileHeaderSize), in fill);

        // Bodies (past file header) MUST match byte-for-byte.
        var bodyV1 = fileV1.AsSpan(AuditRecordCodec.FileHeaderSize).ToArray();
        var bodyV2 = fileV2.AsSpan(AuditRecordCodec.FileHeaderSize).ToArray();
        Assert.Equal(bodyV1, bodyV2);

        // Headers MUST differ on exactly the schemaVersion byte (offset 4).
        Assert.NotEqual(fileV1[4], fileV2[4]);
        Assert.Equal((byte)AuditRecordCodec.SchemaVersionV1, fileV1[4]);
        Assert.Equal((byte)AuditRecordCodec.SchemaVersionV2, fileV2[4]);
    }

    /// <summary>Recomputes the framed-record CRC after the body has
    /// been mutated by a test, so the decoder under test reaches the
    /// post-CRC validation (e.g. recordType cross-check) rather than
    /// short-circuiting at the CRC step.</summary>
    private static void RewriteBodyCrc(byte[] buf, int bodyOffset, int bodyLen)
    {
        Span<byte> hash = stackalloc byte[4];
        System.IO.Hashing.Crc32.Hash(buf.AsSpan(bodyOffset, bodyLen), hash);
        hash.CopyTo(buf.AsSpan(4, 4));
    }
}
