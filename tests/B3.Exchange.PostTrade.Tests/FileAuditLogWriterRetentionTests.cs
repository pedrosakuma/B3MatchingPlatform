using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>
/// Issue #329 PR-6: retention policy tests for
/// <see cref="FileAuditLogWriter.PruneOldDays(DateOnly, int)"/>. The
/// pruner must delete <c>fills-YYYY-MM-DD.log</c>/<c>.idx</c> pairs
/// older than the cutoff, never touch the currently-open day, never
/// touch the per-channel watermark sidecar, and ignore non-matching
/// filenames.
/// </summary>
public class FileAuditLogWriterRetentionTests : IDisposable
{
    private readonly string _root;

    public FileAuditLogWriterRetentionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeRetention_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Prune_DeletesFiles_OlderThanCutoff()
    {
        var channelDir = Path.Combine(_root, "9");
        Directory.CreateDirectory(channelDir);
        var oldDates = new[] { "2024-01-01", "2024-06-15", "2025-12-31" };
        var freshDates = new[] { "2026-05-15", "2026-05-16", "2026-05-17" };
        foreach (var d in oldDates.Concat(freshDates))
        {
            File.WriteAllText(Path.Combine(channelDir, $"fills-{d}.log"), "x");
            File.WriteAllText(Path.Combine(channelDir, $"fills-{d}.idx"), "x");
        }

        using var w = new FileAuditLogWriter(_root, channelNumber: 9);
        // Today = 2026-05-18, retention = 3 days → cutoff = 2026-05-15.
        // Files with date < cutoff are deleted; date >= cutoff stays.
        int deleted = w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: 3);

