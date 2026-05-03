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
        public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveTrade(long restingOrderId, in TradeEvent e, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveCancel(long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue) { Rejects.Add(e); return true; }
        public void NotifyOrderTerminal(long orderId) { }
    }

    [Fact]
    public void UnknownInstrument_RoutesToInlineRejectWithoutDispatcher()
    {
        var routing = new Dictionary<long, ChannelDispatcher>(); // empty
        var outbound = new RecordingOutbound();
        var router = new HostRouter(routing, outbound, new OrderOwnershipMap(), NullLogger<HostRouter>.Instance, () => 1_000UL);
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
            packetSink: pkt,
            outbound: outbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            nowNanos: () => 1_000UL);
        // Dispatcher loop not started; we read inbound queue directly.
        var router = new HostRouter(new Dictionary<long, ChannelDispatcher> { [42] = disp }, outbound, new OrderOwnershipMap(), NullLogger<HostRouter>.Instance);

        router.EnqueueNewOrder(
            new NewOrderCommand("1", SecurityId: 42, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 1, 0),
            new B3.Exchange.Contracts.SessionId("s1"), enteringFirm: 1, clOrdIdValue: 1);

        Assert.Empty(outbound.Rejects);
        // Drain the dispatcher queue and assert it processed the order
        // (replaces the prior reflection-based drain — issue #157).
        disp.CreateTestProbe().DrainInbound();
        Assert.Single(pkt.Calls);
    }

    private static long Px(decimal p) => (long)(p * 10_000m);
}
