using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>
/// Issue #329 PR-4: durability watermark contract on
/// <see cref="FileAuditLogWriter"/>. The watermark gates WAL truncation
/// in <c>ChannelDispatcher</c>; these tests pin the writer-side semantics.
/// </summary>
public class FileAuditLogWriterWatermarkTests : IDisposable
{
    private readonly string _root;

    public FileAuditLogWriterWatermarkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeWmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private const ulong OneDayNanos = 86_400UL * 1_000_000_000UL;

    private static PostTradeRecord Make(uint id, ulong ts) => new(
        TradeId: id, TransactTimeNanos: ts, SecurityId: 900_000_000_001L,
        AggressorSide: id % 2 == 0 ? Side.Buy : Side.Sell,
        Quantity: 100 + id, PriceMantissa: 10_0000L + id,
        BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
        BuyFirm: 7, SellFirm: 8,
        BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    [Fact]
    public void DurableThroughCommandSeq_StartsAtZero()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        Assert.Equal(0, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void Checkpoint_WithoutBoundary_DurableStaysAtZero()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnTrade(Make(1, Day0Nanos));
        w.Checkpoint();
        Assert.Equal(0, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void OnCommandBoundary_AloneDoesNotAdvanceDurable()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnTrade(Make(1, Day0Nanos));
        w.OnCommandBoundary(42);
        // The boundary marks pending — only Checkpoint() promotes it.
        Assert.Equal(0, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void Checkpoint_AfterBoundary_AdvancesDurableToBoundarySeq()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnTrade(Make(1, Day0Nanos));
        w.OnCommandBoundary(42);
        w.Checkpoint();
        Assert.Equal(42, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void OnCommandBoundary_IsMonotonic_LowerSeqIgnored()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnCommandBoundary(50);
        w.OnCommandBoundary(40); // stale (replay-style); must not regress
        w.Checkpoint();
        Assert.Equal(50, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void Checkpoint_OnEmptyWriter_DoesNotThrow_AndDurableAdvances()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnCommandBoundary(7);
        w.Checkpoint(); // no records yet — both streams may be null
        Assert.Equal(7, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void Checkpoint_PreservesWatermark_AcrossDailyRotation()
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnTrade(Make(1, Day0Nanos));
        w.OnCommandBoundary(10);
        w.Checkpoint();
        Assert.Equal(10, w.DurableThroughCommandSeq);

        // Crossing the UTC day boundary rotates files internally — the
        // watermark belongs to the writer instance, not the file, and
        // must survive rotation.
        w.OnTrade(Make(2, Day0Nanos + OneDayNanos));
        w.OnCommandBoundary(20);
        w.Checkpoint();
        Assert.Equal(20, w.DurableThroughCommandSeq);
    }

    [Fact]
    public void NullPostTradeSink_DurableThroughCommandSeq_IsMaxValue()
    {
        // Contract: a dispatcher with audit disabled (default) must never
        // be gated by the watermark — the no-op sink reports +∞.
        IPostTradeSink sink = NullPostTradeSink.Instance;
        Assert.Equal(long.MaxValue, sink.DurableThroughCommandSeq);
        sink.OnCommandBoundary(123);
        sink.Checkpoint();
        Assert.Equal(long.MaxValue, sink.DurableThroughCommandSeq);
    }

    [Fact]
    public void Checkpoint_FromAnotherThread_IsSafeAgainstOnTrade()
    {
        // The async-snapshot-writer path calls Checkpoint from its writer
        // thread while OnTrade keeps running on the dispatch thread.
        // The internal lock must keep both honest. We don't assert
        // performance here — just that no exception or corruption arises
        // and the watermark advances at least to the last observed boundary.
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        var stop = false;
        var producer = new System.Threading.Thread(() =>
        {
            for (uint i = 1; i <= 200 && !stop; i++)
            {
                w.OnTrade(Make(i, Day0Nanos));
                w.OnCommandBoundary(i);
                System.Threading.Thread.Yield();
            }
        });
        producer.Start();
        for (int i = 0; i < 20; i++)
        {
            w.Checkpoint();
            System.Threading.Thread.Sleep(1);
        }
        stop = true;
        producer.Join(TimeSpan.FromSeconds(5));
        w.Checkpoint();
        Assert.True(w.DurableThroughCommandSeq > 0);
        Assert.True(w.DurableThroughCommandSeq <= 200);
    }

    [Fact]
    public void WriteFault_FreezesWatermark_AndCheckpointThrows()
    {
        // Issue #329 PR-4 (HIGH review finding): once an OnTrade fails the
        // writer marks itself "broken" — OnCommandBoundary must not advance
        // _pendingCommandSeq, Checkpoint must throw (so the WAL gate stays
        // closed), and DurableThroughCommandSeq must never advance past
        // the failed command's seq.
        using var w = new FileAuditLogWriter(_root, channelNumber: 1);
        w.OnTrade(Make(1, Day0Nanos));
        w.OnCommandBoundary(1);
        w.Checkpoint();
        Assert.Equal(1, w.DurableThroughCommandSeq);

        w.ForceWriteFaultForTests();
        Assert.True(w.WriteFault);

        // Subsequent boundary tries to bump pending → must be ignored.
        w.OnCommandBoundary(42);
        // Subsequent OnTrade must silently drop (so dispatcher can keep
        // running) — pinning the contract that the audit log already has
        // a known hole and further writes only deepen it.
        w.OnTrade(Make(2, Day0Nanos));
        // Checkpoint must throw — the WAL gate translates that to a
        // deferred truncation and bumps exch_audit_wal_truncate_deferred_total.
        Assert.Throws<IOException>(() => w.Checkpoint());
        // Watermark must NOT have advanced past the last good command.
        Assert.Equal(1, w.DurableThroughCommandSeq);
    }
}
