using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using OrderType = B3.Exchange.Matching.OrderType;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #273: property-based round-trip tests for snapshot
/// capture / restore.
///
/// <para><b>Property under test:</b> for any sequence of state-mutating
/// commands applied to a fresh dispatcher, the snapshot captured
/// after applying them must equal — byte-for-byte through
/// <see cref="BinaryChannelStateSnapshotCodec.Encode"/> — the
/// snapshot recaptured after restoring that snapshot into a brand-new
/// dispatcher. In other words, capture/restore is a round-trip
/// identity:
/// <code>
/// encode(capture(restore(encode(capture(state))))) == encode(capture(state))
/// </code>
/// </para>
///
/// <para><b>Why bytewise equality?</b>
/// <see cref="ChannelStateSnapshot"/> is a record but its nested
/// <see cref="System.Collections.Generic.IReadOnlyList{T}"/> properties
/// fall back to reference equality, so the record's auto-generated
/// <c>Equals</c> is too lax. The binary codec is deterministic and
/// order-preserving so byte equality is the tightest possible
/// equivalence.</para>
///
/// <para><b>Why seeded <see cref="System.Random"/> instead of FsCheck?</b>
/// The acceptance criterion is "Property tests run in CI (bounded test
/// count)", which seeded <c>[Theory]</c> + a deterministic generator
/// satisfies without adding a NuGet dependency. The same generator
/// shape (Op-tagged record + biased pick + live-target tracking) is
/// already used by <see cref="WalReplayIdempotencyTests"/> for issue
/// #287, keeping the patterns consistent. Migrating to FsCheck for
/// shrinking is a future enhancement — see the "How to extend" notes
/// at the bottom of this file.</para>
///
/// <para>Each seed acts as a discovered property: when a future
/// regression breaks the round-trip, Add the failing seed to the
/// <c>InlineData</c> list and let CI carry it forward as a permanent
/// regression test. Seed <c>0xDEAD</c> + length <c>250</c> below
/// guards the historical bug fixed by PR #261 (snapshot encoding
/// dropped order-modification timestamps for replaced orders).</para>
/// </summary>
public class CaptureRestoreRoundTripPropertyTests
{
    private const long Sec = 900_000_000_002L;
    private const byte Channel = 84;

    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Sec,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private static long Px(decimal p) => (long)(p * 10_000m);

