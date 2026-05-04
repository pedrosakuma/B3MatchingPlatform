using System.Diagnostics;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using MatchingSide = B3.Exchange.Matching.Side;
using MatchingRejectEvent = B3.Exchange.Matching.RejectEvent;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Issue #175: verifies the <c>B3.Exchange</c> ActivitySource emits the
/// expected spans across the dispatch boundary, including parent/child
/// linkage from the synthetic <c>gateway.decode</c> root through
/// <c>dispatch.enqueue</c> and <c>engine.process</c> to <c>outbound.emit</c>.
/// Uses an <see cref="ActivityListener"/> to subscribe to the source
/// without relying on an exporter.
/// </summary>
public class TracingTests
{
    private const long Petr = 900_000_000_001L;

    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Petr,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Add(packet.ToArray());
    }

    private sealed class NoopOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, MatchingSide side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(SessionId session, in MatchingRejectEvent e, ulong clOrdIdValue) => true;
    }

    private static ChannelDispatcher NewDispatcher() => new(
        channelNumber: 1,
        engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
        packetSink: new RecordingPacketSink(),
        outbound: new NoopOutbound(),
        logger: NullLogger<ChannelDispatcher>.Instance,
        nowNanos: () => 1_000_000_000UL, tradeDate: 19_000);

    private static ActivityListener Subscribe(List<Activity> sink) => new()
    {
        ShouldListenTo = src => src.Name == ExchangeTelemetry.SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = a => sink.Add(a),
    };

    [Fact]
    public void Dispatch_EmitsAllSpans_WithParentChildLinkage_Issue175()
    {
        var spans = new List<Activity>();
        using var listener = Subscribe(spans);
        ActivitySource.AddActivityListener(listener);

        var disp = NewDispatcher();
        var session = new SessionId("conn-1");

        ActivityTraceId expectedTraceId;

        // Synthesize the gateway.decode root span so we can verify the
        // full pipeline links back to it.
        using (var root = ExchangeTelemetry.Source.StartActivity(
            ExchangeTelemetry.SpanGatewayDecode, ActivityKind.Server))
        {
            Assert.NotNull(root);
            expectedTraceId = root.TraceId;
            disp.EnqueueNewOrder(
                new NewOrderCommand("1", Petr, MatchingSide.Buy, OrderType.Limit, TimeInForce.Day,
                    PriceMantissa: 100_000L, Quantity: 100, EnteringFirm: 7, EnteredAtNanos: 1_000UL),
                session, enteringFirm: 7, clOrdIdValue: 42UL);
        }

        // Drive the engine on the test thread.
        disp.CreateTestProbe().DrainInbound();

        // The xUnit collection runs tests in parallel, and the listener
        // fires for every B3.Exchange span in the process. Filter by the
        // synthesized root's TraceId so a concurrent test's spans cannot
        // contaminate the assertion set.
        var mine = spans.Where(s => s.TraceId == expectedTraceId).ToList();
        var byName = mine.GroupBy(s => s.OperationName).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Contains(ExchangeTelemetry.SpanGatewayDecode, byName);
        Assert.Contains(ExchangeTelemetry.SpanDispatchEnqueue, byName);
        Assert.Contains(ExchangeTelemetry.SpanEngineProcess, byName);
        Assert.Contains(ExchangeTelemetry.SpanOutboundEmit, byName);

        var decode = byName[ExchangeTelemetry.SpanGatewayDecode].Single();
        var enqueue = byName[ExchangeTelemetry.SpanDispatchEnqueue].Single();
        var engine = byName[ExchangeTelemetry.SpanEngineProcess].Single();
        var outbound = byName[ExchangeTelemetry.SpanOutboundEmit].Single();

        // All spans must share the same TraceId (W3C trace-context propagation).
        Assert.Equal(decode.TraceId, enqueue.TraceId);
        Assert.Equal(decode.TraceId, engine.TraceId);
        Assert.Equal(decode.TraceId, outbound.TraceId);

        // dispatch.enqueue is a direct child of gateway.decode (in-thread).
        Assert.Equal(decode.SpanId, enqueue.ParentSpanId);
        // engine.process is parented to dispatch.enqueue via the explicit
        // ActivityContext stamped on the WorkItem (cross-thread propagation).
        Assert.Equal(enqueue.SpanId, engine.ParentSpanId);
        // outbound.emit nests inside engine.process on the dispatch thread.
        Assert.Equal(engine.SpanId, outbound.ParentSpanId);

        // Tag spot-checks.
        Assert.Equal((long)7, enqueue.GetTagItem(ExchangeTelemetry.TagFirm));
        Assert.Equal("New", enqueue.GetTagItem(ExchangeTelemetry.TagWorkKind));
        Assert.Equal("conn-1", engine.GetTagItem(ExchangeTelemetry.TagSession));
    }

    [Fact]
    public void Dispatch_RunsClean_WhenInstrumentedPathHasNoSubscribers()
    {
        // Smoke test: even with no listener attached in this test, the
        // instrumented path must not throw. We deliberately do not assert
        // StartActivity returns null because xUnit can run other Tracing
        // tests in parallel and their listener would observe spans here
        // too. The contract under test is "instrumentation never breaks
        // dispatch", not the listener-list bookkeeping.
        var disp = NewDispatcher();

        disp.EnqueueNewOrder(
            new NewOrderCommand("1", Petr, MatchingSide.Buy, OrderType.Limit, TimeInForce.Day,
                PriceMantissa: 100_000L, Quantity: 100, EnteringFirm: 7, EnteredAtNanos: 1_000UL),
            new SessionId("conn-2"), enteringFirm: 7, clOrdIdValue: 1UL);
        disp.CreateTestProbe().DrainInbound();
    }
}
