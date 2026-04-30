namespace B3.Exchange.SyntheticTrader.Tests;

/// <summary>
/// White-box tests that drive <see cref="SyntheticTraderRunner"/>'s ER
/// handlers directly via the internal <c>TestRaise*</c> seams (no TCP).
/// They cover two regression areas surfaced in the PR review:
///
///  - <see cref="OnExecCancel_WithMultipleLiveOrders_RemovesTargetedOnly"/>
///    exercises the cancel resolution path with multiple live entries on
///    the same strategy. The pre-fix code iterated <c>LiveByTag</c> with
///    <c>foreach</c> while removing the matched entry — fragile against
///    future refactors that move the unconditional <c>return</c> — and used
///    an O(N) scan. The fix routes via an OrderId index in O(1).
///  - <see cref="ByClOrd_Map_DoesNotLeak_OnTerminalEvents"/> verifies the
///    new cleanup of <c>_byClOrd</c> / <c>_byOrderId</c> on full-fill,
///    cancel, and reject. Without the fix, <c>_byClOrd</c> grew unbounded.
/// </summary>
public class RunnerHandlerTests
{
    private const long Sec = 900_000_000_001L;

    private sealed class StubStrategy : IStrategy
    {
        public string Name { get; }
        public List<string> Removed { get; } = new();
        public List<string> Acked { get; } = new();
        public StubStrategy(string name) { Name = name; }
        public IEnumerable<OrderIntent> Tick(in MarketState state, Random rng) => Array.Empty<OrderIntent>();
        public void OnAck(string clientTag, long orderId) => Acked.Add(clientTag);
        public void OnFill(in FillEvent fill) { }
        public void OnRemoved(string clientTag) => Removed.Add(clientTag);
    }

    private static (SyntheticTraderRunner runner, StubStrategy strategy) NewRunner()
    {
        var strategy = new StubStrategy("s");
        var instCfg = new InstrumentConfig
        {
            SecurityId = Sec,
            TickSize = 100,
            LotSize = 100,
            InitialMidMantissa = 100_0000,
            MidDriftProbability = 0.0,
        };
        var runner = new SyntheticTraderRunner(
            new[] { (instCfg, (IReadOnlyList<IStrategy>)new IStrategy[] { strategy }) },
            new Random(1),
            TimeSpan.FromMilliseconds(1));
        return (runner, strategy);
    }

    [Fact]
    public async Task OnExecCancel_WithMultipleLiveOrders_RemovesTargetedOnly()
    {
        var (runner, strategy) = NewRunner();
        await using var _ = runner;

        // Submit three orders, ack each one with a distinct engine OrderID.
        for (int i = 1; i <= 3; i++)
        {
            runner.TestSubmit(strategy, OrderIntent.NewLimit(Sec, OrderSide.Buy, 100, 99_9000 + i * 100, $"t{i}"));
        }
        runner.TestRaiseNew(new ExecReportNew(ClOrdId: 1, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1001));
        runner.TestRaiseNew(new ExecReportNew(ClOrdId: 2, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1002));
        runner.TestRaiseNew(new ExecReportNew(ClOrdId: 3, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1003));

        Assert.Equal(3, runner.ByClOrdCount);
        Assert.Equal(3, runner.ByOrderIdCount);

        // Cancel the middle one. ER_Cancel.ClOrdId carries the cancel
        // request's clord (some unrelated id), which is intentionally NOT
        // registered in _byClOrd; the runner must resolve via OrderId.
        runner.TestRaiseCancel(new ExecReportCancel(
            ClOrdId: 999, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1002));

        // Only "t2" was notified removed; the other two are still live.
        Assert.Equal(new[] { "t2" }, strategy.Removed);
        Assert.Equal(2, runner.ByClOrdCount);
        Assert.Equal(2, runner.ByOrderIdCount);
    }