        // 3 old dates × 2 files each = 6 deletions.
        Assert.Equal(6, deleted);
        foreach (var d in oldDates)
        {
            Assert.False(File.Exists(Path.Combine(channelDir, $"fills-{d}.log")));
            Assert.False(File.Exists(Path.Combine(channelDir, $"fills-{d}.idx")));
        }
        foreach (var d in freshDates)
        {
            Assert.True(File.Exists(Path.Combine(channelDir, $"fills-{d}.log")));
            Assert.True(File.Exists(Path.Combine(channelDir, $"fills-{d}.idx")));
        }
    }

    [Fact]
    public void Prune_NeverDeletes_CurrentlyOpenDay()
    {
        // The active day is whatever the writer last appended to. Even
        // if today's UTC date is far in the future and retentionDays
        // would otherwise sweep it, the open file must survive.
        using var w = new FileAuditLogWriter(_root, channelNumber: 11);
        // 2026-05-18: write a record so the writer opens fills-2026-05-18.{log,idx}.
        w.OnTrade(MakeRecord(1, Day0Nanos));
        var logPath = Path.Combine(_root, "11", "fills-2026-05-18.log");
        var idxPath = Path.Combine(_root, "11", "fills-2026-05-18.idx");
        Assert.True(File.Exists(logPath));

        // todayUtc = far future, retention = 1 day → cutoff = far future - 1.
        // Without the open-day guard this would delete the active file.
        int deleted = w.PruneOldDays(new DateOnly(2030, 1, 1), retentionDays: 1);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(idxPath));
    }

    [Fact]
    public void Prune_NeverDeletes_WatermarkSidecar()
    {
        var channelDir = Path.Combine(_root, "12");
        Directory.CreateDirectory(channelDir);
        // Seed an audit-watermark.bin alongside an old day. The sidecar is
        // per-channel and must outlive any daily prune (its name does not
        // match the fills-YYYY-MM-DD.{log,idx} pattern).
        var sidecarPath = Path.Combine(channelDir, "audit-watermark.bin");
        File.WriteAllBytes(sidecarPath, new byte[AuditWatermarkCodec.FileSize]);
        File.WriteAllText(Path.Combine(channelDir, "fills-2024-01-01.log"), "x");
        File.WriteAllText(Path.Combine(channelDir, "fills-2024-01-01.idx"), "x");

        using var w = new FileAuditLogWriter(_root, channelNumber: 12);
        int deleted = w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: 7);

        Assert.Equal(2, deleted);
        Assert.True(File.Exists(sidecarPath),
            "watermark sidecar must survive daily prune");
    }

    [Fact]
    public void Prune_IgnoresMalformedAndUnrelatedFiles()
    {
        var channelDir = Path.Combine(_root, "13");
        Directory.CreateDirectory(channelDir);
        // Genuine old file that SHOULD be deleted.
        File.WriteAllText(Path.Combine(channelDir, "fills-2024-01-01.log"), "x");
        // Files that look-like but don't match the strict pattern.
        File.WriteAllText(Path.Combine(channelDir, "fills-not-a-date.log"), "x");
        File.WriteAllText(Path.Combine(channelDir, "fills-2024-13-40.log"), "x"); // invalid month/day
        File.WriteAllText(Path.Combine(channelDir, "fills-2024-01-01.bak"), "x"); // wrong extension
        File.WriteAllText(Path.Combine(channelDir, "random.log"), "x");           // wrong prefix

        using var w = new FileAuditLogWriter(_root, channelNumber: 13);
        int deleted = w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: 7);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(channelDir, "fills-2024-01-01.log")));
        Assert.True(File.Exists(Path.Combine(channelDir, "fills-not-a-date.log")));
        Assert.True(File.Exists(Path.Combine(channelDir, "fills-2024-13-40.log")));
        Assert.True(File.Exists(Path.Combine(channelDir, "fills-2024-01-01.bak")));
        Assert.True(File.Exists(Path.Combine(channelDir, "random.log")));
    }

    [Fact]
    public void Prune_NoChannelDir_ReturnsZero()
    {
        // PruneOldDays runs before any OnTrade → channel directory may
        // not exist yet. Must not throw.
        using var w = new FileAuditLogWriter(_root, channelNumber: 14);
        int deleted = w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: 1);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void Prune_RetentionDaysLessThanOne_Throws()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 15);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: -1));
    }

    [Fact]
    public void Prune_BoundaryCutoffIsInclusive_KeepsCutoffDay()
    {
        // cutoff = todayUtc - retentionDays. Files dated >= cutoff are
        // kept; only date < cutoff is deleted. This pins the boundary
        // contract so a future refactor cannot silently shift it.
        var channelDir = Path.Combine(_root, "16");
        Directory.CreateDirectory(channelDir);
        File.WriteAllText(Path.Combine(channelDir, "fills-2026-05-10.log"), "x"); // cutoff - 1 → DELETE
        File.WriteAllText(Path.Combine(channelDir, "fills-2026-05-11.log"), "x"); // cutoff      → KEEP
        File.WriteAllText(Path.Combine(channelDir, "fills-2026-05-12.log"), "x"); // cutoff + 1 → KEEP

        using var w = new FileAuditLogWriter(_root, channelNumber: 16);
        // today = 2026-05-18, retention = 7 → cutoff = 2026-05-11.
        int deleted = w.PruneOldDays(new DateOnly(2026, 5, 18), retentionDays: 7);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(channelDir, "fills-2026-05-10.log")));
        Assert.True(File.Exists(Path.Combine(channelDir, "fills-2026-05-11.log")));
        Assert.True(File.Exists(Path.Combine(channelDir, "fills-2026-05-12.log")));
    }

    // 2026-05-18 00:00:00 UTC (matches Day0 used elsewhere in the suite).
    private static readonly ulong Day0Nanos =
        (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;

    private static PostTradeRecord MakeRecord(uint id, ulong ts) => new(
        TradeId: id, TransactTimeNanos: ts, SecurityId: 900_000_000_001L,
        AggressorSide: Side.Buy, Quantity: 100, PriceMantissa: 10_0000L,
        BuyClOrdId: 1000UL, SellClOrdId: 2000UL,
        BuyFirm: 7, SellFirm: 8,
        BuyOrderId: 5001, SellOrderId: 6001);
}
