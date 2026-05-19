using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>ADR 0008 PR-2 tests for <see cref="BustDedupIndex"/> rebuild
/// from on-disk audit files.</summary>
public class BustDedupIndexTests : IDisposable
{
    private readonly string _root;
    public BustDedupIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BustDedupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private static readonly DateOnly Day0 = new(2026, 5, 18);
    private const ulong OneDayNanos = 86_400UL * 1_000_000_000UL;

    private static PostTradeRecord Fill(uint id, ulong ts) => new(
        TradeId: id, TransactTimeNanos: ts, SecurityId: 1L, AggressorSide: Side.Buy,
        Quantity: 1, PriceMantissa: 1, BuyClOrdId: 1, SellClOrdId: 1,
        BuyFirm: 1, SellFirm: 2, BuyOrderId: 1, SellOrderId: 2);

    private static BustRecord Bust(uint tradeId, ulong correlationId) =>
        new(tradeId, Day0Nanos + 1, 1L, 0, 99, correlationId);

    [Fact]
    public void LoadFromAuditFiles_PicksUpBustsAcrossDaysWithinWindow()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 3))
        {
            w.OnTrade(Fill(1, Day0Nanos));
            w.OnBust(Bust(1, correlationId: 42UL), Day0);
            w.OnTrade(Fill(2, Day0Nanos + OneDayNanos));
            w.OnBust(Bust(2, correlationId: 43UL), Day0.AddDays(1));
        }

        var index = BustDedupIndex.LoadFromAuditFiles(_root, channel: 3, retentionDays: 0, todayUtc: Day0.AddDays(1));
        Assert.Equal(2, index.Count);
        Assert.True(index.TryGet(1, out var e1));
        Assert.Equal(42UL, e1.CorrelationId);
        Assert.Equal(Day0, e1.TradeDate);
        Assert.True(index.TryGet(2, out var e2));
        Assert.Equal(43UL, e2.CorrelationId);
        Assert.Equal(Day0.AddDays(1), e2.TradeDate);
    }

    [Fact]
    public void LoadFromAuditFiles_SkipsFilesOlderThanRetentionWindow()
    {
        using (var w = new FileAuditLogWriter(_root, channelNumber: 4))
        {
            w.OnTrade(Fill(1, Day0Nanos));
            w.OnBust(Bust(1, 100UL), Day0);
            w.OnTrade(Fill(2, Day0Nanos + OneDayNanos));
            w.OnBust(Bust(2, 200UL), Day0.AddDays(1));
        }
        // retentionDays = 1 + today = Day0+1 → window = [Day0+1, Day0+1]
        var index = BustDedupIndex.LoadFromAuditFiles(_root, channel: 4, retentionDays: 1, todayUtc: Day0.AddDays(1));
        Assert.Equal(1, index.Count);
        Assert.False(index.TryGet(1, out _));
        Assert.True(index.TryGet(2, out _));
    }

    [Fact]
    public void LoadFromAuditFiles_EmptyDirReturnsEmptyIndex()
    {
        var index = BustDedupIndex.LoadFromAuditFiles(_root, channel: 99, retentionDays: 0, todayUtc: Day0);
        Assert.Equal(0, index.Count);
    }
}
