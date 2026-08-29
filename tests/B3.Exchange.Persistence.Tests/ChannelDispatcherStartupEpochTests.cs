using System.Runtime.InteropServices;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging.Abstractions;
using OrderType = B3.Exchange.Matching.OrderType;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

public sealed class ChannelDispatcherStartupEpochTests
{
    private const byte Channel = 84;
    private const long Petr = 900_000_000_001L;
    private const long Vale = 900_000_000_002L;
    private const int SnapshotFrameOffset = WireOffsets.PacketHeaderSize
        + WireOffsets.FramingHeaderSize
        + WireOffsets.SbeMessageHeaderSize;

    private static Instrument Instrument(long securityId, string symbol) => new()
    {
        Symbol = symbol,
        SecurityId = securityId,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = $"BR{symbol}TEST0",
        SecurityType = "EQUITY",
    };

    private static long Px(decimal value) => (long)(value * 10_000m);

    private sealed class RecordingPersister(ChannelStateSnapshot initial) : IChannelStatePersister
    {
        private readonly object _gate = new();
        private ChannelStateSnapshot _last = initial;

        public List<string> Events { get; } = new();
        public int SaveCount { get; private set; }

        public ChannelStateSnapshot? TryLoad(byte channelNumber)
        {
            lock (_gate) return _last;
        }

        public long Save(ChannelStateSnapshot snapshot)
        {
            lock (_gate)
            {
                SaveCount++;
                _last = snapshot;
                Events.Add($"save:{snapshot.SequenceVersion}:{snapshot.SequenceNumber}");
                return 1;
            }
        }

        public ChannelStateSnapshot Last
        {
            get
            {
                lock (_gate) return _last;
            }
        }
    }

    private sealed class FailingSavePersister(ChannelStateSnapshot initial) : IChannelStatePersister
    {
        public ChannelStateSnapshot? TryLoad(byte channelNumber) => initial;

        public long Save(ChannelStateSnapshot snapshot)
            => throw new IOException("simulated durable epoch save failure");
    }

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        private readonly Action<ushort, uint>? _beforeRecord;

        public RecordingPacketSink(Action<ushort, uint>? beforeRecord = null)
            => _beforeRecord = beforeRecord;

        public List<byte[]> Packets { get; } = new();

