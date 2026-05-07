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
/// Property-style determinism test for issue #287.
///
/// <para>Invariant under test: starting from a snapshot at sequence
/// <c>K</c> and replaying the WAL tail <c>(K, N]</c> must yield byte-for-byte
/// the same channel state as applying commands <c>[1..N]</c> from a clean
/// engine. This is the safety net behind the snapshot-fallback recovery
/// path: if a fresher snapshot fails to land (or is found corrupt at boot),
/// the dispatcher loads an older snapshot and replays the WAL — that path
/// must be guaranteed convergent for any (snapshot, WAL-tail) split.</para>
///
/// <para>Strategy (per seed):</para>
/// <list type="number">
///   <item>Build a deterministic command sequence of <c>N</c> mixed
///         NewOrder / Cancel / Replace items from a seeded RNG.</item>
///   <item>Run "baseline": apply all <c>N</c> commands with WAL+persister
///         and an effectively-disabled snapshot throttle, capture the
///         on-disk WAL records, then force a final persist. The persisted
///         <see cref="ChannelStateSnapshot"/> is the reference final state
///         and the WAL records are the canonical command stream.</item>
///   <item>For several split points <c>K</c>, build a snapshot at <c>K</c>
///         (apply [1..K] then force-persist) and stage a fresh WAL file
///         containing only the records with <c>Seq &gt; K</c>. Boot a
///         brand-new dispatcher pointed at that snapshot+WAL pair, let
///         it run its replay, then force a persist and compare the
///         encoded snapshot bytes to the baseline.</item>
/// </list>
///
/// <para>Comparison uses
/// <see cref="BinaryChannelStateSnapshotCodec.Encode"/> because
/// <see cref="ChannelStateSnapshot"/> is a record but its nested
/// <see cref="IReadOnlyList{T}"/> properties fall back to reference
/// equality. The binary codec is deterministic and order-preserving so a
/// byte-by-byte comparison is a tight equivalence test.</para>
/// </summary>
public class WalReplayIdempotencyTests
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

    private sealed class InMemoryPersister : IChannelStatePersister
    {
        public ChannelStateSnapshot? Last;
        public int SaveCount;
        public ChannelStateSnapshot? TryLoad(byte n)
            => Last is { } s && s.ChannelNumber == n ? s : null;
        public long Save(ChannelStateSnapshot s)
        {
            Interlocked.Increment(ref SaveCount);
            Volatile.Write(ref Last, s);
            return 1;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wal-idempotency-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private static SnapshotThrottlePolicy NeverAuto =>
        new() { EveryNCommands = int.MaxValue, MinIntervalMs = 0 };

    private static ChannelDispatcher BuildDispatcher(
        IChannelStatePersister? persister,
        IChannelWriteAheadLog? wal)
    {
        return new ChannelDispatcher(
            channelNumber: Channel,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            packetSink: new NoOpPacketSink(),
            outbound: new NoOpOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance,
            metrics: null,
            persister: persister,
            snapshotThrottle: NeverAuto,
            useAsyncSnapshotWriter: false,
            wal: wal);
    }

    private enum Op { New, Cancel, Replace }

    private sealed record Cmd(Op Kind, SessionId Session, uint Firm, ulong ClOrdId,
        ulong OrigClOrdId, NewOrderCommand? New, CancelOrderCommand? Cancel,
        ReplaceOrderCommand? Replace);

    /// <summary>
    /// Builds a deterministic command sequence. Cancel/Replace target a
    /// previously-issued NewOrder by <c>OrigClOrdId</c> so they exercise
    /// the index lookup path and produce a realistic mix of resting,
    /// canceled, replaced, and traded orders.
    /// </summary>
    private static List<Cmd> BuildSequence(int seed, int n)
    {
        var rng = new Random(seed);
        var cmds = new List<Cmd>(n);
        var live = new List<(SessionId Session, uint Firm, ulong ClOrdId, Side Side)>();
        ulong nextClOrd = 1;
        ulong nanos = 1_000UL;

        // Two firms, two sessions to also exercise the per-(firm,clOrdId) index.
        var sessions = new[] { new SessionId("11111"), new SessionId("22222") };
        var firms = new uint[] { 700, 701 };

        for (int i = 0; i < n; i++)
        {
            // Bias toward NewOrder so live[] keeps refilling; only choose
            // Cancel/Replace when there is at least one live target.
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
                // Issue #295 follow-up: keep buy/sell ranges DISJOINT
                // so resting orders never trade out from under a
                // Cancel/Replace that targets them. The determinism
                // invariant is what's under test, not the matcher's
                // crossing logic — which has dedicated coverage in
                // MatchingTests.
                var price = side == Side.Buy
                    ? Px(9.95m + 0.01m * rng.Next(0, 4))
                    : Px(10.05m + 0.01m * rng.Next(0, 4));
                var qty = 100L * rng.Next(1, 4);
                var clOrd = nextClOrd++;
                var no = new NewOrderCommand(
                    ClOrdId: "CL-" + clOrd.ToString(),
                    SecurityId: Sec,
                    Side: side,
                    Type: OrderType.Limit,
                    Tif: TimeInForce.Day,
                    PriceMantissa: price,
                    Quantity: qty,
                    EnteringFirm: f,
                    EnteredAtNanos: nanos);
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
                    SecurityId: Sec,
                    OrderId: 0, // resolve via OrigClOrdId
                    EnteredAtNanos: nanos);
                cmds.Add(new Cmd(Op.Cancel, t.Session, t.Firm, clOrd, t.ClOrdId,
                    null, co, null));
            }
            else
            {
                int idx = rng.Next(live.Count);
                var t = live[idx];
                var clOrd = nextClOrd++;
                // Same-side, non-crossing replacement (see #295 above).
                var newPrice = t.Side == Side.Buy
                    ? Px(9.95m + 0.01m * rng.Next(0, 4))
                    : Px(10.05m + 0.01m * rng.Next(0, 4));
                var newQty = 100L * rng.Next(1, 4);
                var ro = new ReplaceOrderCommand(
                    ClOrdId: "CL-" + clOrd.ToString(),
                    SecurityId: Sec,
                    OrderId: 0,
                    NewPriceMantissa: newPrice,
                    NewQuantity: newQty,
                    EnteredAtNanos: nanos);
                cmds.Add(new Cmd(Op.Replace, t.Session, t.Firm, clOrd, t.ClOrdId,
                    null, null, ro));
                // gpt-5.5 round-2 review (PR #300): the production
                // dispatcher rotates the (firm, clOrdId) index entry to
                // the replacement's NEW ClOrdId after a successful
                // Replace (ChannelDispatcher.Sinks.OnOrderModified →
                // _orders.Reregister). If we kept the original ClOrdId
                // here, subsequent Cancel/Replace commands targeting
                // the same order would carry a stale OrigClOrdId,
                // TryResolveByClOrdId would miss, and the helper's
                // skip-when-unresolvable branch would silently drop
                // every later op for that chain — masking generator
                // bugs and shrinking realistic-replace-chain coverage.
                live[idx] = (t.Session, t.Firm, clOrd, t.Side);
            }
        }
        return cmds;
    }

    private static void Apply(ChannelDispatcher disp, Cmd c)
    {
        bool ok = c.Kind switch
        {
            Op.New => disp.EnqueueNewOrder(c.New!, c.Session, c.Firm, c.ClOrdId),
            Op.Cancel => disp.EnqueueCancel(c.Cancel!, c.Session, c.Firm, c.ClOrdId, c.OrigClOrdId),
            Op.Replace => disp.EnqueueReplace(c.Replace!, c.Session, c.Firm, c.ClOrdId, c.OrigClOrdId),
            _ => throw new InvalidOperationException(),
        };
        Assert.True(ok, $"enqueue rejected for {c.Kind}");
    }

    /// <summary>
    /// Issue #295 follow-up: drives the dispatcher the way <c>HostRouter</c>
    /// drives it in production. For <c>Cancel</c>/<c>Replace</c> the
    /// gateway-side router resolves <c>OrigClOrdId → OrderId</c> via the
    /// dispatcher's order registry and stamps the resolved <c>OrderId</c>
    /// into the command BEFORE calling <c>EnqueueCancel</c>/<c>EnqueueReplace</c>.
    /// Direct enqueueing with <c>OrderId == 0</c> is short-circuited by
    /// <c>ProcessOne</c> (it emits an <c>UnknownOrderIdReject</c> and never
    /// reaches the engine) — which means a test that submits Cancel/Replace
    /// with <c>OrderId == 0</c> silently exercises the reject path, not the
    /// engine cancel/replace path. This helper interleaves enqueue + drain
    /// so the engine state is current at the resolution point and the
    /// resolved <c>OrderId</c> can be stamped before each command.
    /// </summary>
    private static int s_resolvedApplied;
    private static int s_resolveAttempts;

    private static void ApplyResolvingViaIndex(
        ChannelDispatcher disp,
        ChannelDispatcher.TestProbe probe,
        Cmd c)
    {
        switch (c.Kind)
        {
            case Op.New:
                Assert.True(disp.EnqueueNewOrder(c.New!, c.Session, c.Firm, c.ClOrdId));
                break;
            case Op.Cancel:
                {
                    System.Threading.Interlocked.Increment(ref s_resolveAttempts);
                    if (!disp.TryResolveByClOrdId(c.Firm, c.OrigClOrdId, out var orderId, out var secId))
                    {
                        // Mirror HostRouter: emit UnknownOrderId reject and
                        // skip both WAL and engine. The order may have been
                        // matched out (Replace clOrdId rotation) — same
                        // realistic skip behaviour the gateway exhibits.
                        break;
                    }
                    var resolved = c.Cancel! with { OrderId = orderId, SecurityId = secId };
                    Assert.True(disp.EnqueueCancel(resolved, c.Session, c.Firm, c.ClOrdId, c.OrigClOrdId));
                    System.Threading.Interlocked.Increment(ref s_resolvedApplied);
                    break;
                }
            case Op.Replace:
                {
                    System.Threading.Interlocked.Increment(ref s_resolveAttempts);
                    if (!disp.TryResolveByClOrdId(c.Firm, c.OrigClOrdId, out var orderId, out var secId))
                        break;
                    var resolved = c.Replace! with { OrderId = orderId, SecurityId = secId };
                    Assert.True(disp.EnqueueReplace(resolved, c.Session, c.Firm, c.ClOrdId, c.OrigClOrdId));
                    System.Threading.Interlocked.Increment(ref s_resolvedApplied);
                    break;
                }
            default: throw new InvalidOperationException();
        }
        // Drain after every command so the next Cancel/Replace sees an
        // engine state that already reflects the dependency. DrainInbound
        // is synchronous on the test thread; the dispatch loop is not
        // running in baseline / snapshot-at-K builders.
        probe.DrainInbound();
    }

    private static async Task<(byte[] BaselineBytes, IReadOnlyList<WalRecord> WalRecords)>
        RunBaseline(string root, IReadOnlyList<Cmd> cmds)
    {
        var dir = Path.Combine(root, "baseline");
        Directory.CreateDirectory(dir);

        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var disp = BuildDispatcher(persister, wal);
        var probe = disp.CreateTestProbe();

        s_resolvedApplied = 0;
        s_resolveAttempts = 0;
        foreach (var c in cmds) ApplyResolvingViaIndex(disp, probe, c);

        // gpt-5.5 round-2 review (PR #300): with the live[] entry now
        // tracking the rotated ClOrdId after every Replace (mirroring
        // production's _orders.Reregister), the vast majority of
        // Cancel/Replace commands generated by BuildSequence MUST
        // resolve. A wholesale silent-skip would mean we are exercising
        // only the New path again — defeating the point of these
        // idempotency tests. Allow a small skip ratio for cases where
        // a Cancel removed a target before its dependent Cancel/Replace
        // (the input-stream order can interleave them per RNG draw),
        // but never permit "all skipped".
        if (s_resolveAttempts > 0)
        {
            Assert.True(s_resolvedApplied > s_resolveAttempts / 2,
                $"too many Cancel/Replace commands silently skipped: applied={s_resolvedApplied}/{s_resolveAttempts}");
        }

        // Read WAL while it still has every record (force-flush below
        // truncates after a successful Save). We may have skipped some
        // Cancel/Replace items that lost their target (Replace clOrdId
        // rotation), so records.Count is bounded above by cmds.Count.
        var records = wal.ReadAll();
        Assert.True(records.Count <= cmds.Count);
        Assert.True(records.Count > 0, "baseline produced no WAL records");

        probe.FlushPendingSnapshotOnShutdown();
        Assert.NotNull(persister.Last);
        Assert.Equal((long)records.Count, persister.Last!.LastAppliedSeq);

        var bytes = BinaryChannelStateSnapshotCodec.Encode(persister.Last);
        wal.Dispose();
        await disp.DisposeAsync();
        return (bytes, records);
    }

    /// <summary>
    /// Builds a snapshot anchored at WAL record <paramref name="k"/> by
    /// staging a fresh WAL with <c>records[0..k]</c> and letting the
    /// dispatcher's normal replay path apply it, then force-persisting.
    /// Anchoring on WAL records (not on input <c>Cmd</c> indices) keeps
    /// the split-point arithmetic consistent with the dedup branch under
    /// test, which gates on <c>WalRecord.Seq</c>.
    /// </summary>
    private static async Task<ChannelStateSnapshot> BuildSnapshotAtK(
        string root, IReadOnlyList<WalRecord> walRecords, int k)
    {
        var dir = Path.Combine(root, $"snap-{k}");
        Directory.CreateDirectory(dir);

        using (var stage = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            for (int i = 0; i < k; i++) stage.Append(walRecords[i]);
        }

        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var disp = BuildDispatcher(persister, wal);
        disp.Start();

        var savesBefore = Volatile.Read(ref persister.SaveCount);
        Assert.True(disp.EnqueueOperatorPersistSnapshot());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref persister.SaveCount) == savesBefore
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
        Assert.True(Volatile.Read(ref persister.SaveCount) > savesBefore);
        Assert.Equal((long)k, persister.Last!.LastAppliedSeq);

        var snap = Volatile.Read(ref persister.Last)!;
        await disp.DisposeAsync();
        wal.Dispose();
        return snap;
    }

    private static async Task<byte[]> ReplayAndCapture(
        string root, ChannelStateSnapshot snapAtK,
        IReadOnlyList<WalRecord> walTail, int k)
    {
        var dir = Path.Combine(root, $"replay-{k}");
        Directory.CreateDirectory(dir);

        // Stage the WAL with only records past K. The replay path skips
        // any record whose Seq <= snapshot.LastAppliedSeq, but we trim
        // here too to mirror the realistic "snapshot covers prefix"
        // operational scenario.
        using (var stage = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            foreach (var r in walTail)
                if (r.Seq > k) stage.Append(r);
        }

        var persister = new InMemoryPersister { Last = snapAtK };
        var wal = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var disp = BuildDispatcher(persister, wal);

        // Drive the actual replay code path: Start runs
        // LoadPersistedStateOnLoopThread → ReplayWalOnLoopThread on the
        // dispatch thread, then awaits the inbound queue.
        disp.Start();

        // Wait for replay to finish. Quiescence indicators that don't
        // race with the loop's WaitToReadAsync wake-ups: an admin
        // OperatorPersistSnapshot writes through the persister synchronously
        // on the loop thread, so a SaveCount bump after enqueue means the
        // loop has already drained the (replay → persist) sequence.
        var savesBefore = Volatile.Read(ref persister.SaveCount);
        Assert.True(disp.EnqueueOperatorPersistSnapshot());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref persister.SaveCount) == savesBefore
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
        Assert.True(Volatile.Read(ref persister.SaveCount) > savesBefore,
            "operator persist did not complete within timeout");

        var snap = Volatile.Read(ref persister.Last)!;
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);

        await disp.DisposeAsync();
        wal.Dispose();
        return bytes;
    }

    [Theory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    [InlineData(44)]
    [InlineData(57)]
    [InlineData(91)]
    public async Task Replay_FromOlderSnapshot_PlusWalTail_EqualsBaseline(int seed)
    {
        const int N = 24;
        using var temp = new TempDir();

        var cmds = BuildSequence(seed, N);
        var (baselineBytes, walRecords) = await RunBaseline(temp.Path, cmds);

        // Split points expressed in WAL-record indices (the dedup branch
        // gates on WalRecord.Seq, not on input cmd index — those differ
        // because Cancel/Replace whose target was already gone are
        // skipped at the producer side).
        int rec = walRecords.Count;
        int[] splits = { 1, rec / 4, rec / 2, (3 * rec) / 4, rec - 1 };
        foreach (var k in splits)
        {
            if (k < 1 || k >= rec) continue;
            var snapAtK = await BuildSnapshotAtK(temp.Path, walRecords, k);
            Assert.Equal((long)k, snapAtK.LastAppliedSeq);

            var replayBytes = await ReplayAndCapture(temp.Path, snapAtK, walRecords, k);

            Assert.True(baselineBytes.AsSpan().SequenceEqual(replayBytes),
                $"seed={seed} k={k}: replay state diverged from baseline "
                + $"(baseline={baselineBytes.Length}B, replay={replayBytes.Length}B)");
        }
    }

    /// <summary>
    /// Bootstrapping degenerate case: snapshot is absent (older deployment,
    /// snapshot file deleted, or first-ever boot) so the WAL by itself must
    /// reconstruct the same final state.
    /// </summary>
    [Theory]
    [InlineData(13)]
    [InlineData(29)]
    public async Task Replay_FromEmptyState_PlusFullWal_EqualsBaseline(int seed)
    {
        const int N = 18;
        using var temp = new TempDir();

        var cmds = BuildSequence(seed, N);
        var (baselineBytes, walRecords) = await RunBaseline(temp.Path, cmds);

        // Build the "no snapshot" recovery: persister returns null on
        // TryLoad; the dispatcher boots into an empty engine and replays
        // the full WAL.
        var dir = Path.Combine(temp.Path, "noload");
        Directory.CreateDirectory(dir);
        using (var stage = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            foreach (var r in walRecords) stage.Append(r);
        }
        var persister = new InMemoryPersister(); // Last == null
        var wal = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var disp = BuildDispatcher(persister, wal);
        disp.Start();

        var savesBefore = Volatile.Read(ref persister.SaveCount);
        Assert.True(disp.EnqueueOperatorPersistSnapshot());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref persister.SaveCount) == savesBefore
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
        Assert.True(Volatile.Read(ref persister.SaveCount) > savesBefore);

        var bytes = BinaryChannelStateSnapshotCodec.Encode(
            Volatile.Read(ref persister.Last)!);

        await disp.DisposeAsync();
        wal.Dispose();

        Assert.True(baselineBytes.AsSpan().SequenceEqual(bytes),
            $"seed={seed}: WAL-only replay diverged from baseline");
    }

    /// <summary>
    /// Issue #295 follow-up (MEDIUM): exercises the
    /// <c>Seq &lt;= snapshot.LastAppliedSeq</c> dedup branch in
    /// <c>ChannelDispatcher.ReplayWalOnLoopThread</c>. The realistic
    /// disaster scenario is: snapshot persisted at K, WAL kept on
    /// growing past K, then a crash happens before WAL truncation —
    /// boot-time replay sees a WAL that overlaps the snapshot prefix.
    /// The dispatcher must skip the prefix without re-applying it
    /// (re-applying a Cancel for an already-canceled order would
    /// throw / double-cancel) and converge to the same final state.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(33)]
    [InlineData(57)]
    public async Task Replay_SnapshotPlusFullOverlappingWal_DedupsAndEqualsBaseline(int seed)
    {
        const int N = 24;
        using var temp = new TempDir();

        var cmds = BuildSequence(seed, N);
        var (baselineBytes, walRecords) = await RunBaseline(temp.Path, cmds);

        int rec = walRecords.Count;
        int[] splits = { 1, rec / 4, rec / 2, (3 * rec) / 4, rec - 1 };
        foreach (var k in splits)
        {
            if (k < 1 || k >= rec) continue;
            var snapAtK = await BuildSnapshotAtK(temp.Path, walRecords, k);
            Assert.Equal((long)k, snapAtK.LastAppliedSeq);

            var replayBytes = await ReplayWithFullWalAndCapture(
                temp.Path, snapAtK, walRecords, k);

            Assert.True(baselineBytes.AsSpan().SequenceEqual(replayBytes),
                $"seed={seed} k={k}: dedup replay diverged from baseline "
                + $"(baseline={baselineBytes.Length}B, replay={replayBytes.Length}B)");
        }
    }

    private static async Task<byte[]> ReplayWithFullWalAndCapture(
        string root, ChannelStateSnapshot snapAtK,
        IReadOnlyList<WalRecord> walTail, int k)
    {
        var dir = Path.Combine(root, $"replay-full-{k}");
        Directory.CreateDirectory(dir);

        // Stage EVERY record (Seq 1..N) — overlap with the snapshot
        // prefix is intentional. The dispatcher must skip records with
        // Seq <= k during replay.
        using (var stage = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            foreach (var r in walTail) stage.Append(r);
        }

        var persister = new InMemoryPersister { Last = snapAtK };
        var wal = new FileChannelWriteAheadLog(dir, Channel,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var disp = BuildDispatcher(persister, wal);

        disp.Start();

        var savesBefore = Volatile.Read(ref persister.SaveCount);
        Assert.True(disp.EnqueueOperatorPersistSnapshot());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref persister.SaveCount) == savesBefore
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
        Assert.True(Volatile.Read(ref persister.SaveCount) > savesBefore,
            "operator persist did not complete within timeout");

        var snap = Volatile.Read(ref persister.Last)!;
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);

        await disp.DisposeAsync();
        wal.Dispose();
        return bytes;
    }
}
