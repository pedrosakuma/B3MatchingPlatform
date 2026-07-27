using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using B3.Exchange.Gateway;
using B3.Exchange.Host;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

public class HostRouterTests
{
    private sealed class NoopInbound : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;
        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private sealed class NoopPacketSink : IUmdfPacketSink
    {
        public List<int> Calls { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Calls.Add(channelNumber);
    }

    private sealed class RecordingOutbound : ICoreOutbound
    {
        public List<RejectEvent> Rejects { get; } = new();
        public List<string> Events { get; } = new();
        public OrderedStreamWriteResult CancelWriteResult { get; set; } =
            OrderedStreamWriteResult.CommittedAndEnqueued;
        public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle durability = default) { Events.Add("new"); return true; }
        public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty, DurabilityHandle durability = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty, DurabilityHandle durability = default) => true;
        public OrderedStreamWriteResult WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle durability = default) { Events.Add("cancel"); return CancelWriteResult; }
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
        var outbound = new RecordingOutbound
        {
            CancelWriteResult = OrderedStreamWriteResult.NotCommitted,
        };
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SolicitedMassCancel_DeferredReportDoesNotBlockUnrelatedWork(
        bool commitDeferredReport)
    {
        var inst = CreateInstrument();
        var deferred = new TaskCompletionSource<OrderedStreamWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outbound = new RecordingOutbound
        {
            CancelWriteResult = OrderedStreamWriteResult.Deferred(deferred.Task),
        };
        var disp = CreateDispatcher(inst, outbound);
        var router = new HostRouter(
            new Dictionary<long, ChannelDispatcher> { [inst.SecurityId] = disp },
            outbound, NullLogger<HostRouter>.Instance);
        var probe = disp.CreateTestProbe();
        var owner = new SessionId("owner");

        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("1", inst.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1),
            owner, enteringFirm: 7, clOrdIdValue: 1));
        probe.DrainInbound();
        outbound.Events.Clear();

        var terminal = new TaskCompletionSource<MassCancelOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(inst.SecurityId, null, 2),
            owner, enteringFirm: 7,
            outcome =>
            {
                outbound.Events.Add(outcome.Succeeded ? "accepted" : "system-busy");
                terminal.TrySetResult(outcome);
            }));
        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("2", inst.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(9m), 100, 8, 3),
            new SessionId("unrelated"), enteringFirm: 8, clOrdIdValue: 2));

        probe.DrainInbound();

        Assert.False(terminal.Task.IsCompleted);
        Assert.Equal(new[] { "cancel", "new" }, outbound.Events);

        deferred.TrySetResult(commitDeferredReport
            ? OrderedStreamWriteResult.CommittedAndEnqueued
            : OrderedStreamWriteResult.NotCommitted);
        var outcome = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(commitDeferredReport, outcome.Succeeded);
        Assert.Equal(commitDeferredReport ? 1 : 0, outcome.TotalAffectedOrders);
        Assert.Equal(
            new[] { "cancel", "new", commitDeferredReport ? "accepted" : "system-busy" },
            outbound.Events);
    }

    [Fact]
    public async Task SolicitedMassCancel_DisposeCancelsNeverCompletingDeferredReport()
    {
        var inst = CreateInstrument();
        var deferred = new TaskCompletionSource<OrderedStreamWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var outbound = new RecordingOutbound
        {
            CancelWriteResult = OrderedStreamWriteResult.Deferred(deferred.Task),
        };
        var disp = CreateDispatcher(inst, outbound);
        var router = new HostRouter(
            new Dictionary<long, ChannelDispatcher> { [inst.SecurityId] = disp },
            outbound, NullLogger<HostRouter>.Instance);
        var probe = disp.CreateTestProbe();
        var owner = new SessionId("owner");

        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("1", inst.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1),
            owner, enteringFirm: 7, clOrdIdValue: 1));
        probe.DrainInbound();
        outbound.Events.Clear();

        var terminal = new TaskCompletionSource<MassCancelOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(inst.SecurityId, null, 2),
            owner, enteringFirm: 7,
            outcome =>
            {
                Interlocked.Increment(ref callbackCount);
                outbound.Events.Add(outcome.Succeeded ? "accepted" : "system-busy");
                terminal.TrySetResult(outcome);
            }));
        probe.DrainInbound();

        Assert.False(terminal.Task.IsCompleted);
        Assert.Equal(new[] { "cancel" }, outbound.Events);

        await disp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        var outcome = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.Equal(new[] { "cancel", "system-busy" }, outbound.Events);
        Assert.DoesNotContain("accepted", outbound.Events);
        Assert.False(deferred.Task.IsCompleted);
    }

    [Fact]
    public void SolicitedMassCancel_PartialEnqueueDefersSystemBusyUntilAcceptedReports()
    {
        var inst1 = CreateInstrument(42, "TEST1");
        var inst2 = CreateInstrument(43, "TEST2");
        var outbound = new RecordingOutbound();
        var disp1 = CreateDispatcher(inst1, outbound, channelNumber: 1);
        var disp2 = CreateDispatcher(inst2, outbound, channelNumber: 2, inboundCapacity: 1);
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

        // Fill only the later dispatcher's queue. Dictionary insertion order
        // makes channel 1 accept before channel 2 rejects deterministically.
        Assert.True(disp2.EnqueueNewOrder(
            new NewOrderCommand("queue-filler", inst2.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(8m), 100, 7, 3),
            session, enteringFirm: 7, clOrdIdValue: 3));

        MassCancelOutcome? outcome = null;
        int completions = 0;
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(0, null, 4),
            session, enteringFirm: 7,
            completed =>
            {
                completions++;
                outbound.Events.Add("system-busy");
                outcome = completed;
            }));

        Assert.Null(outcome);
        Assert.Empty(outbound.Events);

        probe1.DrainInbound();

        Assert.Equal(new[] { "cancel", "system-busy" }, outbound.Events);
        Assert.False(outcome?.Succeeded);
        Assert.Equal(0, outcome?.TotalAffectedOrders);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void UnsolicitedMassCancel_PartialEnqueueReturnsFalse()
    {
        var inst1 = CreateInstrument(42, "TEST1");
        var inst2 = CreateInstrument(43, "TEST2");
        var outbound = new RecordingOutbound();
        var disp1 = CreateDispatcher(inst1, outbound, channelNumber: 1);
        var disp2 = CreateDispatcher(
            inst2, outbound, channelNumber: 2, inboundCapacity: 1);
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
            new NewOrderCommand("1", inst1.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1),
            session, enteringFirm: 7, clOrdIdValue: 1));
        Assert.True(router.EnqueueNewOrder(
            new NewOrderCommand("2", inst2.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(9m), 100, 7, 2),
            session, enteringFirm: 7, clOrdIdValue: 2));
        probe1.DrainInbound();
        probe2.DrainInbound();
        outbound.Events.Clear();

        Assert.True(disp2.EnqueueNewOrder(
            new NewOrderCommand("queue-filler", inst2.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(8m), 100, 7, 3),
            session, enteringFirm: 7, clOrdIdValue: 3));

        Assert.False(router.EnqueueMassCancel(
            new MassCancelCommand(0, null, 4),
            session, enteringFirm: 7));

        probe1.DrainInbound();
        Assert.Equal(new[] { "cancel" }, outbound.Events);
    }

    [Fact]
    public async Task PendingHandshake_QueuesBoundedlyWithoutBlockingUnrelatedChannelWork()
    {
        var inst = CreateInstrument();
        var registry = new SessionRegistry();
        var inbound = new NoopInbound();
        await using var pending = new FixpSession(
            connectionId: 10,
            enteringFirm: 7,
            sessionId: 1,
            stream: new MemoryStream(),
            sink: inbound,
            logger: NullLogger<FixpSession>.Instance,
            sendQueueCapacity: 1,
            sessionClaims: new SessionClaimRegistry(),
            sessionRegistry: registry);
        await using var active = new FixpSession(
            connectionId: 11,
            enteringFirm: 8,
            sessionId: 2,
            stream: new MemoryStream(),
            sink: inbound,
            logger: NullLogger<FixpSession>.Instance,
            sessionRegistry: registry);
        registry.Register(pending);
        registry.Register(active);

        var gateway = new GatewayRouter(
            registry, NullLogger<GatewayRouter>.Instance);
        var disp = new ChannelDispatcher(
            channelNumber: 1,
            engineFactory: sink => new MatchingEngine(
                new[] { inst }, sink, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoopPacketSink(),
                Outbound = gateway,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000UL),
            });
        var probe = disp.CreateTestProbe();

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("pending-1", inst.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1),
            new SessionId("1"), enteringFirm: 7, clOrdIdValue: 1));
        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("active-1", inst.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(9m), 100, 8, 2),
            new SessionId("2"), enteringFirm: 8, clOrdIdValue: 2));

        probe.DrainInbound();

        Assert.Equal(0, disp.InboundQueueDepth);
        Assert.Equal(0u, pending.OutboundSeq);
        Assert.Equal(1u, active.OutboundSeq);
        Assert.True(disp.TryResolveByClOrdId(7, 1, out _, out _));
        Assert.True(disp.TryResolveByClOrdId(8, 2, out _, out _));
        Assert.False(gateway.WriteExecutionReportReject(
            new SessionId("1"),
            new B3.Exchange.Matching.RejectEvent(
                "capacity", inst.SecurityId, 0,
                RejectReason.UnknownOrderId, 3),
            clOrdIdValue: 99));

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("pending-2", inst.SecurityId, Side.Buy,
                OrderType.Limit, TimeInForce.Day, Px(8m), 100, 7, 3),
            new SessionId("1"), enteringFirm: 7, clOrdIdValue: 3));
        probe.DrainInbound();
        Assert.Equal(0u, pending.OutboundSeq);
    }

    [Fact]
    public void SolicitedMassCancel_BufferedCancelReportCompletesAccepted()
    {
        var inst = CreateInstrument();
        var outbound = new RecordingOutbound
        {
            CancelWriteResult = OrderedStreamWriteResult.Committed,
        };
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

        MassCancelOutcome? outcome = null;
        Assert.True(router.EnqueueMassCancel(
            new MassCancelCommand(inst.SecurityId, null, 2),
            session, enteringFirm: 7,
            completed => outcome = completed));

        probe.DrainInbound();

        Assert.True(outcome?.Succeeded);
        Assert.Equal(1, outcome?.TotalAffectedOrders);
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
        byte channelNumber = 1, int inboundCapacity = 4096)
        => new(channelNumber: channelNumber,
            engineFactory: s => new MatchingEngine(new[] { inst }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoopPacketSink(),
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000UL),
                InboundCapacity = inboundCapacity,
            });

    private static long Px(decimal p) => (long)(p * 10_000m);
}
