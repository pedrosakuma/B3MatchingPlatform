using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>
/// Tests for the schema-v2 reader dispatch added in ADR 0008 PR-1
/// (issue #369). Exercises the per-recordLen routing in
/// <see cref="AuditLogReader.ReadAllEntries"/> on hand-built fixture
/// files so we don't depend on the writer (which is not yet emitting
/// non-fill records in this PR).
/// </summary>
public class AuditLogReaderV2Tests
{
    private static PostTradeRecord SampleFill(uint tradeId = 1, uint buyFirm = 100, uint sellFirm = 200) => new(
        TradeId: tradeId, TransactTimeNanos: 1_700_000_000_000_000_000UL + tradeId,
        SecurityId: 900_000_000_001L, AggressorSide: Side.Buy,
        Quantity: 100, PriceMantissa: 12_345_678L,
        BuyClOrdId: tradeId * 10UL, SellClOrdId: tradeId * 10UL + 1,
        BuyFirm: buyFirm, SellFirm: sellFirm,
        BuyOrderId: 1000 + tradeId, SellOrderId: 2000 + tradeId);

    private static BustRecord SampleBust(uint cancelledTradeId, ulong correlationId) => new(
        CancelledTradeId: cancelledTradeId,
        BustTransactTimeNanos: 1_700_000_000_500_000_000UL,
        SecurityId: 900_000_000_001L, ReasonCode: 1,
        BusterFirm: 999, CorrelationId: correlationId);

    private static RejectAttemptRecord SampleRejectAttempt(uint attemptedTradeId, ulong correlationId, ushort rejectCode) => new(
        AttemptedTradeId: attemptedTradeId,
        AttemptTransactTimeNanos: 1_700_000_000_600_000_000UL,
        DeclaredTradeDateDays: 20597, RejectCode: rejectCode,
        BusterFirm: 999, CorrelationId: correlationId);

