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
/// Issue #286: <see cref="WalAppendFailurePolicy"/> end-to-end
/// behaviour. <c>Continue</c> (the historical default) lets the
/// command run despite a WAL append failure; <c>Halt</c> refuses
/// the command, marks the channel unhealthy, and short-circuits
/// every subsequent producer-side enqueue.
/// </summary>
public class WalAppendFailurePolicyTests
{
    private const long Sec = 900_000_000_002L;

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

    private sealed class CountingOutbound : ICoreOutbound
    {
        public int NewCount;
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue)
        { NewCount++; return true; }
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c) => true;
    }

    private sealed class FailingWal : IChannelWriteAheadLog
    {
        public int AppendCalls;
        public void Append(WalRecord record)
        {
            AppendCalls++;
            throw new IOException("simulated disk failure");
        }
        public IReadOnlyList<WalRecord> ReadAll() => Array.Empty<WalRecord>();
        public void Truncate() { }
        public void Dispose() { }
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelWriteAheadLog wal,
        WalAppendFailurePolicy policy,
        out CountingOutbound outbound,
        ChannelMetrics? metrics = null)
    {
        var sink = new NoOpPacketSink();
        var localOutbound = outbound = new CountingOutbound();
        return new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            packetSink: sink,
            outbound: localOutbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            metrics: metrics,
            wal: wal,
            walAppendFailurePolicy: policy);
    }

    private static bool EnqueueOrder(ChannelDispatcher disp, ulong clOrdIdValue)
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdIdValue.ToString(), Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 700u, EnteredAtNanos: 0),
            new SessionId("S1"), enteringFirm: 700, clOrdIdValue: clOrdIdValue);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Continue_OnWalFailure_RunsCommand_AndStaysHealthy()
    {
        var wal = new FailingWal();
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(wal, WalAppendFailurePolicy.Continue, out var outbound, metrics);
        try
        {
            disp.Start();
            Assert.True(EnqueueOrder(disp, clOrdIdValue: 1));
            await WaitForAsync(() => wal.AppendCalls >= 1 && outbound.NewCount >= 1);

            Assert.Equal(1, wal.AppendCalls);
            Assert.Equal(1, outbound.NewCount);
            Assert.Equal(1, metrics.WalAppendFailures);
            Assert.Equal(0, metrics.WalHaltRejects);
            Assert.True(disp.IsWalHealthy);
        }
        finally
        {
            await disp.DisposeAsync();
        }
    }

    [Fact]
    public async Task Halt_OnWalFailure_RefusesCommand_FlipsHealthAndShortCircuits()
    {
        var wal = new FailingWal();
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(wal, WalAppendFailurePolicy.Halt, out var outbound, metrics);
        try
        {
            disp.Start();

            Assert.True(EnqueueOrder(disp, clOrdIdValue: 1));
            await WaitForAsync(() => !disp.IsWalHealthy);

            Assert.Equal(1, wal.AppendCalls);
            Assert.Equal(0, outbound.NewCount); // engine was NOT touched
            Assert.False(disp.IsWalHealthy);
            Assert.Equal(1, metrics.WalAppendFailures);
            Assert.True(metrics.WalHaltRejects >= 1);

            // Subsequent enqueues are rejected at the producer side
            // (no work item posted, no Append call).
            Assert.False(EnqueueOrder(disp, clOrdIdValue: 2));
            Assert.False(EnqueueOrder(disp, clOrdIdValue: 3));
            Assert.Equal(1, wal.AppendCalls); // still only the first one
            Assert.True(metrics.WalHaltRejects >= 3);
        }
        finally
        {
            await disp.DisposeAsync();
        }
    }

    [Fact]
    public async Task WalHaltReadinessProbe_Reports_NotReady_When_AnyChannelHalted()
    {
        var wal = new FailingWal();
        var disp = BuildDispatcher(wal, WalAppendFailurePolicy.Halt, out _);
        try
        {
            var probe = new WalHaltReadinessProbe(new[] { disp });
            Assert.True(probe.IsReady);

            disp.Start();
            Assert.True(EnqueueOrder(disp, clOrdIdValue: 1));
            // Wait for the dispatch loop to consume the item and trip
            // the halt flag.
            for (int i = 0; i < 50 && disp.IsWalHealthy; i++)
            {
                await Task.Delay(20);
            }
            Assert.False(disp.IsWalHealthy);
            Assert.False(probe.IsReady);
        }
        finally
        {
            await disp.DisposeAsync();
        }
    }
}