        // Issue #596: the dispatch loop publishes packets from its own
        // dedicated thread while tests poll for them from the xUnit test
        // thread via WaitFor. Reading `Packets.Count` unsynchronized is a
        // genuine data race — nothing forces the polling thread to observe
        // the writer's mutations, so a sufficiently aggressive (Release-mode)
        // JIT is free to cache/hoist the stale value for the lifetime of the
        // poll loop, making the 5s deadline expire on a value that was never
        // re-read. Debug's weaker optimizer happened to reload it, masking
        // the bug there. Route the polled count through the same lock the
        // writer takes so the read has a real happens-before edge.
        public int Count { get { lock (Packets) return Packets.Count; } }

        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            ushort version = MemoryMarshal.Read<ushort>(
                packet.Slice(WireOffsets.PacketHeaderSequenceVersionOffset, 2));
            uint sequence = MemoryMarshal.Read<uint>(
                packet.Slice(WireOffsets.PacketHeaderSequenceNumberOffset, 4));
            _beforeRecord?.Invoke(version, sequence);
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    private sealed class RecordingOutbound : ICoreOutbound
    {
        public List<OrderAcceptedEvent> Accepted { get; } = new();

        public bool WriteExecutionReportNew(SessionId session, uint enteringFirm, ulong clOrdIdValue,
            in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue,
            DurabilityHandle durability = default)
        {
            Accepted.Add(e);
            return true;
        }

        public bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor,
            long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty,
            DurabilityHandle durability = default) => true;

        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId,
            long restingOrderId, in TradeEvent e, long leavesQty, long cumQty,
            DurabilityHandle durability = default) => true;

        public OrderedStreamWriteResult WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId,
            long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero,
            ulong receivedTimeNanos = ulong.MaxValue,
            DurabilityHandle durability = default) => OrderedStreamWriteResult.CommittedAndEnqueued;

        public bool WriteExecutionReportModify(SessionId session, long securityId, long orderId,
            ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa,
            long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
            ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle durability = default,
            InvestorId? investorId = null) => true;

        public bool WriteExecutionReportReject(SessionId session, in RejectEvent e,
            ulong clOrdIdValue, DurabilityHandle durability = default) => true;
    }

    private sealed class InMemoryWal(params WalRecord[] initial) : IChannelWriteAheadLog
    {
        private readonly List<WalRecord> _records = [.. initial];

        public int Append(WalRecord record)
        {
            _records.Add(record);
            return 1;
        }

        public IReadOnlyList<WalRecord> ReadAll() => _records.ToArray();

        public void Truncate() => _records.Clear();

        public void TruncateThrough(long throughSeq)
            => _records.RemoveAll(record => record.Seq <= throughSeq);

        public long PendingDurableSeqOrZero => _records.Count == 0 ? 0 : _records[^1].Seq;

        public void WaitForDurable(long seq, CancellationToken cancellationToken = default) { }
    }

    private sealed class CorruptWal : IChannelWriteAheadLog
    {
        public int Append(WalRecord record) => throw new NotSupportedException();

        public IReadOnlyList<WalRecord> ReadAll()
            => throw new WalCorruptionException(Channel, 1, "simulated WAL corruption");

        public void Truncate() { }

        public void TruncateThrough(long throughSeq) { }

        public long PendingDurableSeqOrZero => 0;

        public void WaitForDurable(long seq, CancellationToken cancellationToken = default) { }
    }

    private static ChannelStateSnapshot RichRestoredSnapshot(ushort version = 9)
    {
        var bid = new RestingOrderRecord(
            OrderId: 1, ClOrdId: "BID-1", Side: Side.Buy,
            PriceMantissa: Px(10.00m), RemainingQuantity: 200,
            EnteringFirm: 101, InsertTimestampNanos: 1_000,
            Tif: TimeInForce.Day, MaxFloor: 0, HiddenQuantity: 0);
        var offer = new RestingOrderRecord(
            OrderId: 2, ClOrdId: "OFFER-1", Side: Side.Sell,
            PriceMantissa: Px(11.00m), RemainingQuantity: 300,
            EnteringFirm: 202, InsertTimestampNanos: 2_000,
            Tif: TimeInForce.Gtc, MaxFloor: 0, HiddenQuantity: 0);
        var stop = new RestingStopRecord(
            OrderId: 3, ClOrdId: "STOP-1", SecurityId: Petr, Side: Side.Buy,
            StopType: OrderType.StopLimit, Tif: TimeInForce.Day,
            StopPxMantissa: Px(12.00m), LimitPriceMantissa: Px(12.10m),
            Quantity: 400, EnteringFirm: 303, EnteredAtNanos: 3_000);

        var engine = new EngineStateSnapshot(
            NextOrderId: 4,
            NextTradeId: 17,
            RptSeqBySecurity:
            [
                new EngineStateSnapshot.RptSeqEntry(Petr, 29),
                new EngineStateSnapshot.RptSeqEntry(Vale, 29),
            ],
            Phases:
            [
                new EngineStateSnapshot.PhaseEntry(Petr, TradingPhase.Open),
                new EngineStateSnapshot.PhaseEntry(Vale, TradingPhase.Pause),
            ],
            Books:
            [
                new EngineStateSnapshot.BookSnapshot(Petr, [bid, offer]),
                new EngineStateSnapshot.BookSnapshot(Vale, []),
            ],
            Stops: [stop],
            Halts:
            [
                new EngineStateSnapshot.HaltEntry(
                    Vale, (byte)HaltReason.RegulatoryHalt, 4_000, "restored halt"),
            ]);

        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: Channel,
            SequenceNumber: 77,
            SequenceVersion: version,
            Engine: engine,
            Owners:
            [
                new OrderOwnerSnapshot(1, "10001", 101, 0xA001, Side.Buy, Petr)
                    { OriginalQty = 200 },
                new OrderOwnerSnapshot(2, "20002", 202, 0xA002, Side.Sell, Petr)
                    { OriginalQty = 300 },
                new OrderOwnerSnapshot(3, "30003", 303, 0xA003, Side.Buy, Petr)
                    { OriginalQty = 400 },
            ])
        {
            LastAppliedSeq = 3,
        };
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelStatePersister persister,
        IUmdfPacketSink incrementalSink,
        RecordingOutbound outbound,
        IChannelWriteAheadLog? wal,
        out MatchingEngine engine)
    {
        MatchingEngine? captured = null;
        var dispatcher = new ChannelDispatcher(
            channelNumber: Channel,
            engineFactory: sink =>
            {
                captured = new MatchingEngine(
                    [Instrument(Petr, "PETR4"), Instrument(Vale, "VALE3")],
                    sink,
                    NullLogger<MatchingEngine>.Instance);
                return captured;
            },
            options: new ChannelDispatcherOptions
            {
                PacketSink = incrementalSink,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Persister = persister,
                Wal = wal,
                SeedSecurityIds = [Petr, Vale],
            });
        engine = captured!;
        return dispatcher;
    }

    [Fact]
    public async Task RestoreReplay_StartsDurableEpoch_PreservesState_AndOrdersResetSnapshotLive()
    {
        var initial = RichRestoredSnapshot();
        var persister = new RecordingPersister(initial);
        var replayOrder = new NewOrderCommand(
            "WAL-BID", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
            Px(9.50m), 100, 404, 5_000);
        var wal = new InMemoryWal(new WalRecord(
            Seq: 4, Kind: WalRecordKind.NewOrder, SessionValue: "40004",
            Firm: 404, ClOrdId: 0xA004, OrigClOrdId: 0,
            NewOrder: replayOrder, Cancel: null, Replace: null));
        var incremental = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var dispatcher = BuildDispatcher(
            persister, incremental, outbound, wal, out var engine);
        var snapshotSink = new RecordingPacketSink();
        dispatcher.AttachSnapshotRotator(new SnapshotRotator(
            Channel,
            new MatchingEngineSnapshotSource(engine, [Petr, Vale]),
            snapshotSink));

        dispatcher.PrepareStartup();

        Assert.Equal((ushort)10, dispatcher.SequenceVersion);
        Assert.Equal(0u, dispatcher.SequenceNumber);
        Assert.Empty(incremental.Packets);
        Assert.Empty(snapshotSink.Packets);

        var prepared = persister.Last;
        Assert.Equal((ushort)10, prepared.SequenceVersion);
        Assert.Equal(0u, prepared.SequenceNumber);
        Assert.Equal(4L, prepared.LastAppliedSeq);
        Assert.Equal(5L, prepared.Engine.NextOrderId);
        Assert.Equal(17u, prepared.Engine.NextTradeId);
        Assert.Equal(30u, prepared.Engine.RptSeqBySecurity.Single(entry => entry.SecurityId == Petr).RptSeq);
        Assert.Equal(29u, prepared.Engine.RptSeqBySecurity.Single(entry => entry.SecurityId == Vale).RptSeq);
        Assert.Equal(3, prepared.Engine.Books.Single(book => book.SecurityId == Petr).Orders.Count);
        Assert.Single(prepared.Engine.Stops!);
        Assert.Equal(4, prepared.Owners.Count);
        Assert.Contains(prepared.Engine.Phases,
            phase => phase.SecurityId == Vale && phase.Phase == TradingPhase.Pause);
        Assert.Contains(prepared.Engine.Halts!,
            halt => halt.SecurityId == Vale
                && halt.Reason == (byte)HaltReason.RegulatoryHalt
                && halt.Note == "restored halt");

        dispatcher.Activate();

        Assert.Equal(1u, dispatcher.SequenceNumber);
        Assert.Single(incremental.Packets);
        AssertChannelReset(incremental.Packets[0], expectedVersion: 10);

        Assert.True(dispatcher.TryResolveByClOrdId(101, 0xA001, out var bidOrderId, out _));
        Assert.Equal(1L, bidOrderId);
        Assert.True(dispatcher.TryResolveByClOrdId(303, 0xA003, out var stopOrderId, out _));
        Assert.Equal(3L, stopOrderId);
        Assert.True(dispatcher.TryResolveByClOrdId(404, 0xA004, out var replayOrderId, out _));
        Assert.Equal(4L, replayOrderId);
        Assert.True(dispatcher.TryGetPhaseSnapshot(Vale, out var restoredPhase));
        Assert.Equal(TradingPhase.Pause, restoredPhase);
        Assert.True(dispatcher.TryGetHaltSnapshot(Vale, out var restoredHalt));
        Assert.Equal(HaltReason.RegulatoryHalt, restoredHalt.Reason);

        Assert.True(dispatcher.EnqueueSnapshotTick());
        // Snapshot rotation now emits the book snapshot packet followed by a
        // standalone SecurityStatus_3 recovery packet for the instrument
        // (issue #583), so a single tick yields 2 packets, not 1.
        Assert.True(WaitFor(() => snapshotSink.Count == 2));
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            snapshotSink.Packets[0].AsSpan(
                SnapshotFrameOffset, WireOffsets.SnapHeaderBlockLength),
            out var snapshotHeader));
        Assert.Equal(3u, snapshotHeader.Data.TotNumReports);
        Assert.Equal(2u, snapshotHeader.Data.TotNumBids);
        Assert.Equal(1u, snapshotHeader.Data.TotNumOffers);
        Assert.Equal(30u, snapshotHeader.Data.LastRptSeq);
        Assert.Equal((ushort)10, snapshotHeader.Data.LastSequenceVersion);

        Assert.True(dispatcher.EnqueueNewOrder(
            new NewOrderCommand(
                "LIVE-BID", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                Px(9.00m), 100, 505, 6_000),
            new SessionId("50005"), enteringFirm: 505, clOrdIdValue: 0xA005));
        Assert.True(WaitFor(() => incremental.Count == 2));

        Assert.Equal(5L, Assert.Single(outbound.Accepted).OrderId);
        AssertPacketHeader(incremental.Packets[1], expectedVersion: 10, expectedSequence: 2);
        Assert.Equal((ushort)10, persister.Last.SequenceVersion);
        Assert.Equal(2u, persister.Last.SequenceNumber);

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task PersistFailure_PreventsResetPublicationAndLiveProcessing()
    {
        var sink = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var dispatcher = BuildDispatcher(
            new FailingSavePersister(RichRestoredSnapshot()),
            sink,
            outbound,
            wal: null,
            out _);
        Assert.True(dispatcher.EnqueueNewOrder(
            new NewOrderCommand(
                "QUEUED-LIVE", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                Px(9.00m), 100, 505, 6_000),
            new SessionId("50005"), enteringFirm: 505, clOrdIdValue: 0xB005));

        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.Start());

        Assert.Contains("failed to durably prepare startup UMDF epoch", error.Message);
        Assert.Empty(sink.Packets);
        Assert.Empty(outbound.Accepted);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task WalReadFailure_AbortsStartupWithoutSavingResetOrTruncatingTail()
    {
        string dataDirectory = Path.Combine(
            AppContext.BaseDirectory,
            $"startup-wal-read-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var replayOrder = new NewOrderCommand(
                "WAL-BID", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                Px(9.50m), 100, 404, 5_000);
            var tail = new WalRecord(
                Seq: 4, Kind: WalRecordKind.NewOrder, SessionValue: "40004",
                Firm: 404, ClOrdId: 0xA004, OrigClOrdId: 0,
                NewOrder: replayOrder, Cancel: null, Replace: null);
            using (var seedWal = new FileChannelWriteAheadLog(
                dataDirectory,
                Channel,
                NullLogger<FileChannelWriteAheadLog>.Instance))
            {
                seedWal.Append(tail);
            }

            string walPath = Path.Combine(dataDirectory, $"channel-{Channel}.wal");
            byte[] tailBefore = File.ReadAllBytes(walPath);
            var initial = RichRestoredSnapshot();
            var persister = new RecordingPersister(initial);
            var sink = new RecordingPacketSink();
            var wal = new FileChannelWriteAheadLog(
                dataDirectory,
                Channel,
                NullLogger<FileChannelWriteAheadLog>.Instance);
            var dispatcher = BuildDispatcher(
                persister, sink, new RecordingOutbound(), wal, out _);

            using (new FileStream(
                walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Assert.Throws<IOException>(() => dispatcher.Start());

                Assert.Contains("failed to read WAL during boot recovery", error.Message);
                Assert.False(dispatcher.IsWalHealthy);
                Assert.Equal((ushort)9, dispatcher.SequenceVersion);
                Assert.Equal(77u, dispatcher.SequenceNumber);
                Assert.Equal(0, persister.SaveCount);
                Assert.Equal(initial, persister.Last);
                Assert.Empty(sink.Packets);
                await dispatcher.DisposeAsync();
            }

            Assert.Equal(tailBefore, File.ReadAllBytes(walPath));
            using var verifyWal = new FileChannelWriteAheadLog(
                dataDirectory,
                Channel,
                NullLogger<FileChannelWriteAheadLog>.Instance);
            Assert.Equal(4L, Assert.Single(verifyWal.ReadAll()).Seq);
        }
        finally
        {
            try { Directory.Delete(dataDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WalCorruption_FaultsStartupBeforeReadinessResetSnapshotOrLiveTraffic()
    {
        var initial = RichRestoredSnapshot();
        var persister = new RecordingPersister(initial);
        var incremental = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var dispatcher = BuildDispatcher(
            persister, incremental, outbound, new CorruptWal(), out var engine);
        var snapshot = new RecordingPacketSink();
        dispatcher.AttachSnapshotRotator(new SnapshotRotator(
            Channel,
            new MatchingEngineSnapshotSource(engine, [Petr, Vale]),
            snapshot));
        Assert.True(dispatcher.EnqueueSnapshotTick());
        Assert.True(dispatcher.EnqueueNewOrder(
            new NewOrderCommand(
                "QUEUED-LIVE", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                Px(9.00m), 100, 505, 6_000),
            new SessionId("50005"), enteringFirm: 505, clOrdIdValue: 0xC005));

        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.Start());

        Assert.Contains("WAL replay failed during boot recovery", error.Message);
        Assert.False(dispatcher.IsWalHealthy);
        Assert.False(new WalHaltReadinessProbe([dispatcher]).IsReady);
        Assert.Equal((ushort)9, dispatcher.SequenceVersion);
        Assert.Equal(77u, dispatcher.SequenceNumber);
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(initial, persister.Last);
        Assert.Empty(incremental.Packets);
        Assert.Empty(snapshot.Packets);
        Assert.Empty(outbound.Accepted);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task WalBoundaryGap_FaultsStartupBeforeReadinessResetSnapshotOrLiveTraffic()
    {
        var initial = RichRestoredSnapshot();
        var gapRecord = new WalRecord(
            Seq: initial.LastAppliedSeq + 2,
            Kind: WalRecordKind.NewOrder,
            SessionValue: "40004",
            Firm: 404,
            ClOrdId: 0xA004,
            OrigClOrdId: 0,
            NewOrder: new NewOrderCommand(
                "WAL-GAP", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                Px(9.50m), 100, 404, 5_000),
            Cancel: null,
            Replace: null);
        var persister = new RecordingPersister(initial);
        var incremental = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var dispatcher = BuildDispatcher(
            persister, incremental, outbound, new InMemoryWal(gapRecord), out var engine);
        var snapshot = new RecordingPacketSink();
        dispatcher.AttachSnapshotRotator(new SnapshotRotator(
            Channel,
            new MatchingEngineSnapshotSource(engine, [Petr, Vale]),
            snapshot));
        Assert.True(dispatcher.EnqueueSnapshotTick());
        Assert.True(dispatcher.EnqueueNewOrder(
            new NewOrderCommand(
                "QUEUED-LIVE", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                Px(9.00m), 100, 505, 6_000),
            new SessionId("50005"), enteringFirm: 505, clOrdIdValue: 0xD005));

        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.Start());

        Assert.Contains("WAL replay failed during boot recovery", error.Message);
        Assert.False(dispatcher.IsWalHealthy);
        Assert.False(new WalHaltReadinessProbe([dispatcher]).IsReady);
        Assert.Equal((ushort)9, dispatcher.SequenceVersion);
        Assert.Equal(77u, dispatcher.SequenceNumber);
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(initial, persister.Last);
        Assert.Empty(incremental.Packets);
        Assert.Empty(snapshot.Packets);
        Assert.Empty(outbound.Accepted);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task CrashBeforeResetPublication_NextStartupSkipsPreparedVersion()
    {
        var persister = new RecordingPersister(RichRestoredSnapshot(version: 7));
        var events = persister.Events;
        var throwingSink = new RecordingPacketSink((version, sequence) =>
        {
            events.Add($"publish:{version}:{sequence}");
            throw new IOException("simulated crash before reset publication");
        });
        var first = BuildDispatcher(
            persister, throwingSink, new RecordingOutbound(), wal: null, out _);

        Assert.Throws<IOException>(() => first.Start());
        Assert.Equal((ushort)8, persister.Last.SequenceVersion);
        Assert.Equal(0u, persister.Last.SequenceNumber);
        Assert.Equal(["save:8:0", "publish:8:1"], events);
        await first.DisposeAsync();

        var secondSink = new RecordingPacketSink();
        var second = BuildDispatcher(
            persister, secondSink, new RecordingOutbound(), wal: null, out _);
        second.Start();

        Assert.Equal((ushort)9, second.SequenceVersion);
        AssertChannelReset(Assert.Single(secondSink.Packets), expectedVersion: 9);
        Assert.Equal((ushort)9, persister.Last.SequenceVersion);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task CrashAfterResetPublication_NextStartupUsesStrictlyNewerVersion()
    {
        var persister = new RecordingPersister(RichRestoredSnapshot(version: 20));
        var firstSink = new RecordingPacketSink();
        var first = BuildDispatcher(
            persister, firstSink, new RecordingOutbound(), wal: null, out _);
        first.Start();

        AssertChannelReset(Assert.Single(firstSink.Packets), expectedVersion: 21);
        Assert.Equal((ushort)21, persister.Last.SequenceVersion);
        first.CreateTestProbe().Kill();
        await first.DisposeAsync();

        var secondSink = new RecordingPacketSink();
        var second = BuildDispatcher(
            persister, secondSink, new RecordingOutbound(), wal: null, out _);
        second.Start();

        AssertChannelReset(Assert.Single(secondSink.Packets), expectedVersion: 22);
        Assert.Equal((ushort)22, persister.Last.SequenceVersion);
        await second.DisposeAsync();
    }

    [Theory]
    [InlineData((ushort)65534)]
    [InlineData(ushort.MaxValue)]
    public async Task StartupEpoch_TerminalVersionFailsBeforePersistenceOrReset(ushort version)
    {
        var initial = RichRestoredSnapshot(version);
        var persister = new RecordingPersister(initial);
        var sink = new RecordingPacketSink();
        var dispatcher = BuildDispatcher(
            persister, sink, new RecordingOutbound(), wal: null, out _);

        var error = Assert.Throws<InvalidOperationException>(() => dispatcher.Start());

        Assert.Contains("SequenceVersion space is exhausted", error.Message);
        Assert.Equal(version, dispatcher.SequenceVersion);
        Assert.Equal(77u, dispatcher.SequenceNumber);
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(initial, persister.Last);
        Assert.Empty(sink.Packets);
        await dispatcher.DisposeAsync();
    }

    [Theory]
    [InlineData((ushort)65534)]
    [InlineData(ushort.MaxValue)]
    public void OperatorBump_TerminalVersionPreservesStateWithoutPersistenceOrReset(ushort version)
    {
        var initial = RichRestoredSnapshot(version);
        var persister = new RecordingPersister(initial);
        var sink = new RecordingPacketSink();
        var dispatcher = BuildDispatcher(
            persister, sink, new RecordingOutbound(), wal: null, out _);
        dispatcher.RestoreChannelState(initial);

        Assert.True(dispatcher.EnqueueOperatorBumpVersion());
        var error = Assert.Throws<InvalidOperationException>(
            () => dispatcher.CreateTestProbe().DrainInbound());

        Assert.Contains("SequenceVersion space is exhausted", error.Message);
        Assert.Equal(version, dispatcher.SequenceVersion);
        Assert.Equal(77u, dispatcher.SequenceNumber);
        Assert.Equal(0, persister.SaveCount);
        Assert.Empty(sink.Packets);
        Assert.True(dispatcher.TryResolveByClOrdId(101, 0xA001, out var orderId, out _));
        Assert.Equal(1L, orderId);
    }

    private static void AssertChannelReset(byte[] packet, ushort expectedVersion)
    {
        AssertPacketHeader(packet, expectedVersion, expectedSequence: 1);
        int sbeHeaderOffset = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize;
        ushort templateId = MemoryMarshal.Read<ushort>(
            packet.AsSpan(sbeHeaderOffset + 2, 2));
        Assert.Equal((ushort)11, templateId);
    }

    private static void AssertPacketHeader(
        byte[] packet, ushort expectedVersion, uint expectedSequence)
    {
        Assert.Equal(expectedVersion, MemoryMarshal.Read<ushort>(
            packet.AsSpan(WireOffsets.PacketHeaderSequenceVersionOffset, 2)));
        Assert.Equal(expectedSequence, MemoryMarshal.Read<uint>(
            packet.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4)));
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }
}