    private static string WriteFixture(
        string path,
        byte channelNumber,
        DateOnly tradeDate,
        ushort schemaVersion,
        Action<FileStream> writeBody)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var header = new byte[AuditRecordCodec.FileHeaderSize];
        AuditRecordCodec.WriteFileHeader(header, channelNumber, tradeDate, schemaVersion);
        fs.Write(header, 0, header.Length);
        writeBody(fs);
        return path;
    }

    [Fact]
    public void ReadAllEntries_OnV1File_YieldsOnlyFills_InOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-v1-{Guid.NewGuid():N}.log");
        try
        {
            var fillA = SampleFill(1);
            var fillB = SampleFill(2);
            WriteFixture(path, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV1, fs =>
            {
                var buf = new byte[AuditRecordCodec.RecordSize];
                AuditRecordCodec.Encode(buf, in fillA); fs.Write(buf);
                AuditRecordCodec.Encode(buf, in fillB); fs.Write(buf);
            });

            var entries = AuditLogReader.ReadAllEntries(path).ToList();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Equal(AuditRecordKind.Fill, e.Kind));
            Assert.Equal(fillA, entries[0].Fill);
            Assert.Equal(fillB, entries[1].Fill);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllEntries_OnV1File_TerminatesQuietly_OnNonFillRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-v1-corrupt-{Guid.NewGuid():N}.log");
        try
        {
            WriteFixture(path, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV1, fs =>
            {
                var fillBuf = new byte[AuditRecordCodec.RecordSize];
                AuditRecordCodec.Encode(fillBuf, SampleFill(1)); fs.Write(fillBuf);
                // Now write a perfectly valid bust record (recordLen=40).
                // A v1 reader treats this as corruption and stops here.
                var bustBuf = new byte[AuditRecordCodec.BustRecordSize];
                AuditRecordCodec.EncodeBust(bustBuf, SampleBust(1, 0xCAFE)); fs.Write(bustBuf);
            });

            var entries = AuditLogReader.ReadAllEntries(path).ToList();
            Assert.Single(entries);
            Assert.Equal(AuditRecordKind.Fill, entries[0].Kind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllEntries_OnV2File_DispatchesAllThreeRecordTypes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-v2-mixed-{Guid.NewGuid():N}.log");
        try
        {
            var fillA = SampleFill(1);
            var bust = SampleBust(1, 0xDEAD);
            var rej = SampleRejectAttempt(99, 0xBEEF, 1);
            var fillB = SampleFill(2);
            WriteFixture(path, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV2, fs =>
            {
                var fillBuf = new byte[AuditRecordCodec.RecordSize];
                AuditRecordCodec.Encode(fillBuf, in fillA); fs.Write(fillBuf);
                var bustBuf = new byte[AuditRecordCodec.BustRecordSize];
                AuditRecordCodec.EncodeBust(bustBuf, in bust); fs.Write(bustBuf);
                var rejBuf = new byte[AuditRecordCodec.RejectAttemptRecordSize];
                AuditRecordCodec.EncodeRejectAttempt(rejBuf, in rej); fs.Write(rejBuf);
                AuditRecordCodec.Encode(fillBuf, in fillB); fs.Write(fillBuf);
            });

            var entries = AuditLogReader.ReadAllEntries(path).ToList();
            Assert.Equal(4, entries.Count);
            Assert.Equal(AuditRecordKind.Fill, entries[0].Kind);
            Assert.Equal(fillA, entries[0].Fill);
            Assert.Equal(AuditRecordKind.Bust, entries[1].Kind);
            Assert.Equal(bust, entries[1].Bust);
            Assert.Equal(AuditRecordKind.RejectAttempt, entries[2].Kind);
            Assert.Equal(rej, entries[2].RejectAttempt);
            Assert.Equal(AuditRecordKind.Fill, entries[3].Kind);
            Assert.Equal(fillB, entries[3].Fill);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAll_OnV2File_SkipsBustAndRejectRecords()
    {
        // Back-compat contract: existing callers of ReadAll (fills-only)
        // must keep getting only fills even when the underlying file is v2.
        var path = Path.Combine(Path.GetTempPath(), $"audit-v2-readall-{Guid.NewGuid():N}.log");
        try
        {
            var fillA = SampleFill(1);
            var bust = SampleBust(1, 0xDEAD);
            var fillB = SampleFill(2);
            WriteFixture(path, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV2, fs =>
            {
                var fillBuf = new byte[AuditRecordCodec.RecordSize];
                AuditRecordCodec.Encode(fillBuf, in fillA); fs.Write(fillBuf);
                var bustBuf = new byte[AuditRecordCodec.BustRecordSize];
                AuditRecordCodec.EncodeBust(bustBuf, in bust); fs.Write(bustBuf);
                AuditRecordCodec.Encode(fillBuf, in fillB); fs.Write(fillBuf);
            });

            var fills = AuditLogReader.ReadAll(path).ToList();
            Assert.Equal(2, fills.Count);
            Assert.Equal(fillA, fills[0]);
            Assert.Equal(fillB, fills[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllEntries_TerminatesQuietly_OnUnknownRecordLen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-v2-junk-{Guid.NewGuid():N}.log");
        try
        {
            WriteFixture(path, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV2, fs =>
            {
                var fillBuf = new byte[AuditRecordCodec.RecordSize];
                AuditRecordCodec.Encode(fillBuf, SampleFill(1)); fs.Write(fillBuf);
                // 4-byte length prefix declaring an unknown record length.
                fs.Write(BitConverter.GetBytes((uint)123));
                fs.Write(new byte[100]); // junk body
            });

            var entries = AuditLogReader.ReadAllEntries(path).ToList();
            Assert.Single(entries);
            Assert.Equal(AuditRecordKind.Fill, entries[0].Kind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllEntries_TerminatesQuietly_OnTruncatedTail()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-v2-truncated-{Guid.NewGuid():N}.log");
        try
        {
            WriteFixture(path, 7, new DateOnly(2026, 5, 19), AuditRecordCodec.SchemaVersionV2, fs =>
            {
                var fillBuf = new byte[AuditRecordCodec.RecordSize];
                AuditRecordCodec.Encode(fillBuf, SampleFill(1)); fs.Write(fillBuf);
                // Length prefix for a bust, but no body — partial trailing
                // record on writer crash.
                fs.Write(BitConverter.GetBytes(AuditRecordCodec.BustRecordLen));
            });

            var entries = AuditLogReader.ReadAllEntries(path).ToList();
            Assert.Single(entries);
        }
        finally { File.Delete(path); }
    }
}
