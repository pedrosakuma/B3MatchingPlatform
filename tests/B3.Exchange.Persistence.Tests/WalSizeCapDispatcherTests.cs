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
/// Issue #291: dispatcher behaviour when the WAL throws
/// <see cref="WalSizeCapExceededException"/>. The cap-breach
/// halt is sticky and ALWAYS overrides
/// <see cref="WalAppendFailurePolicy"/> — even
/// <see cref="WalAppendFailurePolicy.Continue"/> must not be
/// allowed to silently degrade to a non-durable execution
/// once the operator has explicitly opted into a hard cap.
/// </summary>
public class WalSizeCapDispatcherTests
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

    private sealed class NoOpOutbound : ICoreOutbound
    {
        public int NewCount;
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue, DurabilityHandle d = default)
        { NewCount++; return true; }
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c, DurabilityHandle d = default) => true;
    }

    /// <summary>
    /// First call succeeds, every subsequent call throws
    /// <see cref="WalSizeCapExceededException"/>.
    /// </summary>
    private sealed class CapExceededAfterNWal : IChannelWriteAheadLog
    {
        private readonly int _allowedAppends;
        public int AppendCalls;
        public long CurrentSizeBytes => _allowedAppends * 64L;
        public long DropsOnFullCount => 0;

        public CapExceededAfterNWal(int allowedAppends) { _allowedAppends = allowedAppends; }
        public int Append(WalRecord record)
        {
            AppendCalls++;
            if (AppendCalls > _allowedAppends)
                throw new WalSizeCapExceededException(
                    currentSizeBytes: CurrentSizeBytes,
                    maxBytes: CurrentSizeBytes,
                    incomingRecordBytes: 64);
            return 64;
        }
        public IReadOnlyList<WalRecord> ReadAll() => Array.Empty<WalRecord>();
        public void Truncate() { }
        public void Dispose() { }
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelWriteAheadLog wal,
        WalAppendFailurePolicy appendPolicy,
        out NoOpOutbound outbound,
        ChannelMetrics? metrics = null)
    {
        var sink = new NoOpPacketSink();
        var localOutbound = outbound = new NoOpOutbound();
        return new ChannelDispatcher(
            channelNumber: 91,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            packetSink: sink,
            outbound: localOutbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            metrics: metrics,
            wal: wal,
            walAppendFailurePolicy: appendPolicy);
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

    [Theory]
    [InlineData(WalAppendFailurePolicy.Continue)]
    [InlineData(WalAppendFailurePolicy.Halt)]
    public async Task SizeCap_AlwaysHalts_RegardlessOfAppendFailurePolicy(WalAppendFailurePolicy appendPolicy)
    {
        var wal = new CapExceededAfterNWal(allowedAppends: 1);
        var metrics = new ChannelMetrics(91);
        var disp = BuildDispatcher(wal, appendPolicy, out var outbound, metrics);
        try
        {
            disp.Start();

            // First order: succeeds (under the cap).
            Assert.True(EnqueueOrder(disp, clOrdIdValue: 1));
            await WaitForAsync(() => outbound.NewCount >= 1);
            Assert.True(disp.IsWalHealthy);

            // Second order: WAL throws WalSizeCapExceededException.
            // Regardless of appendPolicy (Continue or Halt) the
            // dispatcher MUST halt the channel — silent degradation
            // is disallowed when the operator has opted into a cap.
            Assert.True(EnqueueOrder(disp, clOrdIdValue: 2));
            await WaitForAsync(() => !disp.IsWalHealthy);

            Assert.False(disp.IsWalHealthy);
            Assert.Equal(1, outbound.NewCount); // second order NOT executed
            Assert.Equal(1, metrics.WalAppendFailures);

            // Subsequent enqueues are rejected producer-side.
            Assert.False(EnqueueOrder(disp, clOrdIdValue: 3));
            Assert.True(metrics.WalHaltRejects >= 1);
        }
        finally
        {
            await disp.DisposeAsync();
        }
    }
}
