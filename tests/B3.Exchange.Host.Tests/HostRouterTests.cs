using B3.Exchange.EntryPoint;
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

    private sealed class RecordingReply : IGatewayResponseChannel
    {
        public long ConnectionId => 1;
        public uint EnteringFirm => 1;
        public bool IsOpen => true;
        public List<RejectEvent> Rejects { get; } = new();
        public bool WriteExecutionReportNew(in OrderAcceptedEvent e) => true;
        public bool WriteExecutionReportTrade(in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool WriteExecutionReportModify(long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq) => true;
        public bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue) { Rejects.Add(e); return true; }
        public bool WriteSessionReject(byte terminationCode) => true;
        public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId, uint businessRejectReason, string? text = null) => true;
    }

    [Fact]
    public void UnknownInstrument_RoutesToInlineRejectWithoutDispatcher()
    {
        var routing = new Dictionary<long, ChannelDispatcher>(); // empty
        var router = new HostRouter(routing, NullLogger<HostRouter>.Instance, () => 1_000UL);
        var reply = new RecordingReply();
        router.EnqueueNewOrder(
            new NewOrderCommand("1", SecurityId: 12345, Side.Buy, OrderType.Limit, TimeInForce.Day, 100, 100, 1, 0),
            reply, clOrdIdValue: 1);
        var rej = Assert.Single(reply.Rejects);
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
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => new MatchingEngine(new[] { inst }, s, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            logger: NullLogger<ChannelDispatcher>.Instance,
            nowNanos: () => 1_000UL);
        // Dispatcher loop not started; we read inbound queue directly.
        var router = new HostRouter(new Dictionary<long, ChannelDispatcher> { [42] = disp }, NullLogger<HostRouter>.Instance);
        var reply = new RecordingReply();

        router.EnqueueNewOrder(
            new NewOrderCommand("1", SecurityId: 42, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 1, 0),
            reply, clOrdIdValue: 1);

        Assert.Empty(reply.Rejects);
        // Drain the dispatcher queue + assert it actually processed an order
        var field = typeof(ChannelDispatcher).GetField("_inbound", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var inbound = field.GetValue(disp)!;
        var reader = inbound.GetType().GetProperty("Reader")!.GetValue(inbound)!;
        var tryRead = reader.GetType().GetMethod("TryRead")!;
        var args = new object?[] { null };
        var processOne = typeof(ChannelDispatcher).GetMethod("ProcessOne", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True((bool)tryRead.Invoke(reader, args)!);
        processOne.Invoke(disp, new[] { args[0] });
        Assert.Single(pkt.Calls);
    }

    private static long Px(decimal p) => (long)(p * 10_000m);
}
