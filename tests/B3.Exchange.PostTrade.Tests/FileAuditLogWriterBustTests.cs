using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>ADR 0008 PR-2 tests for the bust + reject-attempt
/// extensions of <see cref="FileAuditLogWriter"/>.</summary>
public class FileAuditLogWriterBustTests : IDisposable
{
    private readonly string _root;

    public FileAuditLogWriterBustTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeBustTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private static readonly DateOnly Day0 = new(2026, 5, 18);
    private const ulong OneDayNanos = 86_400UL * 1_000_000_000UL;

    private static PostTradeRecord MakeFill(uint id, ulong ts, long secId = 900_000_000_001L) => new(
        TradeId: id, TransactTimeNanos: ts, SecurityId: secId, AggressorSide: Side.Buy,
        Quantity: 100, PriceMantissa: 10_0000L,
        BuyClOrdId: 1000UL, SellClOrdId: 2000UL,
        BuyFirm: 7, SellFirm: 8,
        BuyOrderId: 5000, SellOrderId: 6000);

    private static BustRecord MakeBust(uint tradeId, ulong correlationId) =>
        new(CancelledTradeId: tradeId, BustTransactTimeNanos: Day0Nanos + 5UL * 60UL * 1_000_000_000UL,
            SecurityId: 900_000_000_001L, ReasonCode: 1, BusterFirm: 99, CorrelationId: correlationId);

    private static RejectAttemptRecord MakeReject(uint tradeId, ushort code) =>
        new(AttemptedTradeId: tradeId, AttemptTransactTimeNanos: Day0Nanos + 10UL * 60UL * 1_000_000_000UL,
            DeclaredTradeDateDays: Day0.DayNumber, RejectCode: code, BusterFirm: 99, CorrelationId: 555UL);

    [Fact]
    public void OnBust_SameDayInline_AppendsAndIsReadableViaReadAllEntries()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 7))
        {
            w.OnTrade(MakeFill(1, Day0Nanos));
            w.OnBust(MakeBust(1, correlationId: 42UL), Day0);
        }
        var path = Path.Combine(_root, "7", "fills-2026-05-18.log");
        var entries = AuditLogReader.ReadAllEntries(path).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(AuditRecordKind.Fill, entries[0].Kind);
        Assert.Equal(AuditRecordKind.Bust, entries[1].Kind);
        Assert.Equal(1u, entries[1].Bust.CancelledTradeId);
        Assert.Equal(42UL, entries[1].Bust.CorrelationId);
    }

    [Fact]
    public void OnBust_CrossDay_OpensTransientFileAndAppends()
    {
        // Writer is "today" = day+1; bust references day 0.
        using (var w = new FileAuditLogWriter(_root, channelNumber: 8))
        {
            w.OnTrade(MakeFill(1, Day0Nanos));         // creates day-0 file
            w.OnTrade(MakeFill(2, Day0Nanos + OneDayNanos)); // rotates to day-1
            w.OnBust(MakeBust(1, correlationId: 7UL), Day0);
        }
        var day0Path = Path.Combine(_root, "8", "fills-2026-05-18.log");
        var day1Path = Path.Combine(_root, "8", "fills-2026-05-19.log");

        var day0Entries = AuditLogReader.ReadAllEntries(day0Path).ToList();
        Assert.Equal(2, day0Entries.Count);
        Assert.Equal(AuditRecordKind.Fill, day0Entries[0].Kind);
        Assert.Equal(AuditRecordKind.Bust, day0Entries[1].Kind);
        Assert.Equal(7UL, day0Entries[1].Bust.CorrelationId);

        var day1Entries = AuditLogReader.ReadAllEntries(day1Path).ToList();
        var only = Assert.Single(day1Entries);
        Assert.Equal(AuditRecordKind.Fill, only.Kind);
        Assert.Equal(2u, only.Fill.TradeId);
    }

    [Fact]
    public void OnBust_CrossDay_NewFileGetsValidHeader()
    {
        // Bust day has no fills at all -> writer must create the file +
        // write the v2 header before appending.
        using (var w = new FileAuditLogWriter(_root, channelNumber: 9))
        {
            // Open writer with a today-record to make _currentDate != bust day.
            w.OnTrade(MakeFill(1, Day0Nanos + OneDayNanos));
            w.OnBust(MakeBust(99, correlationId: 1UL), Day0); // creates day-0 file from scratch
        }
        var day0Path = Path.Combine(_root, "9", "fills-2026-05-18.log");
        Assert.True(File.Exists(day0Path));
        var entries = AuditLogReader.ReadAllEntries(day0Path).ToList();
        var only = Assert.Single(entries);
        Assert.Equal(AuditRecordKind.Bust, only.Kind);
        Assert.Equal(99u, only.Bust.CancelledTradeId);
    }

    [Fact]
    public void OnRejectAttempt_WritesToTodayFile()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 10))
        {
            w.OnTrade(MakeFill(1, Day0Nanos));
            w.OnRejectAttempt(MakeReject(42, code: 1));
        }
        var path = Path.Combine(_root, "10", "fills-2026-05-18.log");
        var entries = AuditLogReader.ReadAllEntries(path).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(AuditRecordKind.Fill, entries[0].Kind);
        Assert.Equal(AuditRecordKind.RejectAttempt, entries[1].Kind);
        Assert.Equal(42u, entries[1].RejectAttempt.AttemptedTradeId);
        Assert.Equal((ushort)1, entries[1].RejectAttempt.RejectCode);
    }

    [Fact]
    public void NewFilesUseSchemaV2_AndAreReadableAfterReopen()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 11))
        {
            w.OnTrade(MakeFill(1, Day0Nanos));
            w.OnBust(MakeBust(1, correlationId: 99UL), Day0);
        }
        var path = Path.Combine(_root, "11", "fills-2026-05-18.log");
        // Spot-check the header byte for the schema-v2 marker.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > AuditRecordCodec.FileHeaderSize);
        // schemaVersion lives at the start of the header (per codec layout).
        var (_, _, sv) = AuditRecordCodec.ReadFileHeader(bytes.AsSpan(0, AuditRecordCodec.FileHeaderSize));
        Assert.Equal(AuditRecordCodec.SchemaVersionV2, sv);

        // Reopen and append more — should not truncate anything.
        using (var w = new FileAuditLogWriter(_root, channelNumber: 11))
        {
            w.OnTrade(MakeFill(2, Day0Nanos));
        }
        var entries = AuditLogReader.ReadAllEntries(path).ToList();
        Assert.Equal(3, entries.Count);
        Assert.Equal(AuditRecordKind.Fill, entries[0].Kind);
        Assert.Equal(AuditRecordKind.Bust, entries[1].Kind);
        Assert.Equal(AuditRecordKind.Fill, entries[2].Kind);
    }
}
