using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>
/// Issue #380 / ADR 0010: the orchestrator owns bust validation, dedup,
/// audit writes, routing and amendments. These tests exercise each
/// outcome branch in isolation so a future dispatcher refactor cannot
/// silently regress the boundary.
/// </summary>
public class PostTradeOrchestratorTests : IDisposable
{
    private readonly string _auditRoot;
    private readonly string _dropRoot;

    public PostTradeOrchestratorTests()
    {
        _auditRoot = Path.Combine(Path.GetTempPath(), "OrchTests_audit_" + Guid.NewGuid().ToString("N"));
        _dropRoot = Path.Combine(Path.GetTempPath(), "OrchTests_drop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_auditRoot);
        Directory.CreateDirectory(_dropRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_auditRoot)) Directory.Delete(_auditRoot, recursive: true); } catch { }
        try { if (Directory.Exists(_dropRoot)) Directory.Delete(_dropRoot, recursive: true); } catch { }
    }

    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private static readonly DateOnly Day0 = new(2026, 5, 18);
    private const long SecId = 900_000_000_777L;
    private const byte Channel = 1;

    private static PostTradeRecord Fill(uint id) => new(
        TradeId: id, TransactTimeNanos: Day0Nanos, SecurityId: SecId, AggressorSide: Side.Buy,
        Quantity: 100, PriceMantissa: 10_0000L,
        BuyClOrdId: 1, SellClOrdId: 2, BuyFirm: 10, SellFirm: 20, BuyOrderId: 5, SellOrderId: 6);

    private void SeedFill(uint tradeId)
    {
        using var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel);
        w.OnTrade(Fill(tradeId));
    }

    private void TouchDoneSidecar(DateOnly tradeDate)
    {
        var dir = Path.Combine(_dropRoot,
            Channel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fills.csv.done"), "");
    }

    private static BustRequest Req(uint tradeId, ulong corr, long? echo = null) =>
        new(tradeId, Day0, corr, echo, ReasonCode: 1, BusterFirm: 99, AttemptTransactTimeNanos: Day0Nanos + 1);

    private sealed class RecordingSink : IPostTradeSink
    {
        public List<(BustRecord Bust, DateOnly Date)> Busts { get; } = new();
        public List<RejectAttemptRecord> Rejects { get; } = new();
        public void OnTrade(in PostTradeRecord record) { }
        public void OnBust(in BustRecord bust, DateOnly tradeDate) => Busts.Add((bust, tradeDate));
        public void OnRejectAttempt(in RejectAttemptRecord reject) => Rejects.Add(reject);
        public void OnCommandBoundary(long durableThroughCommandSeq) { }
        public void Checkpoint() { }
        public long DurableThroughCommandSeq => 0;
    }

    private sealed class RecordingAmendments : IAmendmentsPublisher
    {
        public List<(string AuditRoot, string DropRoot, byte Channel, DateOnly Date)> Calls { get; } = new();
        public bool ThrowNext;
        public void Publish(string auditRootDir, string dropRootDir, byte channel, DateOnly tradeDate, DateTime utcNow)
        {
            Calls.Add((auditRootDir, dropRootDir, channel, tradeDate));
            if (ThrowNext) { ThrowNext = false; throw new InvalidOperationException("simulated publish failure"); }
        }
    }

    [Fact]
    public void Accept_PreEod_RoutesToTradeDate_AndDoesNotPublishAmendments()
    {
        SeedFill(100u);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var amendments = new RecordingAmendments();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendments);

        var outcome = orch.ProcessBust(Req(100u, corr: 5UL, echo: SecId), Channel, tradeDateDaysSinceEpoch: 1234);

        Assert.Equal(BustValidationKind.Accept, outcome.Kind);
        Assert.Equal(SecId, outcome.MatchedFill.SecurityId);
        Assert.Single(sink.Busts);
        Assert.Equal(Day0, sink.Busts[0].Date);
        Assert.Empty(amendments.Calls);
        Assert.True(dedup.TryGet(100u, out _));
    }

    [Fact]
    public void Accept_PostEod_RoutesToToday_AndPublishesAmendments()
    {
        SeedFill(101u);
        TouchDoneSidecar(Day0);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var amendments = new RecordingAmendments();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendments);

        var outcome = orch.ProcessBust(Req(101u, corr: 7UL, echo: SecId), Channel, tradeDateDaysSinceEpoch: 1234);

        Assert.Equal(BustValidationKind.Accept, outcome.Kind);
        Assert.Single(sink.Busts);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), sink.Busts[0].Date);
        Assert.Single(amendments.Calls);
        Assert.Equal(Day0, amendments.Calls[0].Date);
    }

    [Fact]
    public void Accept_PostEod_AmendmentsFailure_IsSwallowed_StatePersists()
    {
        SeedFill(102u);
        TouchDoneSidecar(Day0);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var amendments = new RecordingAmendments { ThrowNext = true };
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendments);

        var outcome = orch.ProcessBust(Req(102u, corr: 9UL, echo: SecId), Channel, tradeDateDaysSinceEpoch: 1234);

        Assert.Equal(BustValidationKind.Accept, outcome.Kind);
        Assert.Single(sink.Busts);
        Assert.True(dedup.TryGet(102u, out _));
    }

    [Fact]
    public void IdempotentReplay_PreEod_DoesNotRepublishAmendments()
    {
        SeedFill(103u);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var amendments = new RecordingAmendments();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendments);

        var first = orch.ProcessBust(Req(103u, corr: 11UL, echo: SecId), Channel, 1234);
        var second = orch.ProcessBust(Req(103u, corr: 11UL, echo: SecId), Channel, 1234);

        Assert.Equal(BustValidationKind.Accept, first.Kind);
        Assert.Equal(BustValidationKind.IdempotentReplay, second.Kind);
        Assert.Single(sink.Busts); // first accept only
        Assert.Empty(amendments.Calls);
    }

    [Fact]
    public void IdempotentReplay_PostEod_RepublishesAmendments()
    {
        SeedFill(104u);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var amendments = new RecordingAmendments();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendments);

        // First accept while pre-EOD; second call after .done appears.
        orch.ProcessBust(Req(104u, corr: 13UL, echo: SecId), Channel, 1234);
        TouchDoneSidecar(Day0);
        var second = orch.ProcessBust(Req(104u, corr: 13UL, echo: SecId), Channel, 1234);

        Assert.Equal(BustValidationKind.IdempotentReplay, second.Kind);
        Assert.Single(amendments.Calls);
    }

    [Fact]
    public void UnknownTradeId_RejectsAndFiresRejectAttempt_NoAuditWrite()
    {
        SeedFill(105u);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendmentsPublisher: null);

        var outcome = orch.ProcessBust(Req(tradeId: 999u, corr: 15UL, echo: SecId), Channel, 4242);

        Assert.Equal(BustValidationKind.UnknownTradeId, outcome.Kind);
        Assert.Empty(sink.Busts);
        Assert.Single(sink.Rejects);
        Assert.Equal((ushort)1, sink.Rejects[0].RejectCode);
        Assert.Equal(4242, sink.Rejects[0].DeclaredTradeDateDays);
        Assert.False(dedup.TryGet(999u, out _));
    }

    [Fact]
    public void SecurityIdMismatch_FiresRejectAttemptWithCode3()
    {
        SeedFill(106u);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendmentsPublisher: null);

        var outcome = orch.ProcessBust(Req(106u, corr: 17UL, echo: SecId + 1), Channel, 4242);

        Assert.Equal(BustValidationKind.SecurityIdMismatch, outcome.Kind);
        Assert.Single(sink.Rejects);
        Assert.Equal((ushort)3, sink.Rejects[0].RejectCode);
    }

    [Fact]
    public void AlreadyBustedDifferentCorrelation_FiresRejectAttemptWithCode2()
    {
        SeedFill(107u);
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendmentsPublisher: null);

        orch.ProcessBust(Req(107u, corr: 19UL, echo: SecId), Channel, 1234);
        var second = orch.ProcessBust(Req(107u, corr: 20UL, echo: SecId), Channel, 1234);

        Assert.Equal(BustValidationKind.AlreadyBustedDifferentCorrelation, second.Kind);
        Assert.Equal(19UL, second.ExistingCorrelationId);
        Assert.Single(sink.Rejects);
        Assert.Equal((ushort)2, sink.Rejects[0].RejectCode);
    }

    [Fact]
    public void MissingDay_NoAuditWriteAndNoRejectAttempt()
    {
        // Audit root exists but no fills-{date}.log seeded.
        var sink = new RecordingSink();
        var dedup = new BustDedupIndex();
        var orch = new PostTradeOrchestrator(sink, dedup, _auditRoot, _dropRoot, amendmentsPublisher: null);

        var outcome = orch.ProcessBust(Req(200u, corr: 21UL, echo: SecId), Channel, 1234);

        Assert.Equal(BustValidationKind.MissingDay, outcome.Kind);
        Assert.Empty(sink.Busts);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void RoutingLock_IsStableAndIndependentBetweenInstances()
    {
        var a = new PostTradeOrchestrator(new RecordingSink(), new BustDedupIndex(), _auditRoot);
        var b = new PostTradeOrchestrator(new RecordingSink(), new BustDedupIndex(), _auditRoot);

        Assert.Same(a.RoutingLock, a.RoutingLock);
        Assert.NotSame(a.RoutingLock, b.RoutingLock);
    }
}
