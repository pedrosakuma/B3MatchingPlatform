using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

public class FileAuditLogWriterTests : IDisposable
{
    private readonly string _root;

    public FileAuditLogWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Writer_Reopen_TruncatesTornTail_AndAppendsAreReadable()
    {
        // Write two clean records, then simulate a torn tail by appending
        // a half-record of garbage. After reopening, the writer must
        // truncate the torn tail so subsequent appends remain readable.
        using (var w = new FileAuditLogWriter(_root, channelNumber: 5))
        {
            w.OnTrade(Make(1, Day0Nanos));
            w.OnTrade(Make(2, Day0Nanos));
        }
        var path = Path.Combine(_root, "5", "fills-2026-05-18.log");
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
            fs.Write(new byte[AuditRecordCodec.RecordSize / 2], 0, AuditRecordCodec.RecordSize / 2);
        var truncatedLen = new FileInfo(path).Length;

        using (var w = new FileAuditLogWriter(_root, channelNumber: 5))
            w.OnTrade(Make(3, Day0Nanos));

        var finalLen = new FileInfo(path).Length;
        Assert.True(finalLen < truncatedLen + AuditRecordCodec.RecordSize,
            "torn tail should have been truncated before appending");
        var read = AuditLogReader.ReadAll(path).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2, 3 }, read);
    }

    [Fact]
    public void Writer_Reopen_RejectsHeaderWithChannelMismatch()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 6))
            w.OnTrade(Make(1, Day0Nanos));
        var path = Path.Combine(_root, "6", "fills-2026-05-18.log");
        // Move the file under a different channel directory to simulate a
        // operator misconfiguration; the writer must refuse to silently
        // append records with the wrong channel number.
        var otherDir = Path.Combine(_root, "60");
        Directory.CreateDirectory(otherDir);
        var moved = Path.Combine(otherDir, "fills-2026-05-18.log");
        File.Move(path, moved);
        var w2 = new FileAuditLogWriter(_root, channelNumber: 60);
        Assert.Throws<InvalidDataException>(() => w2.OnTrade(Make(2, Day0Nanos)));
        w2.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // 2026-05-18 00:00:00 UTC
    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private const ulong OneDayNanos = 86_400UL * 1_000_000_000UL;

    private static PostTradeRecord Make(uint id, ulong ts, long secId = 900_000_000_001L) => new(
        TradeId: id, TransactTimeNanos: ts, SecurityId: secId, AggressorSide: id % 2 == 0 ? Side.Buy : Side.Sell,
        Quantity: 100 + id, PriceMantissa: 10_0000L + id,
        BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
        BuyFirm: 7, SellFirm: 8,
        BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    [Fact]
    public void Writer_PersistsRecord_AndReaderRoundTrips()
    {
        var rec = Make(1, Day0Nanos);
        using (var w = new FileAuditLogWriter(_root, channelNumber: 42))
        {
            w.OnTrade(rec);
            Assert.Equal(1, w.RecordsWritten);
        }
        var path = Path.Combine(_root, "42", "fills-2026-05-18.log");
        Assert.True(File.Exists(path));
        var read = AuditLogReader.ReadAll(path).ToList();
        var only = Assert.Single(read);
        Assert.Equal(rec, only);
    }

    [Fact]
    public void Writer_DailyRollover_OpensNewFileAtUtcMidnight()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 7);
        w.OnTrade(Make(1, Day0Nanos));
        w.OnTrade(Make(2, Day0Nanos + 12UL * 3600UL * 1_000_000_000UL)); // same day
        w.OnTrade(Make(3, Day0Nanos + OneDayNanos));                     // next day
        w.OnTrade(Make(4, Day0Nanos + OneDayNanos + 1_000_000UL));       // same next day
        w.Dispose();

        var day0 = Path.Combine(_root, "7", "fills-2026-05-18.log");
        var day1 = Path.Combine(_root, "7", "fills-2026-05-19.log");
        Assert.True(File.Exists(day0));
        Assert.True(File.Exists(day1));
        var read0 = AuditLogReader.ReadAll(day0).Select(r => r.TradeId).ToList();
        var read1 = AuditLogReader.ReadAll(day1).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1, 2 }, read0);
        Assert.Equal(new uint[] { 3, 4 }, read1);
    }

    [Fact]
    public void Writer_Append_PreservesPreviousRecords_NoDuplicateHeader()
    {
        using (var w1 = new FileAuditLogWriter(_root, channelNumber: 1))
            w1.OnTrade(Make(10, Day0Nanos));
        using (var w2 = new FileAuditLogWriter(_root, channelNumber: 1))
            w2.OnTrade(Make(11, Day0Nanos));
        var path = Path.Combine(_root, "1", "fills-2026-05-18.log");
        var read = AuditLogReader.ReadAll(path).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 10, 11 }, read);
        // File size: 1 header + 2 records, NOT 2 headers + 2 records.
        var expected = AuditRecordCodec.FileHeaderSize + 2 * AuditRecordCodec.RecordSize;
        Assert.Equal(expected, new FileInfo(path).Length);
    }

    [Fact]
    public void Writer_ReaderStopsAtTornTail_NoException()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 2))
        {
            w.OnTrade(Make(1, Day0Nanos));
            w.OnTrade(Make(2, Day0Nanos));
        }
        var path = Path.Combine(_root, "2", "fills-2026-05-18.log");
        // Truncate mid-record (header + 1 record + half of the next).
        var len = AuditRecordCodec.FileHeaderSize + AuditRecordCodec.RecordSize + (AuditRecordCodec.RecordSize / 2);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            fs.SetLength(len);
        var read = AuditLogReader.ReadAll(path).Select(r => r.TradeId).ToList();
        Assert.Equal(new uint[] { 1 }, read);
    }

    [Fact]
    public void Writer_Checkpoint_FlushesToDisk_NoThrow()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 3);
        w.OnTrade(Make(1, Day0Nanos));
        w.Checkpoint();
        // No assertion on fsync (OS-dependent); we only verify the call is
        // legal in any state, including before the next OnTrade.
        w.Checkpoint();
    }

    /// <summary>Property-style round-trip: a deterministic pseudo-random
    /// sequence of records is written, the writer is disposed, the file
    /// is reopened, and the read-back sequence must equal the input
    /// exactly (issue #329 acceptance criterion).</summary>
    [Theory]
    [InlineData(1, 100)]
    [InlineData(42, 500)]
    [InlineData(2026, 1_000)]
    public void Writer_PropertyRoundTrip_RandomSequence_Equal(int seed, int count)
    {
        var rng = new Random(seed);
        var generated = new List<PostTradeRecord>(count);
        // Records spread over up to 3 UTC business days to exercise rollover.
        for (int i = 0; i < count; i++)
        {
            ulong dayOffset = (ulong)rng.Next(0, 3) * OneDayNanos;
            ulong intraDay = (ulong)rng.Next(0, 86_400) * 1_000_000_000UL;
            generated.Add(new PostTradeRecord(
                TradeId: (uint)(i + 1),
                TransactTimeNanos: Day0Nanos + dayOffset + intraDay,
                SecurityId: 900_000_000_000L + rng.Next(1, 20),
                AggressorSide: rng.Next(2) == 0 ? Side.Buy : Side.Sell,
                Quantity: rng.Next(1, 10_000),
                PriceMantissa: rng.Next(1, int.MaxValue),
                BuyClOrdId: (ulong)rng.NextInt64(),
                SellClOrdId: (ulong)rng.NextInt64(),
                BuyFirm: (uint)rng.Next(1, 256),
                SellFirm: (uint)rng.Next(1, 256),
                BuyOrderId: rng.NextInt64(),
                SellOrderId: rng.NextInt64()));
        }
        // Order chronologically so the writer's date-based rollover is monotonic.
        generated.Sort((a, b) => a.TransactTimeNanos.CompareTo(b.TransactTimeNanos));

        using (var w = new FileAuditLogWriter(_root, channelNumber: 99))
        {
            foreach (var r in generated)
                w.OnTrade(r);
        }

        var perDay = generated.GroupBy(r => DateOnly.FromDateTime(
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((long)(r.TransactTimeNanos / 100UL))));

        var readBack = new List<PostTradeRecord>();
        foreach (var g in perDay.OrderBy(g => g.Key))
        {
            var path = Path.Combine(_root, "99", $"fills-{g.Key:yyyy-MM-dd}.log");
            readBack.AddRange(AuditLogReader.ReadAll(path));
        }
        Assert.Equal(generated, readBack);
    }
}
