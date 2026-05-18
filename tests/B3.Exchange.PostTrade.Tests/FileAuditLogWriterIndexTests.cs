using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

public class FileAuditLogWriterIndexTests : IDisposable
{
    private readonly string _root;

    public FileAuditLogWriterIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeIdxTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;

    private static PostTradeRecord Make(uint id, uint buyFirm, uint sellFirm) => new(
        TradeId: id, TransactTimeNanos: Day0Nanos + id, SecurityId: 900_000_000_001L,
        AggressorSide: id % 2 == 0 ? Side.Buy : Side.Sell,
        Quantity: 100, PriceMantissa: 10_0000L + id,
        BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
        BuyFirm: buyFirm, SellFirm: sellFirm,
        BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    [Fact]
    public void Writer_EmitsSidecarIdxAlongsideLog()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 1, indexBlockRecords: 4))
            for (uint i = 1; i <= 10; i++)
                w.OnTrade(Make(i, buyFirm: 10, sellFirm: 20));

        var logPath = Path.Combine(_root, "1", "fills-2026-05-18.log");
        var idxPath = Path.Combine(_root, "1", "fills-2026-05-18.idx");
        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(idxPath));
        // Index header (24) + 3 entries (2 full blocks of 4 + 1 partial of 2),
        // each entry = prefix(14) + 2 distinct firms*4 = 22 bytes.
        var idxLen = new FileInfo(idxPath).Length;
        Assert.Equal(AuditIndexCodec.FileHeaderSize + (3 * (AuditIndexCodec.BlockEntryFixedPrefix + (2 * 4))), idxLen);
    }

    [Fact]
    public void ReadByFirm_ReturnsOnlyMatchingRecords()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 2, indexBlockRecords: 4))
        {
            // Block A (records 1-4): firms 10/20
            for (uint i = 1; i <= 4; i++) w.OnTrade(Make(i, 10, 20));
            // Block B (5-8): firms 30/40 — firm 10 must NOT match this block
            for (uint i = 5; i <= 8; i++) w.OnTrade(Make(i, 30, 40));
            // Block C (9-10): firms 10/50 — firm 10 matches again
            for (uint i = 9; i <= 10; i++) w.OnTrade(Make(i, 10, 50));
        }
        var logPath = Path.Combine(_root, "2", "fills-2026-05-18.log");
        var got = AuditLogReader.ReadByFirm(logPath, firmId: 10).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2, 3, 4, 9, 10 }, got);
    }

    [Fact]
    public void ReadByFirm_NoMatch_ReturnsEmpty()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 3, indexBlockRecords: 4))
            for (uint i = 1; i <= 10; i++) w.OnTrade(Make(i, 10, 20));
        var logPath = Path.Combine(_root, "3", "fills-2026-05-18.log");
        Assert.Empty(AuditLogReader.ReadByFirm(logPath, firmId: 99));
    }

    [Fact]
    public void ReadByFirm_FallsBackToFullScan_WhenIdxMissing()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 4, indexBlockRecords: 4))
            for (uint i = 1; i <= 6; i++) w.OnTrade(Make(i, i <= 3 ? 10u : 50u, 20));
        var logPath = Path.Combine(_root, "4", "fills-2026-05-18.log");
        var idxPath = Path.ChangeExtension(logPath, ".idx");
        File.Delete(idxPath);
        var got = AuditLogReader.ReadByFirm(logPath, firmId: 10).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2, 3 }, got);
    }

    [Fact]
    public void ReadByFirm_FallsBackToFullScan_WhenIdxCorrupt()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 5, indexBlockRecords: 4))
            for (uint i = 1; i <= 6; i++) w.OnTrade(Make(i, i <= 3 ? 10u : 50u, 20));
        var logPath = Path.Combine(_root, "5", "fills-2026-05-18.log");
        var idxPath = Path.ChangeExtension(logPath, ".idx");
        // Corrupt the index magic — reader must fall back, not throw.
        using (var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Write))
        {
            fs.WriteByte(0xFF);
        }
        var got = AuditLogReader.ReadByFirm(logPath, firmId: 10).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2, 3 }, got);
    }

    [Fact]
    public void Writer_Reopen_RebuildsIndexFromLog()
    {
        // First session writes blocks A and B.
        using (var w = new FileAuditLogWriter(_root, channelNumber: 6, indexBlockRecords: 4))
        {
            for (uint i = 1; i <= 4; i++) w.OnTrade(Make(i, 10, 20));
            for (uint i = 5; i <= 8; i++) w.OnTrade(Make(i, 30, 40));
        }
        // Simulate operator wiping the index but keeping the log.
        var logPath = Path.Combine(_root, "6", "fills-2026-05-18.log");
        var idxPath = Path.ChangeExtension(logPath, ".idx");
        File.Delete(idxPath);

        // Second session reopens; rebuild happens during RotateTo + then we
        // append one more block (just 2 records, partial).
        using (var w = new FileAuditLogWriter(_root, channelNumber: 6, indexBlockRecords: 4))
        {
            for (uint i = 9; i <= 10; i++) w.OnTrade(Make(i, 10, 50));
        }
        Assert.True(File.Exists(idxPath));
        var firm10 = AuditLogReader.ReadByFirm(logPath, firmId: 10).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2, 3, 4, 9, 10 }, firm10);
        var firm30 = AuditLogReader.ReadByFirm(logPath, firmId: 30).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 5, 6, 7, 8 }, firm30);
    }

    [Fact]
    public void Writer_Reopen_RebuildIndex_DiscardsStaleIdx()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 7, indexBlockRecords: 4))
            for (uint i = 1; i <= 6; i++) w.OnTrade(Make(i, 10, 20));
        var idxPath = Path.Combine(_root, "7", "fills-2026-05-18.idx");
        var beforeLen = new FileInfo(idxPath).Length;

        // Reopen without adding records: writer recreates a fresh .idx that
        // covers exactly the records present on disk.
        using (var w = new FileAuditLogWriter(_root, channelNumber: 7, indexBlockRecords: 4))
            w.OnTrade(Make(7, 10, 20));
        var afterLen = new FileInfo(idxPath).Length;

        // The new file must NOT be the byte-for-byte concatenation of the
        // old (which would be the bug of "kept appending to stale idx").
        // After reopen+1 record we have 2 full blocks of 4 (rebuilt) + a
        // partial block of 1 from the new record's Dispose flush.
        var expected = AuditIndexCodec.FileHeaderSize
            + (AuditIndexCodec.BlockEntryFixedPrefix + (2 * 4))  // rebuilt block A
            + (AuditIndexCodec.BlockEntryFixedPrefix + (2 * 4))  // rebuilt block B (partial of 2 records, still 2 firms)
            + (AuditIndexCodec.BlockEntryFixedPrefix + (2 * 4)); // new partial block (record 7) with firms 10/20
        Assert.Equal(expected, afterLen);
        Assert.NotEqual(beforeLen * 2, afterLen);
    }

    [Fact]
    public void ReadByFirm_AfterCheckpoint_SeesInProgressBlock()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 8, indexBlockRecords: 64);
        for (uint i = 1; i <= 3; i++) w.OnTrade(Make(i, 10, 20));
        w.Checkpoint();
        var logPath = Path.Combine(_root, "8", "fills-2026-05-18.log");
        var got = AuditLogReader.ReadByFirm(logPath, firmId: 10).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2, 3 }, got);
    }
}
