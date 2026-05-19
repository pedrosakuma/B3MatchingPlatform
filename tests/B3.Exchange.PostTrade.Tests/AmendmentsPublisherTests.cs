using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>ADR 0008 PR-4 tests for <see cref="AmendmentsPublisher"/> —
/// the post-EOD amendments-file producer.</summary>
public sealed class AmendmentsPublisherTests : IDisposable
{
    private readonly string _auditRoot;
    private readonly string _dropRoot;
    private const byte Channel = 7;
    private static readonly DateOnly TradeDate = new(2026, 5, 18);
    private static readonly DateOnly BustToday = new(2026, 5, 20);
    private static readonly DateTime GeneratedAt = new(2026, 5, 20, 18, 0, 0, DateTimeKind.Utc);
    private static readonly ulong TradeDateNanos = (ulong)(TradeDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private static readonly ulong BustTodayNanos = (ulong)(BustToday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private static readonly int TradeDateDays = TradeDate.DayNumber - new DateOnly(1970, 1, 1).DayNumber;

    public AmendmentsPublisherTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "B3AmendPubTests_" + Guid.NewGuid().ToString("N"));
        _auditRoot = Path.Combine(root, "audit");
        _dropRoot = Path.Combine(root, "drop");
        Directory.CreateDirectory(_auditRoot);
        Directory.CreateDirectory(_dropRoot);
    }

    public void Dispose()
    {
        try
        {
            var root = Path.GetDirectoryName(_auditRoot);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* swallow */ }
    }

    private static PostTradeRecord Fill(uint id, ulong ts) => new(
        TradeId: id, TransactTimeNanos: ts, SecurityId: 900_000_000_001L,
        AggressorSide: Side.Buy, Quantity: 100, PriceMantissa: 10_0000L,
        BuyClOrdId: 1000UL, SellClOrdId: 2000UL, BuyFirm: 7, SellFirm: 8,
        BuyOrderId: 5000, SellOrderId: 6000);

    private static BustRecord Bust(uint tradeId, ulong ts, ulong correlationId, int declaredTradeDateDays) =>
        new(CancelledTradeId: tradeId, BustTransactTimeNanos: ts,
            SecurityId: 900_000_000_001L, ReasonCode: 5, BusterFirm: 99,
            CorrelationId: correlationId, DeclaredTradeDateDays: declaredTradeDateDays);

