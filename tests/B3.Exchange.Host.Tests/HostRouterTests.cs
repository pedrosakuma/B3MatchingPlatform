using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using B3.Exchange.Host;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

public class HostRouterTests
{
    private sealed class NoopPacketSink : IUmdfPacketSink
    {
        public List<int> Calls { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Calls.Add(channelNumber);
    }

    private sealed class RecordingOutbound : ICoreOutbound
    {
        public List<RejectEvent> Rejects { get; } = new();
        public List<string> Events { get; } = new();
        public bool CancelEnqueueResult { get; set; } = true;
        public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle durability = default) { Events.Add("new"); return true; }
        public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty, DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty, DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle durability = default) { Events.Add("cancel"); return CancelEnqueueResult; }
        public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle durability = default, InvestorId? investorId = null) => true;
        public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue, DurabilityHandle durability = default) { Rejects.Add(e); return true; }
    }

    [Fact]
    public void UnknownInstrument_RoutesToInlineRejectWithoutDispatcher()
    {
        var routing = new Dictionary<long, ChannelDispatcher>(); // empty
        var outbound = new RecordingOutbound();
        var router = new HostRouter(routing, outbound, NullLogger<HostRouter>.Instance, new FakeNanosTimeSource(1_000UL));
        router.EnqueueNewOrder(
            new NewOrderCommand("1", SecurityId: 12345, Side.Buy, OrderType.Limit, TimeInForce.Day, 100, 100, 1, 0),
            new B3.Exchange.Contracts.SessionId("s1"), enteringFirm: 1, clOrdIdValue: 1);
        var rej = Assert.Single(outbound.Rejects);
        Assert.Equal(12345, rej.SecurityId);
        Assert.Equal(RejectReason.UnknownInstrument, rej.Reason);
    }

    [Fact]
    public void KnownInstrument_RoutesToDispatcher()
    {
        var inst = new Instrument
        {
            Symbol = "TEST",
            SecurityId = 42,
            TickSize = 0.01m,
            LotSize = 1,
            MinPrice = 0.01m,
            MaxPrice = 1000m,
            Currency = "BRL",
            Isin = "X",
            SecurityType = "CS"
        };
        var pkt = new NoopPacketSink();
        var outbound = new RecordingOutbound();
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => new MatchingEngine(new[] { inst }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = pkt,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000UL),
            });
        // Dispatcher loop not started; we read inbound queue directly.
        var router = new HostRouter(new Dictionary<long, ChannelDispatcher> { [42] = disp }, outbound, NullLogger<HostRouter>.Instance);

        router.EnqueueNewOrder(
            new NewOrderCommand("1", SecurityId: 42, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 1, 0),
            new B3.Exchange.Contracts.SessionId("s1"), enteringFirm: 1, clOrdIdValue: 1);

        Assert.Empty(outbound.Rejects);
        // Drain the dispatcher queue and assert it processed the order
        // (replaces the prior reflection-based drain — issue #157).
        disp.CreateTestProbe().DrainInbound();
        Assert.Single(pkt.Calls);
    }

    [Fact]
    public void SolicitedMassCancel_CompletesAfterCancellationReports()
    {
        var inst1 = CreateInstrument(42, "TEST1");
        var inst2 = CreateInstrument(43, "TEST2");
        var outbound = new RecordingOutbound();
        var disp1 = CreateDispatcher(inst1, outbound, channelNumber: 1);
        var disp2 = CreateDispatcher(inst2, outbound, channelNumber: 2);
        var router = new HostRouter(
            new Dictionary<long, ChannelDispatcher>
            {
                [inst1.SecurityId] = disp1,
                [inst2.SecurityId] = disp2,
            },
            outbound, NullLogger<HostRouter>.Instance);
        var session = new SessionId("s1");
        var probe1 = disp1.CreateTestProbe();
        var probe2 = disp2.CreateTestProbe();

        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("1", inst1.SecurityId, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10m), 100, 7, 1),
            session, enteringFirm: 7, clOrdIdValue: 1));
        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("2", inst2.SecurityId, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(9m), 100, 7, 2),
            session, enteringFirm: 7, clOrdIdValue: 2));
        probe1.DrainInbound();
        probe2.DrainInbound();
        outbound.Events.Clear();

        MassCancelOutcome? outcome = null;
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(0, null, 3),
            session, enteringFirm: 7,
            completed =>
            {
                outbound.Events.Add("complete");
                outcome = completed;
            }));

        Assert.Null(outcome);
        Assert.Empty(outbound.Events);

        probe1.DrainInbound();
        Assert.Null(outcome);
        Assert.Equal(new[] { "cancel" }, outbound.Events);

        probe2.DrainInbound();

        Assert.Equal(new[] { "cancel", "cancel", "complete" }, outbound.Events);
        Assert.True(outcome?.Succeeded);
        Assert.Equal(2, outcome?.TotalAffectedOrders);
    }

    [Fact]
    public void SolicitedMassCancel_CancelReportFailureCompletesSystemBusy()
    {
        var inst = CreateInstrument();
        var outbound = new RecordingOutbound { CancelEnqueueResult = false };
        var disp = CreateDispatcher(inst, outbound);
        var router = new HostRouter(
            new Dictionary<long, ChannelDispatcher> { [inst.SecurityId] = disp },
            outbound, NullLogger<HostRouter>.Instance);
        var session = new SessionId("s1");
        var probe = disp.CreateTestProbe();

        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("1", inst.SecurityId, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10m), 100, 7, 1),
            session, enteringFirm: 7, clOrdIdValue: 1));
        probe.DrainInbound();
        outbound.Events.Clear();

        MassCancelOutcome? outcome = null;
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(inst.SecurityId, null, 2),
            session, enteringFirm: 7,
            completed =>
            {
                outbound.Events.Add("complete");
                outcome = completed;
            }));

        probe.DrainInbound();

        Assert.Equal(new[] { "cancel", "complete" }, outbound.Events);
        Assert.False(outcome?.Succeeded);
    }

    [Fact]
    public void SolicitedMassCancel_ZeroMatchesCompletesImmediately()
    {
        var inst = CreateInstrument();
        var outbound = new RecordingOutbound();
        var disp = CreateDispatcher(inst, outbound);
        var router = new HostRouter(
            new Dictionary<long, ChannelDispatcher> { [inst.SecurityId] = disp },
            outbound, NullLogger<HostRouter>.Instance);

        MassCancelOutcome? outcome = null;
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(inst.SecurityId, null, 1),
            new SessionId("s1"), enteringFirm: 7,
            completed => outcome = completed));

        Assert.True(outcome?.Succeeded);
        Assert.Equal(0, outcome?.TotalAffectedOrders);
        Assert.Equal(0, disp.InboundQueueDepth);
    }

    private static Instrument CreateInstrument(long securityId = 42, string symbol = "TEST") => new()
    {
        Symbol = symbol,
        SecurityId = securityId,
        TickSize = 0.01m,
        LotSize = 1,
        MinPrice = 0.01m,
        MaxPrice = 1000m,
        Currency = "BRL",
        Isin = "X",
        SecurityType = "CS"
    };

    private static ChannelDispatcher CreateDispatcher(Instrument inst, RecordingOutbound outbound,
        byte channelNumber = 1)
        => new(channelNumber: channelNumber,
            engineFactory: s => new MatchingEngine(new[] { inst }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoopPacketSink(),
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000UL),
            });

    private static long Px(decimal p) => (long)(p * 10_000m);
}
