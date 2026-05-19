using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Property-based round-trip tests for <see cref="MatchingEngine.CaptureState"/>
/// + <see cref="MatchingEngine.RestoreState"/> (issue #273).
///
/// Drives the engine with FsCheck-generated sequences of
/// <see cref="NewOrderCommand"/> / <see cref="CancelOrderCommand"/> /
/// <see cref="ReplaceOrderCommand"/> and asserts two invariants:
///
/// 1. <c>capture → restore → capture</c> is the identity on
///    <see cref="EngineStateSnapshot"/> (structural equality of every
///    captured field — counters, phases, books in FIFO order, stops,
///    halts).
/// 2. <c>apply(prefix) → capture → restore → apply(suffix)</c> yields
///    the same snapshot as <c>apply(prefix ++ suffix)</c> on a fresh
///    engine, so a restart at any point is observationally indistinguishable
///    from never having restarted.
///
/// Generator surface is deliberately narrow (single instrument, Limit
/// orders only, lot-aligned, tick-aligned, no auctions, no halts) to
/// keep shrinking effective and to keep validation rejections rare —
/// rejections do not break the invariants but waste generator effort.
/// CI uses bounded test counts (<c>MaxTest = 100</c>) per property.
///
/// Extending the generators: see
/// <c>docs/PERSISTENCE-PROPERTY-TESTS.md</c>.
/// </summary>
public class CaptureRestorePropertyTests
{
    private const long Sec = 900_000_000_001L;
    private const long TickMantissa = 100; // 0.01 → mantissa 100
    private const long LotSize = 100;
    private const long MinPriceTicks = 100;   // 1.00
    private const long MaxPriceTicks = 10000; // 100.00

    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Sec,
        TickSize = 0.01m,
        LotSize = (int)LotSize,
        MinPrice = 0.01m,
        MaxPrice = 1_000.00m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private sealed class NullSink : IMatchingEventSink
    {
        public void OnOrderAccepted(in OrderAcceptedEvent e) { }
        public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e) { }
        public void OnOrderModified(in OrderModifiedEvent e) { }
        public void OnOrderCanceled(in OrderCanceledEvent e) { }
        public void OnOrderFilled(in OrderFilledEvent e) { }
        public void OnTrade(in TradeEvent e) { }
        public void OnReject(in RejectEvent e) { }
        public void OnOrderMassCanceled(in OrderMassCanceledEvent e) { }
        public void OnOrderBookSideEmpty(in OrderBookSideEmptyEvent e) { }
        public void OnTradingPhaseChanged(in TradingPhaseChangedEvent e) { }
        public void OnIcebergReplenished(in IcebergReplenishedEvent e) { }
        public void OnStopOrderAccepted(in StopOrderAcceptedEvent e) { }
        public void OnStopOrderTriggered(in StopOrderTriggeredEvent e) { }
        public void OnStopOrderCanceled(in StopOrderCanceledEvent e) { }
        public void OnAuctionTopChanged(in AuctionTopChangedEvent e) { }
        public void OnAuctionPrint(in AuctionPrintEvent e) { }
    }

    private static MatchingEngine NewEngine()
        => new(new[] { Petr4 }, new NullSink(), NullLogger<MatchingEngine>.Instance);

    // ---------------------------------------------------------------
    // Generated command surface (abstract DU for shrinking).
    // The interpreter (Apply) materialises a real engine command from
    // each variant; Cancel/Replace are resolved against the engine's
    // current resting orders so that arbitrary sequences are mostly
    // valid rather than mostly no-ops.
    // ---------------------------------------------------------------
    public abstract record TestCmd;
    public sealed record NewLimit(bool IsBuy, int PriceTicks, int LotMultiple, bool Ioc) : TestCmd;
    public sealed record NewIceberg(bool IsBuy, int PriceTicks, int LotMultiple, int VisibleLotMultiple) : TestCmd;
    public sealed record CancelNth(int Index) : TestCmd;
    public sealed record ReplaceNth(int Index, int NewPriceTicks, int NewLotMultiple) : TestCmd;

    public static class Arbs
    {
        private static Gen<TestCmd> NewLimitGen =>
            from isBuy in ArbMap.Default.GeneratorFor<bool>()
            from priceTicks in Gen.Choose((int)MinPriceTicks, (int)MaxPriceTicks)
            from lots in Gen.Choose(1, 10)
            from ioc in ArbMap.Default.GeneratorFor<bool>()
            select (TestCmd)new NewLimit(isBuy, priceTicks, lots, ioc);

        // Iceberg requires Day/GTC TIF (engine rejects IOC/FOK +
        // MaxFloor — see Commands.cs MaxFloor doc) and MaxFloor strictly
        // less than total quantity to actually exercise the hidden
        // reserve (MaxFloor == Quantity is a no-op degenerate iceberg).
        private static Gen<TestCmd> NewIcebergGen =>
            from isBuy in ArbMap.Default.GeneratorFor<bool>()
            from priceTicks in Gen.Choose((int)MinPriceTicks, (int)MaxPriceTicks)
            from totalLots in Gen.Choose(2, 10)
            from visibleLots in Gen.Choose(1, totalLots - 1)
            select (TestCmd)new NewIceberg(isBuy, priceTicks, totalLots, visibleLots);

        private static Gen<TestCmd> CancelGen =>
            from idx in Gen.Choose(0, 32)
            select (TestCmd)new CancelNth(idx);

        private static Gen<TestCmd> ReplaceGen =>
            from idx in Gen.Choose(0, 32)
            from priceTicks in Gen.Choose((int)MinPriceTicks, (int)MaxPriceTicks)
            from lots in Gen.Choose(1, 10)
            select (TestCmd)new ReplaceNth(idx, priceTicks, lots);

        // Element-level shrinker. FsCheck's default array shrinker
        // (used for TestCmd[]) removes elements first — that alone
        // gives meaningful sequence minimization — but to also
        // minimize the *values* inside each command we provide a
        // per-element shrinker that walks numeric fields toward their
        // minimum legal value and collapses NewIceberg → NewLimit.
        // Without this the test would still find counter-examples but
        // could not minimize them, defeating the point of property
        // testing.
        private static IEnumerable<TestCmd> Shrink(TestCmd cmd)
        {
            switch (cmd)
            {
                case NewLimit nl:
                    if (nl.LotMultiple > 1) yield return nl with { LotMultiple = nl.LotMultiple - 1 };
                    if (nl.PriceTicks > MinPriceTicks) yield return nl with { PriceTicks = (int)MinPriceTicks };
                    if (nl.Ioc) yield return nl with { Ioc = false };
                    break;
                case NewIceberg ni:
                    // Shrink toward a plain Day NewLimit (drops the
                    // iceberg fields entirely so the counter-example
                    // shows whether iceberg state is required).
                    yield return new NewLimit(ni.IsBuy, ni.PriceTicks, ni.LotMultiple, Ioc: false);
                    if (ni.LotMultiple > 2) yield return ni with { LotMultiple = ni.LotMultiple - 1, VisibleLotMultiple = Math.Min(ni.VisibleLotMultiple, ni.LotMultiple - 2) };
                    if (ni.VisibleLotMultiple > 1) yield return ni with { VisibleLotMultiple = ni.VisibleLotMultiple - 1 };
                    if (ni.PriceTicks > MinPriceTicks) yield return ni with { PriceTicks = (int)MinPriceTicks };
                    break;
                case CancelNth c:
                    if (c.Index > 0) yield return c with { Index = 0 };
                    break;
                case ReplaceNth r:
                    if (r.Index > 0) yield return r with { Index = 0 };
                    if (r.NewLotMultiple > 1) yield return r with { NewLotMultiple = r.NewLotMultiple - 1 };
                    if (r.NewPriceTicks > MinPriceTicks) yield return r with { NewPriceTicks = (int)MinPriceTicks };
                    break;
            }
        }

        // 8:1:1:1 weighting keeps the book populated; pure-cancel and
        // pure-replace sequences leave nothing to assert on. Iceberg
        // is rarer because it adds non-trivial validation overhead but
        // is essential for exercising the HiddenQuantity / MaxFloor
        // round-trip path through the snapshot.
        public static Arbitrary<TestCmd> Cmd() =>
            Arb.From(
                Gen.Frequency<TestCmd>(
                    (7, NewLimitGen),
                    (1, NewIcebergGen),
                    (1, CancelGen),
                    (1, ReplaceGen)),
                Shrink);
    }

    // ---------------------------------------------------------------
    // Engine driver: applies a command, swallowing structurally
    // invalid combinations (e.g. CancelNth(5) on a book with 2 orders).
    // The engine emits a Reject internally; we treat that as a no-op
    // because the round-trip invariants must hold regardless of
    // whether individual commands were accepted.
    // ---------------------------------------------------------------
    private static void Apply(MatchingEngine eng, TestCmd cmd, ref ulong clock, ref uint clOrd)
    {
        clock += 1_000UL;
        switch (cmd)
        {
            case NewLimit nl:
                eng.Submit(new NewOrderCommand(
                    ClOrdId: (++clOrd).ToString(),
                    SecurityId: Sec,
                    Side: nl.IsBuy ? Side.Buy : Side.Sell,
                    Type: OrderType.Limit,
                    Tif: nl.Ioc ? TimeInForce.IOC : TimeInForce.Day,
                    PriceMantissa: nl.PriceTicks * TickMantissa,
                    Quantity: nl.LotMultiple * LotSize,
                    EnteringFirm: 100,
                    EnteredAtNanos: clock));
                break;
            case NewIceberg ni:
                eng.Submit(new NewOrderCommand(
                    ClOrdId: (++clOrd).ToString(),
                    SecurityId: Sec,
                    Side: ni.IsBuy ? Side.Buy : Side.Sell,
                    Type: OrderType.Limit,
                    Tif: TimeInForce.Day,
                    PriceMantissa: ni.PriceTicks * TickMantissa,
                    Quantity: ni.LotMultiple * LotSize,
                    EnteringFirm: 100,
                    EnteredAtNanos: clock)
                { MaxFloor = (ulong)(ni.VisibleLotMultiple * LotSize) });
                break;
            case CancelNth c:
                {
                    var resting = ResolveResting(eng, c.Index);
                    if (resting is null) return;
                    eng.Cancel(new CancelOrderCommand(
                        ClOrdId: (++clOrd).ToString(),
                        SecurityId: Sec,
                        OrderId: resting.Value,
                        EnteredAtNanos: clock));
                }
                break;
            case ReplaceNth r:
                {
                    var resting = ResolveResting(eng, r.Index);
                    if (resting is null) return;
                    eng.Replace(new ReplaceOrderCommand(
                        ClOrdId: (++clOrd).ToString(),
                        SecurityId: Sec,
                        OrderId: resting.Value,
                        NewPriceMantissa: r.NewPriceTicks * TickMantissa,
                        NewQuantity: r.NewLotMultiple * LotSize,
                        EnteredAtNanos: clock));
                }
                break;
        }
    }

    private static long? ResolveResting(MatchingEngine eng, int idx)
    {
        var all = eng.EnumerateBook(Sec, Side.Buy)
            .Concat(eng.EnumerateBook(Sec, Side.Sell))
            .ToList();
        if (all.Count == 0) return null;
        return all[idx % all.Count].OrderId;
    }

    private static void Drive(MatchingEngine eng, IEnumerable<TestCmd> cmds)
    {
        ulong clock = 1_000_000UL;
        uint clOrd = 0;
        foreach (var c in cmds) Apply(eng, c, ref clock, ref clOrd);
    }

    // ---------------------------------------------------------------
    // Snapshot equality: EngineStateSnapshot is a record but uses
    // sequence-typed lists, whose default record equality is reference-
    // based. Compare structurally.
    // ---------------------------------------------------------------
    private static bool SnapshotsEqual(EngineStateSnapshot a, EngineStateSnapshot b)
    {
        if (a.NextOrderId != b.NextOrderId) return false;
        if (a.NextTradeId != b.NextTradeId) return false;
        if (a.RptSeq != b.RptSeq) return false;
        if (!SeqEqual(a.Phases, b.Phases, (x, y) => x.SecurityId == y.SecurityId && x.Phase == y.Phase))
            return false;
        if (!SeqEqual(a.Books, b.Books, BookEqual)) return false;
        if (!NullableSeqEqual(a.Stops, b.Stops, StopEqual)) return false;
        if (!NullableSeqEqual(a.Halts, b.Halts,
            (x, y) => x.SecurityId == y.SecurityId && x.Reason == y.Reason
                      && x.HaltedAtNanos == y.HaltedAtNanos && x.Note == y.Note))
            return false;
        return true;
    }

    private static bool BookEqual(EngineStateSnapshot.BookSnapshot x, EngineStateSnapshot.BookSnapshot y)
        => x.SecurityId == y.SecurityId && SeqEqual(x.Orders, y.Orders, OrderEqual);

    private static bool OrderEqual(RestingOrderRecord x, RestingOrderRecord y)
        => x.OrderId == y.OrderId && x.ClOrdId == y.ClOrdId && x.Side == y.Side
           && x.PriceMantissa == y.PriceMantissa && x.RemainingQuantity == y.RemainingQuantity
           && x.EnteringFirm == y.EnteringFirm && x.InsertTimestampNanos == y.InsertTimestampNanos
           && x.Tif == y.Tif && x.MaxFloor == y.MaxFloor && x.HiddenQuantity == y.HiddenQuantity;

    private static bool StopEqual(RestingStopRecord x, RestingStopRecord y)
        => x.OrderId == y.OrderId && x.ClOrdId == y.ClOrdId && x.SecurityId == y.SecurityId
           && x.Side == y.Side && x.StopType == y.StopType && x.Tif == y.Tif
           && x.StopPxMantissa == y.StopPxMantissa && x.LimitPriceMantissa == y.LimitPriceMantissa
           && x.Quantity == y.Quantity && x.EnteringFirm == y.EnteringFirm
           && x.EnteredAtNanos == y.EnteredAtNanos;

    private static bool SeqEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> eq)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!eq(a[i], b[i])) return false;
        return true;
    }

    private static bool NullableSeqEqual<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b, Func<T, T, bool> eq)
    {
        if (a is null && b is null) return true;
        if (a is null) return b!.Count == 0;
        if (b is null) return a.Count == 0;
        return SeqEqual(a, b, eq);
    }

    // ---------------------------------------------------------------
    // Properties.
    // ---------------------------------------------------------------

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public bool CaptureRestoreCapture_IsIdentity(TestCmd[] cmds)
    {
        var eng = NewEngine();
        Drive(eng, cmds);
        var s1 = eng.CaptureState();

        var dst = NewEngine();
        dst.RestoreState(s1);
        var s2 = dst.CaptureState();

        return SnapshotsEqual(s1, s2);
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public bool RestartAtAnyPoint_MatchesContinuousRun(TestCmd[] prefix, TestCmd[] suffix)
    {
        // Engine A: continuous run of prefix ++ suffix.
        var contA = NewEngine();
        Drive(contA, prefix);
        Drive(contA, suffix);
        var snapA = contA.CaptureState();

        // Engine B: prefix → capture → restore into fresh engine → suffix.
        var preB = NewEngine();
        Drive(preB, prefix);
        var midSnap = preB.CaptureState();
        var contB = NewEngine();
        contB.RestoreState(midSnap);
        Drive(contB, suffix);
        var snapB = contB.CaptureState();

        return SnapshotsEqual(snapA, snapB);
    }

    // ---------------------------------------------------------------
    // Historical-bug regression (issue #262, rediscovered via property
    // shrinking before #262 shipped: stop orders silently vanished
    // across a snapshot round-trip because they were not in the
    // snapshot at all). Kept as an explicit example so the
    // generator-coverage gap that originally let #262 escape is now
    // permanently closed by an explicit assertion. Pinning it as a
    // Fact (not a Property) so a regression here cannot hide behind
    // a low MaxTest count.
    // ---------------------------------------------------------------
    [Fact]
    public void Regression_262_StopOrdersSurviveSnapshot()
    {
        var src = NewEngine();
        // Untriggered buy-stop: priced above the (empty) book → parks.
        src.Submit(new NewOrderCommand(
            ClOrdId: "STOP-1",
            SecurityId: Sec,
            Side: Side.Buy,
            Type: OrderType.StopLimit,
            Tif: TimeInForce.Day,
            PriceMantissa: 50 * TickMantissa,
            Quantity: 1 * LotSize,
            EnteringFirm: 100,
            EnteredAtNanos: 1_000_000UL)
        { StopPxMantissa = 60 * TickMantissa });

        var snap = src.CaptureState();
        Assert.NotNull(snap.Stops);
        Assert.Single(snap.Stops!);

        var dst = NewEngine();
        dst.RestoreState(snap);
        var snap2 = dst.CaptureState();
        Assert.True(SnapshotsEqual(snap, snap2),
            "Stop order must survive a capture → restore → capture round-trip (issue #262).");
    }
}
