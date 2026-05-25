using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using OrderType = B3.Exchange.Matching.OrderType;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #270: cross-channel consistency check on restore. Verifies
/// that <see cref="ChannelDispatcher.RestoreChannelState"/> partitions
/// owners by the host's session-existence predicate and applies the
/// configured <see cref="OrphanSessionPolicy"/>: <see cref="OrphanSessionPolicy.Drop"/>
/// logs + skips orphan owners (engine state still loads), while
/// <see cref="OrphanSessionPolicy.Reject"/> throws so the channel
/// fails closed.
/// </summary>
public class ChannelDispatcherOrphanSessionTests
{
    private const long Sec = 900_000_000_001L;

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

    private sealed class NoOpPacketSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private sealed class NoOpOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue, DurabilityHandle d = default, InvestorId? iv = null) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c, DurabilityHandle d = default) => true;
    }

    private static ChannelDispatcher BuildDispatcher(
        ChannelMetrics? metrics,
        Func<string, bool>? sessionExists,
        OrphanSessionPolicy policy)
    {
        return new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = new NoOpOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Metrics = metrics,
                SessionExists = sessionExists,
                OrphanPolicy = policy,
            });
    }

    private static ChannelStateSnapshot BuildSnapshotWithOwners(params string[] sessionIds)
    {
        var orders = new List<RestingOrderRecord>();
        var owners = new List<OrderOwnerSnapshot>();
        long nextId = 1;
        foreach (var sid in sessionIds)
        {
            long oid = nextId++;
            orders.Add(new RestingOrderRecord(
                OrderId: oid,
                ClOrdId: "CL-" + oid,
                Side: Side.Buy,
                PriceMantissa: 100000,
                RemainingQuantity: 100,
                EnteringFirm: 100,
                InsertTimestampNanos: 1000UL,
                Tif: TimeInForce.Day,
                MaxFloor: 100,
                HiddenQuantity: 0));
            owners.Add(new OrderOwnerSnapshot(
                OrderId: oid,
                SessionValue: sid,
                Firm: 100,
                ClOrdId: (ulong)oid,
                Side: Side.Buy,
                SecurityId: Sec));
        }
        var book = new EngineStateSnapshot.BookSnapshot(Sec, orders);
        var engine = new EngineStateSnapshot(
            NextOrderId: nextId,
            NextTradeId: 1,
            RptSeq: 0,
            Phases: Array.Empty<EngineStateSnapshot.PhaseEntry>(),
            Books: new[] { book });
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 0,
            SequenceVersion: 1,
            Engine: engine,
            Owners: owners);
    }

    [Fact]
    public void DropPolicy_OrphanOwnerIsSkipped_KnownOwnerIsRegistered()
    {
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(metrics,
            sessionExists: sid => sid == "10101",
            policy: OrphanSessionPolicy.Drop);
        var snap = BuildSnapshotWithOwners("10101", "99999");

        disp.RestoreChannelState(snap);

        // Both orders sit in the engine book; only the recognised
        // session's owner is in the routing map.
        Assert.Equal(2, snap.Engine.Books.Single().Orders.Count);
        Assert.Equal(1L, metrics.OwnerOrphansDropped);
    }

    [Fact]
    public void RejectPolicy_OrphanOwnerThrows()
    {
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(metrics,
            sessionExists: sid => sid == "10101",
            policy: OrphanSessionPolicy.Reject);
        var snap = BuildSnapshotWithOwners("10101", "99999");

        var ex = Assert.Throws<InvalidOperationException>(
            () => disp.RestoreChannelState(snap));
        Assert.Contains("99999", ex.Message);
        Assert.Contains("orphan", ex.Message);
    }

    [Fact]
    public void NullPredicate_RestoresEverything_NoOrphanCounting()
    {
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(metrics,
            sessionExists: null,
            policy: OrphanSessionPolicy.Reject);
        var snap = BuildSnapshotWithOwners("10101", "99999");

        // No predicate ⇒ legacy behaviour: every owner registers and
        // the orphan counter stays at zero regardless of policy.
        disp.RestoreChannelState(snap);

        Assert.Equal(0L, metrics.OwnerOrphansDropped);
    }

    [Fact]
    public void DropPolicy_AllOwnersOrphan_MetricCountsAll()
    {
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(metrics,
            sessionExists: _ => false,
            policy: OrphanSessionPolicy.Drop);
        var snap = BuildSnapshotWithOwners("10101", "20202", "30303");

        disp.RestoreChannelState(snap);

        Assert.Equal(3L, metrics.OwnerOrphansDropped);
    }
}
