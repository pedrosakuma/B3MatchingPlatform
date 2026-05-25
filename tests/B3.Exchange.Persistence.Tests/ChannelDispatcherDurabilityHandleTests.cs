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
/// Issue #312: end-to-end gating contract — when the channel has a
/// WAL configured the <see cref="ChannelDispatcher"/> must hand
/// every <c>WriteExecutionReportXxx</c> a
/// <see cref="DurabilityHandle"/> tied to the WAL barrier and the
/// command's seq, so the gateway send loop blocks the bytes until
/// fsync. When no WAL is configured the dispatcher must hand
/// <see cref="DurabilityHandle.None"/> and the legacy fast path
/// stays unaffected.
/// </summary>
public class ChannelDispatcherDurabilityHandleTests
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

    private sealed class CapturingOutbound : ICoreOutbound
    {
        public List<DurabilityHandle> NewDurabilities { get; } = new();
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue, DurabilityHandle d = default)
        { NewDurabilities.Add(d); return true; }
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue, DurabilityHandle d = default, InvestorId? iv = null) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c, DurabilityHandle d = default) => true;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wal-dh-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void WalEnabled_PassesActiveDurabilityHandle_ToOutboundEr()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 84,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: false, maxBytes: 0,
            onFull: WalSizeCapPolicy.Halt,
            fsyncMode: WalFsyncMode.GroupCommit,
            groupCommitInterval: TimeSpan.FromMilliseconds(1));
        var outbound = new CapturingOutbound();
        var disp = new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Wal = wal,
            });
        var probe = disp.CreateTestProbe();

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-1", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1_000UL),
            new SessionId("S"), enteringFirm: 700, clOrdIdValue: 1));
        probe.DrainInbound();

        var handle = Assert.Single(outbound.NewDurabilities);
        Assert.True(handle.IsActive,
            "ChannelDispatcher with WAL must propagate an active DurabilityHandle so the gateway can fsync-gate the ER");
        Assert.NotNull(handle.Barrier);
        Assert.Equal(1L, handle.Seq);
    }

    [Fact]
    public void WalDisabled_PassesNoneDurabilityHandle_ToOutboundEr()
    {
        var outbound = new CapturingOutbound();
        var disp = new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Wal = null,
            });
        var probe = disp.CreateTestProbe();

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-1", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1_000UL),
            new SessionId("S"), enteringFirm: 700, clOrdIdValue: 1));
        probe.DrainInbound();

        var handle = Assert.Single(outbound.NewDurabilities);
        Assert.False(handle.IsActive,
            "no-WAL channel must hand DurabilityHandle.None so the gateway send loop fast-paths through");
        Assert.Equal(DurabilityHandle.None, handle);
    }
}