    private void PublishFillsCsv(IEnumerable<PostTradeRecord> fills)
    {
        // Use EodFillsExporter to publish a real fills.csv for the
        // tradeDate, so amendments hashes match what a consumer would
        // compute against the same file.
        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            foreach (var f in fills) w.OnTrade(f);
        }
        var exporter = new EodFillsExporter();
        exporter.Export(_auditRoot, _dropRoot, Channel, TradeDate,
            _ => "PETR4", GeneratedAt);
    }

    [Fact]
    public void Publish_EmitsAmendmentRow_WithSha256OfOriginalFillsRow()
    {
        PublishFillsCsv(new[] { Fill(1, TradeDateNanos + 1_000UL), Fill(2, TradeDateNanos + 2_000UL) });

        // Post-EOD bust lands in fills-<bustToday>.log but carries
        // declaredTradeDate = TradeDate.
        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            w.OnBust(Bust(1, BustTodayNanos + 1_000UL, correlationId: 42UL, declaredTradeDateDays: TradeDateDays), BustToday);
        }

        var publisher = new AmendmentsPublisher();
        publisher.Publish(_auditRoot, _dropRoot, Channel, TradeDate, GeneratedAt);

        var dropDir = Path.Combine(_dropRoot, Channel.ToString(CultureInfo.InvariantCulture),
            TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var amendCsv = Path.Combine(dropDir, "amendments.csv");
        var amendDone = Path.Combine(dropDir, "amendments.csv.done");
        Assert.True(File.Exists(amendCsv));
        Assert.True(File.Exists(amendDone));

        var lines = File.ReadAllLines(amendCsv);
        Assert.Equal(2, lines.Length); // header + 1 row
        Assert.Equal("cancelTradeId,bustTransactTime,reasonCode,correlationId,sha256OfOriginalFillRow", lines[0]);
        var cols = lines[1].Split(',');
        Assert.Equal("1", cols[0]);
        Assert.Equal("5", cols[2]);
        Assert.Equal("42", cols[3]);

        // Cross-check sha256 column against bytes 0..first '\n' AFTER
        // the header row in fills.csv (the row whose tradeId == 1).
        var fillsBytes = File.ReadAllBytes(Path.Combine(dropDir, "fills.csv"));
        int firstLf = Array.IndexOf(fillsBytes, (byte)'\n');
        int secondLf = Array.IndexOf(fillsBytes, (byte)'\n', firstLf + 1);
        var rowBytes = fillsBytes.AsSpan(firstLf + 1, secondLf - firstLf).ToArray();
        var expected = Convert.ToHexString(SHA256.HashData(rowBytes)).ToLowerInvariant();
        Assert.Equal(expected, cols[4]);
    }

    [Fact]
    public void Publish_RegeneratesFromAuditLog_OnRepeatedCalls()
    {
        PublishFillsCsv(new[] { Fill(1, TradeDateNanos + 1_000UL), Fill(2, TradeDateNanos + 2_000UL) });

        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            w.OnBust(Bust(1, BustTodayNanos + 1_000UL, 42UL, TradeDateDays), BustToday);
        }
        var publisher = new AmendmentsPublisher();
        publisher.Publish(_auditRoot, _dropRoot, Channel, TradeDate, GeneratedAt);

        // Second bust appended; re-publish must reflect both rows
        // (regenerated from audit log, not appended).
        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            w.OnBust(Bust(2, BustTodayNanos + 2_000UL, 43UL, TradeDateDays), BustToday);
        }
        publisher.Publish(_auditRoot, _dropRoot, Channel, TradeDate, GeneratedAt.AddSeconds(1));

        var dropDir = Path.Combine(_dropRoot, Channel.ToString(CultureInfo.InvariantCulture),
            TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var lines = File.ReadAllLines(Path.Combine(dropDir, "amendments.csv"));
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.StartsWith("1,", lines[1]);
        Assert.StartsWith("2,", lines[2]);
    }

    [Fact]
    public void Publish_FiltersBustsByDeclaredTradeDate()
    {
        PublishFillsCsv(new[] { Fill(1, TradeDateNanos + 1_000UL) });

        // Two busts: one targets TradeDate, the other targets a
        // different day; only the former should appear.
        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            w.OnBust(Bust(1, BustTodayNanos + 1_000UL, 42UL, TradeDateDays), BustToday);
            w.OnBust(Bust(99, BustTodayNanos + 2_000UL, 43UL, TradeDateDays + 1), BustToday);
        }
        var publisher = new AmendmentsPublisher();
        publisher.Publish(_auditRoot, _dropRoot, Channel, TradeDate, GeneratedAt);

        var dropDir = Path.Combine(_dropRoot, Channel.ToString(CultureInfo.InvariantCulture),
            TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var lines = File.ReadAllLines(Path.Combine(dropDir, "amendments.csv"));
        Assert.Equal(2, lines.Length); // header + only the TradeDate-targeting bust
        Assert.StartsWith("1,", lines[1]);
    }

    [Fact]
    public void Publish_DoneSidecar_HashesPublishedCsvBytes()
    {
        PublishFillsCsv(new[] { Fill(1, TradeDateNanos + 1_000UL) });
        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            w.OnBust(Bust(1, BustTodayNanos + 1_000UL, 42UL, TradeDateDays), BustToday);
        }
        var publisher = new AmendmentsPublisher();
        publisher.Publish(_auditRoot, _dropRoot, Channel, TradeDate, GeneratedAt);

        var dropDir = Path.Combine(_dropRoot, Channel.ToString(CultureInfo.InvariantCulture),
            TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var csvBytes = File.ReadAllBytes(Path.Combine(dropDir, "amendments.csv"));
        var expected = Convert.ToHexString(SHA256.HashData(csvBytes)).ToLowerInvariant();
        var doneJson = File.ReadAllText(Path.Combine(dropDir, "amendments.csv.done"));
        Assert.Contains("\"sha256\":\"" + expected + "\"", doneJson);
    }
}