    private sealed class NoOpPacketSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private sealed class NoOpOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue) => true;
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c) => true;
    }

    private static SnapshotThrottlePolicy NeverAuto =>
        new() { EveryNCommands = int.MaxValue, MinIntervalMs = 0 };

    private static ChannelDispatcher BuildDispatcher()
    {
        return new ChannelDispatcher(
            channelNumber: Channel,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            packetSink: new NoOpPacketSink(),
            outbound: new NoOpOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance,
            metrics: null,
            persister: null,
            snapshotThrottle: NeverAuto,
            useAsyncSnapshotWriter: false,
            wal: null);
    }

    private enum Op { New, Cancel, Replace }

    private sealed record Cmd(Op Kind, SessionId Session, uint Firm, ulong ClOrdId,
        ulong OrigClOrdId, NewOrderCommand? New, CancelOrderCommand? Cancel,
        ReplaceOrderCommand? Replace);

    /// <summary>
    /// Deterministic command-sequence generator. Mirrors the
    /// generator in <see cref="WalReplayIdempotencyTests"/> so
    /// regressions caught here surface in the WAL-replay suite too.
    /// Buy/sell ranges are kept disjoint so resting orders never
    /// trade out from under a Cancel/Replace — round-trip is what
    /// is under test, not the matcher's crossing logic.
    /// </summary>
    private static List<Cmd> BuildSequence(int seed, int n)
    {
        var rng = new Random(seed);
        var cmds = new List<Cmd>(n);
        var live = new List<(SessionId Session, uint Firm, ulong ClOrdId, Side Side)>();
        ulong nextClOrd = 1;
        ulong nanos = 1_000UL;

        var sessions = new[] { new SessionId("11111"), new SessionId("22222") };
        var firms = new uint[] { 700, 701 };

        for (int i = 0; i < n; i++)
        {
            int pick = live.Count == 0 ? 0 : rng.Next(0, 6);
            Op op = pick switch
            {
                <= 3 => Op.New,
                4 => Op.Cancel,
                _ => Op.Replace,
            };
            nanos += (ulong)rng.Next(1, 5);

            if (op == Op.New)
            {
                var s = sessions[rng.Next(sessions.Length)];
                var f = firms[rng.Next(firms.Length)];
                var side = rng.Next(2) == 0 ? Side.Buy : Side.Sell;
                var price = side == Side.Buy
                    ? Px(9.95m + 0.01m * rng.Next(0, 4))
                    : Px(10.05m + 0.01m * rng.Next(0, 4));
                var qty = 100L * rng.Next(1, 4);
                var clOrd = nextClOrd++;
                var no = new NewOrderCommand(
                    ClOrdId: "CL-" + clOrd.ToString(),
                    SecurityId: Sec, Side: side,
                    Type: OrderType.Limit, Tif: TimeInForce.Day,
                    PriceMantissa: price, Quantity: qty,
                    EnteringFirm: f, EnteredAtNanos: nanos);
                cmds.Add(new Cmd(Op.New, s, f, clOrd, 0, no, null, null));
                live.Add((s, f, clOrd, side));
            }
            else if (op == Op.Cancel)
            {
                int idx = rng.Next(live.Count);
                var t = live[idx];
                live.RemoveAt(idx);
                var clOrd = nextClOrd++;
                var co = new CancelOrderCommand(
                    ClOrdId: "CL-" + clOrd.ToString(),
                    SecurityId: Sec, OrderId: 0, EnteredAtNanos: nanos);
                cmds.Add(new Cmd(Op.Cancel, t.Session, t.Firm, clOrd, t.ClOrdId,
                    null, co, null));
            }
            else
            {
                int idx = rng.Next(live.Count);
                var t = live[idx];
                var clOrd = nextClOrd++;
                var newPrice = t.Side == Side.Buy
                    ? Px(9.95m + 0.01m * rng.Next(0, 4))
                    : Px(10.05m + 0.01m * rng.Next(0, 4));
                var newQty = 100L * rng.Next(1, 4);
                var ro = new ReplaceOrderCommand(
                    ClOrdId: "CL-" + clOrd.ToString(),
                    SecurityId: Sec, OrderId: 0,
                    NewPriceMantissa: newPrice, NewQuantity: newQty,
                    EnteredAtNanos: nanos);
                cmds.Add(new Cmd(Op.Replace, t.Session, t.Firm, clOrd, t.ClOrdId,
                    null, null, ro));
                // Mirror dispatcher's (firm,clOrdId) rotation on Replace
                // so subsequent Cancel/Replace targets the new ClOrdId.
                live[idx] = (t.Session, t.Firm, clOrd, t.Side);
            }
        }
        return cmds;
    }

    private static void ApplyResolving(ChannelDispatcher disp,
        ChannelDispatcher.TestProbe probe, Cmd c)
    {
        switch (c.Kind)
        {
            case Op.New:
                Assert.True(disp.EnqueueNewOrder(c.New!, c.Session, c.Firm, c.ClOrdId));
                break;
            case Op.Cancel:
                if (!disp.TryResolveByClOrdId(c.Firm, c.OrigClOrdId, out var oid, out var sid))
                    break;
                Assert.True(disp.EnqueueCancel(c.Cancel! with { OrderId = oid, SecurityId = sid },
                    c.Session, c.Firm, c.ClOrdId, c.OrigClOrdId));
                break;
            case Op.Replace:
                if (!disp.TryResolveByClOrdId(c.Firm, c.OrigClOrdId, out var roid, out var rsid))
                    break;
                Assert.True(disp.EnqueueReplace(c.Replace! with { OrderId = roid, SecurityId = rsid },
                    c.Session, c.Firm, c.ClOrdId, c.OrigClOrdId));
                break;
        }
        probe.DrainInbound();
    }

    /// <summary>
    /// The core round-trip property, run across many seeds.
    /// </summary>
    [Theory]
    // Smoke seeds — short sequences that catch fundamental bugs fast.
    [InlineData(1, 25)]
    [InlineData(2, 25)]
    [InlineData(3, 25)]
    // Medium seeds — exercise interaction between live[] tracking and
    // Replace's ClOrdId rotation.
    [InlineData(42, 100)]
    [InlineData(101, 100)]
    [InlineData(202, 100)]
    [InlineData(303, 100)]
    [InlineData(404, 100)]
    // Longer seeds — accumulate state to catch encoder buffer-growth
    // bugs and large-book ordering.
    [InlineData(1001, 250)]
    [InlineData(1002, 250)]
    [InlineData(1003, 250)]
    // Regression seed for PR #261 (encoder dropped modify timestamps
    // for replaced orders). Keep this even if other seeds also catch
    // it — the InlineData label is the regression record.
    [InlineData(0xDEAD, 250)]
    public async Task CaptureRestoreRecapture_IsBytewiseIdentity(int seed, int n)
    {
        var cmds = BuildSequence(seed, n);

        // Phase 1: apply on a fresh dispatcher, capture snapshot.
        ChannelStateSnapshot snapshot;
        await using (var disp1 = BuildDispatcher())
        {
            var probe = disp1.CreateTestProbe();
            foreach (var c in cmds) ApplyResolving(disp1, probe, c);
            snapshot = disp1.CaptureChannelState();
        }

        var encodedOriginal = BinaryChannelStateSnapshotCodec.Encode(snapshot);

        // Phase 2: encode → decode round-trip on the wire format alone.
        var decoded = BinaryChannelStateSnapshotCodec.Decode(encodedOriginal);
        var encodedDecoded = BinaryChannelStateSnapshotCodec.Encode(decoded);
        Assert.Equal(encodedOriginal, encodedDecoded);

        // Phase 3: restore into a brand-new dispatcher, recapture,
        // assert bytewise identity. This is the property we ultimately
        // care about — boot recovery must converge.
        byte[] encodedRecaptured;
        await using (var disp2 = BuildDispatcher())
        {
            disp2.RestoreChannelState(decoded);
            var recaptured = disp2.CaptureChannelState();
            encodedRecaptured = BinaryChannelStateSnapshotCodec.Encode(recaptured);
        }

        Assert.Equal(encodedOriginal, encodedRecaptured);
    }

    /// <summary>
    /// Two-generation chained restore: the snapshot produced from
    /// Phase 3 must again round-trip. Catches degenerate cases where
    /// the first restore "fixes" a normalisation that the second
    /// restore would diverge on (encoder/decoder asymmetry).
    /// </summary>
    [Theory]
    [InlineData(7, 60)]
    [InlineData(77, 120)]
    [InlineData(777, 200)]
    public async Task TwoGenerationRestore_StaysStable(int seed, int n)
    {
        var cmds = BuildSequence(seed, n);

        ChannelStateSnapshot gen0;
        await using (var disp = BuildDispatcher())
        {
            var probe = disp.CreateTestProbe();
            foreach (var c in cmds) ApplyResolving(disp, probe, c);
            gen0 = disp.CaptureChannelState();
        }

        var encGen0 = BinaryChannelStateSnapshotCodec.Encode(gen0);

        ChannelStateSnapshot gen1;
        await using (var disp = BuildDispatcher())
        {
            disp.RestoreChannelState(BinaryChannelStateSnapshotCodec.Decode(encGen0));
            gen1 = disp.CaptureChannelState();
        }
        var encGen1 = BinaryChannelStateSnapshotCodec.Encode(gen1);

        ChannelStateSnapshot gen2;
        await using (var disp = BuildDispatcher())
        {
            disp.RestoreChannelState(BinaryChannelStateSnapshotCodec.Decode(encGen1));
            gen2 = disp.CaptureChannelState();
        }
        var encGen2 = BinaryChannelStateSnapshotCodec.Encode(gen2);

        Assert.Equal(encGen0, encGen1);
        Assert.Equal(encGen1, encGen2);
    }

    // === How to extend ==================================================
    //
    // 1. **Add a new seed:** when a future regression surfaces, copy the
    //    failing (seed, n) into the [InlineData] list of
    //    CaptureRestoreRecapture_IsBytewiseIdentity. The seed becomes
    //    the permanent regression record — git blame on the InlineData
    //    line points at the fixing commit.
    //
    // 2. **Add a new operation:** extend the Op enum + Cmd record with the
    //    new command kind, pick a probability bucket in the rng.Next(0,6)
    //    switch, and emit it from BuildSequence. Mirror the addition into
    //    WalReplayIdempotencyTests so the determinism + round-trip
    //    properties stay aligned.
    //
    // 3. **Add a new property:** clone CaptureRestoreRecapture_*, change
    //    the assertion. Examples worth adding when the relevant feature
    //    lands:
    //      - "captured.LastAppliedSeq strictly increases per accepted command"
    //      - "encoded snapshot size is bounded by O(liveOrders + tradeHistory)"
    //      - "restoring a snapshot of channel A into a dispatcher
    //         configured for channel B throws" (already covered by
    //         RestoreChannelState invariant checks)
    //
    // 4. **Migrate to FsCheck (future):** the generator shape here maps
    //    1:1 to FsCheck Arbitrary<Cmd>; replace BuildSequence with a
    //    Gen<List<Cmd>>, mark the test with [Property] from FsCheck.Xunit,
    //    and benefit from automatic shrinking. Add `FsCheck.Xunit`
    //    package to Directory.Packages.props pinned to a 3.x version
    //    compatible with xunit 2.9.x.
}