    [Fact]
    public async Task OnExecCancel_FallsBackToOrderId_WhenClOrdIdUnknown()
    {
        // Sanity check: even if the cancel ER carried an unknown ClOrdId, the
        // OrderId-based resolution still finds the originating tracking. This
        // is the path the buggy foreach-Remove iteration was trying to handle.
        var (runner, strategy) = NewRunner();
        await using var _ = runner;
        runner.TestSubmit(strategy, OrderIntent.NewLimit(Sec, OrderSide.Buy, 100, 99_9000, "tag"));
        runner.TestRaiseNew(new ExecReportNew(ClOrdId: 1, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 5555));

        runner.TestRaiseCancel(new ExecReportCancel(
            ClOrdId: ulong.MaxValue, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 5555));

        Assert.Equal(new[] { "tag" }, strategy.Removed);
        Assert.Equal(0, runner.ByClOrdCount);
        Assert.Equal(0, runner.ByOrderIdCount);
    }

    [Fact]
    public async Task ByClOrd_Map_DoesNotLeak_OnTerminalEvents()
    {
        var (runner, strategy) = NewRunner();
        await using var _ = runner;

        // Send 6 orders. Drive each through a different terminal ER:
        //   - 1,2  full fill (Trade with LeavesQty == 0)
        //   - 3,4  cancel
        //   - 5,6  reject (rejected pre-ack: only _byClOrd needs cleanup)
        for (int i = 1; i <= 6; i++)
        {
            runner.TestSubmit(strategy, OrderIntent.NewLimit(Sec, OrderSide.Buy, 100, 99_9000 + i * 100, $"t{i}"));
        }
        Assert.Equal(6, runner.ByClOrdCount);

        // Ack 1..4 (so cancels can resolve via OrderId).
        for (int i = 1; i <= 4; i++)
        {
            runner.TestRaiseNew(new ExecReportNew(ClOrdId: (ulong)i, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1000 + i));
        }
        Assert.Equal(4, runner.ByOrderIdCount);

        // Full fill 1 and 2 — Trade ER's ClOrdId is the original order's clord.
        runner.TestRaiseTrade(new ExecReportTrade(
            ClOrdId: 1, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1001,
            LastQty: 100, LastPxMantissa: 99_9100, LeavesQty: 0, CumQty: 100));
        runner.TestRaiseTrade(new ExecReportTrade(
            ClOrdId: 2, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1002,
            LastQty: 100, LastPxMantissa: 99_9200, LeavesQty: 0, CumQty: 100));

        // Cancel 3 and 4.
        runner.TestRaiseCancel(new ExecReportCancel(ClOrdId: 8001, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1003));
        runner.TestRaiseCancel(new ExecReportCancel(ClOrdId: 8002, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 1004));

        // Reject 5 and 6 — pre-ack rejection.
        runner.TestRaiseReject(new ExecReportReject(ClOrdId: 5, SecurityId: Sec));
        runner.TestRaiseReject(new ExecReportReject(ClOrdId: 6, SecurityId: Sec));

        Assert.Equal(0, runner.ByClOrdCount);
        Assert.Equal(0, runner.ByOrderIdCount);
    }

    [Fact]
    public async Task PartialFill_KeepsTrackingEntries()
    {
        var (runner, strategy) = NewRunner();
        await using var _ = runner;

        runner.TestSubmit(strategy, OrderIntent.NewLimit(Sec, OrderSide.Buy, 200, 99_9000, "t"));
        runner.TestRaiseNew(new ExecReportNew(ClOrdId: 1, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 7000));

        // Partial fill: leaves 100 of 200.
        runner.TestRaiseTrade(new ExecReportTrade(
            ClOrdId: 1, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 7000,
            LastQty: 100, LastPxMantissa: 99_9000, LeavesQty: 100, CumQty: 100));

        Assert.Equal(1, runner.ByClOrdCount);
        Assert.Equal(1, runner.ByOrderIdCount);
        Assert.Empty(strategy.Removed);

        // Now fully fill the rest — entries must be cleaned up.
        runner.TestRaiseTrade(new ExecReportTrade(
            ClOrdId: 1, SecurityId: Sec, Side: OrderSide.Buy, OrderId: 7000,
            LastQty: 100, LastPxMantissa: 99_9000, LeavesQty: 0, CumQty: 200));

        Assert.Equal(0, runner.ByClOrdCount);
        Assert.Equal(0, runner.ByOrderIdCount);
    }
}
