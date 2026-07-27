using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using B3.Exchange.Gateway;
using B3.Exchange.Core;
using B3.Umdf.WireEncoder;
using B3.Exchange.TestSupport;
using B3.Umdf.Mbo.Sbe.V16;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// End-to-end happy-path: spin up <see cref="ExchangeHost"/> on loopback,
/// open a TCP client to the EntryPoint listener, send a SimpleNewOrderV2
/// frame, and read the ExecutionReport_New back. Then send a crossing
/// counter-order and read both ExecutionReport_New and ExecutionReport_Trade
/// for the aggressor side.
///
/// This is the only test that exercises:
///   TCP socket → EntryPointFrameReader → InboundMessageDecoder →
///   HostRouter → ChannelDispatcher → MatchingEngine → ExecutionReport
///   encode → TCP socket
///
/// Multicast publishing is left to <c>ChannelDispatcherTests</c> with a
/// recording packet sink — sniffing real multicast in CI is too fragile.
/// </summary>
public class ExchangeHostE2ETests
{
    private const long Petr = 900_000_000_001L;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public List<byte> Channels { get; } = new();

        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets)
            {
                Channels.Add(channelNumber);
                Packets.Add(packet.ToArray());
            }
        }

        public int Count
        {
            get
            {
                lock (Packets) return Packets.Count;
            }
        }

        public byte[][] SnapshotPackets()
        {
            lock (Packets) return Packets.Select(static packet => (byte[])packet.Clone()).ToArray();
        }

        public byte[] SnapshotChannels()
        {
            lock (Packets) return Channels.ToArray();
        }
    }

    private static (HostConfig cfg, RecordingPacketSink sink) BuildConfig()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30184,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                },
            },
        };
        return (cfg, new RecordingPacketSink());
    }


    [Fact]
    public async Task FixpCross_PublishesDecodedIncrementalTradeFrames_WhileSnapshotsFlow()
    {
        const uint sessionId = 553;
        const ulong sessionVerId = 1;
        const uint enteringFirm = 7;

        var (cfg, incrementalSink) = BuildConfig();
        cfg.Auth = new AuthConfig { DevMode = true, RequireFixpHandshake = true };
        cfg.Tcp.HeartbeatIntervalMs = 60_000;
        cfg.Tcp.IdleTimeoutMs = 60_000;
        cfg.Tcp.TestRequestGraceMs = 60_000;
        cfg.Firms.Add(new FirmConfig
        {
            Id = "firm-553",
            Name = "Issue 553 regression",
            EnteringFirmCode = enteringFirm,
        });
        cfg.Sessions.Add(new SessionConfig
        {
            SessionId = sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FirmId = "firm-553",
        });
        cfg.Channels[0].Snapshot = new SnapshotChannelConfig
        {
            Group = "239.255.42.85",
            Port = 30185,
            Ttl = 0,
            CadenceMs = 25,
        };

        var snapshotSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg,
            packetSinkFactory: _ => incrementalSink,
            snapshotSinkFactory: (_, _) => snapshotSink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        await WriteNegotiateAsync(stream, sessionId, sessionVerId, enteringFirm);
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadFrameAsync(stream, TimeSpan.FromSeconds(5))).TemplateId);
        await WriteEstablishAsync(stream, sessionId, sessionVerId, nextSeqNo: 1);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck,
            (await ReadFrameAsync(stream, TimeSpan.FromSeconds(5))).TemplateId);

        // Reproduce the production observation: snapshot heartbeat is alive
        // before any order/trade incremental event is submitted.
        await WaitForPacketCountAsync(snapshotSink, expected: 1, TimeSpan.FromSeconds(2));
        Assert.Contains(snapshotSink.SnapshotPackets()
            .SelectMany(DecodeUmdfFrames), frame => frame.TemplateId == 30);
        Assert.Empty(incrementalSink.SnapshotPackets());

        // First order: BUY PETR4 100 @ 12.34, which rests and must publish
        // Order_MBO_50(NEW) on the incremental sink.
        var newOrder = BuildSimpleNewOrder(clOrdId: 1001, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400,
            sessionId: sessionId, msgSeqNum: 1);
        await stream.WriteAsync(newOrder);

        var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);
        Assert.Equal(6, er1.Version);

        // Second order on the same established FIXP session: SELL PETR4
        // 100 @ 12.34 → fully
        // crosses the resting BUY. Aggressor session sees ER_Trade (no New —
        // fully filled). Passive owner is the same session, so it also sees
        // a Trade ER (i.e. two ER_Trade frames total).
        var sellOrder = BuildSimpleNewOrder(clOrdId: 2001, secId: Petr,
            side: '2', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400,
            sessionId: sessionId, msgSeqNum: 2);
        await stream.WriteAsync(sellOrder);

        var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.True(
            er2.TemplateId == EntryPointFrameReader.TidExecutionReportTrade,
            $"expected ER_Trade(203) but got tid={er2.TemplateId}");

        var er3 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.True(
            er3.TemplateId == EntryPointFrameReader.TidExecutionReportTrade,
            $"expected second ER_Trade(203) but got tid={er3.TemplateId}");

        await WaitForPacketCountAsync(incrementalSink, expected: 2, TimeSpan.FromSeconds(2));
        var packets = incrementalSink.SnapshotPackets();
        Assert.Equal(2, packets.Length);
        Assert.All(incrementalSink.SnapshotChannels(), channel => Assert.Equal((byte)84, channel));

        ref readonly var addHeader = ref MemoryMarshal.AsRef<PacketHeader>(
            packets[0].AsSpan(0, WireOffsets.PacketHeaderSize));
        ref readonly var crossHeader = ref MemoryMarshal.AsRef<PacketHeader>(
            packets[1].AsSpan(0, WireOffsets.PacketHeaderSize));
        Assert.Equal((byte)84, addHeader.ChannelNumber);
        Assert.Equal((byte)84, crossHeader.ChannelNumber);
        Assert.True(addHeader.SequenceVersion > 0);
        Assert.Equal(addHeader.SequenceVersion, crossHeader.SequenceVersion);
        Assert.True(addHeader.SequenceNumber > 0);
        Assert.Equal(addHeader.SequenceNumber + 1, crossHeader.SequenceNumber);
        Assert.True(addHeader.SendingTime > 0);
        Assert.True(crossHeader.SendingTime >= addHeader.SendingTime);

        var addFrames = DecodeUmdfFrames(packets[0]);
        var addFrame = Assert.Single(addFrames);
        Assert.Equal((ushort)50, addFrame.TemplateId);
        Assert.Equal((ushort)WireOffsets.OrderBlockLength, addFrame.BlockLength);
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.Order_MBO_50Data.TryParse(
            addFrame.Body.Span, out var orderAdd));
        Assert.Equal(Petr, (long)(ulong)orderAdd.Data.SecurityID.Value);
        Assert.Equal(MDUpdateAction.NEW, orderAdd.Data.MDUpdateAction);
        Assert.Equal(MDEntryType.BID, orderAdd.Data.MDEntryType);
        Assert.Equal(123_400L, orderAdd.Data.MDEntryPx.Mantissa);
        Assert.Equal(100L, orderAdd.Data.MDEntrySize.Value);
        Assert.True((long)orderAdd.Data.SecondaryOrderID.Value > 0);
        Assert.Equal(1u, orderAdd.Data.RptSeq);
        long restingOrderId = (long)orderAdd.Data.SecondaryOrderID.Value;

        var crossFrames = DecodeUmdfFrames(packets[1]);
        Assert.Equal(new ushort[] { 53, 51, 9 }, crossFrames.Select(static frame => frame.TemplateId));

        var tradeFrame = crossFrames[0];
        Assert.Equal((ushort)WireOffsets.TradeBlockLength, tradeFrame.BlockLength);
        Assert.True(Trade_53Data.TryParse(tradeFrame.Body.Span, out var trade));
        Assert.Equal(Petr, (long)(ulong)trade.Data.SecurityID.Value);
        Assert.Equal(123_400L, trade.Data.MDEntryPx.Mantissa);
        Assert.Equal(100L, trade.Data.MDEntrySize.Value);
        Assert.True(trade.Data.TradeID.Value > 0);
        Assert.Equal(enteringFirm, trade.Data.MDEntryBuyer);
        Assert.Equal(enteringFirm, trade.Data.MDEntrySeller);
        Assert.Equal(2u, trade.Data.RptSeq);

        var deleteFrame = crossFrames[1];
        Assert.Equal((ushort)WireOffsets.DeleteOrderBlockLength, deleteFrame.BlockLength);
        Assert.True(DeleteOrder_MBO_51Data.TryParse(deleteFrame.Body.Span, out var orderDelete));
        Assert.Equal(Petr, (long)(ulong)orderDelete.Data.SecurityID.Value);
        Assert.Equal(MDEntryType.BID, orderDelete.Data.MDEntryType);
        Assert.Equal(restingOrderId, (long)orderDelete.Data.SecondaryOrderID.Value);
        Assert.Equal(100L, orderDelete.Data.MDEntrySize.Value);
        Assert.Equal(123_400L, orderDelete.Data.MDEntryPx.Mantissa);
        Assert.Equal(3u, orderDelete.Data.RptSeq);

        // EmptyBook_9 is the required terminal marker after the only bid is
        // fully deleted. Snapshot packets continue on their separate sink.
        Assert.Equal((ushort)WireOffsets.EmptyBookBlockLength, crossFrames[2].BlockLength);
        var snapshotTemplateIds = snapshotSink.SnapshotPackets()
            .SelectMany(DecodeUmdfFrames)
            .Select(static frame => frame.TemplateId)
            .ToArray();
        Assert.Contains((ushort)30, snapshotTemplateIds);
        Assert.DoesNotContain((ushort)50, snapshotTemplateIds);
        Assert.DoesNotContain((ushort)51, snapshotTemplateIds);
        Assert.DoesNotContain((ushort)53, snapshotTemplateIds);
    }

    [Fact]
    public async Task NewOrderCross_DecodesAndProducesTradeForBothLegs()
    {
        // #GAP-16 (#52): NewOrderCross template 106 must decode into a
        // single atomic dispatch that submits both legs against each
        // other. With buy-first ordering the buy rests, then the sell
        // crosses → two ER_Trade frames (one per side, both routed back
        // to the same session) and a single UMDF Trade frame.
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var crossFrame = BuildNewOrderCross(crossId: 7777, secId: Petr,
            qty: 100, priceMantissa: 100_0000,
            buyClOrdId: 5001, sellClOrdId: 5002);
        await stream.WriteAsync(crossFrame);

        // Both sides belong to the same session so the engine emits two
        // ER_Trade frames back-to-back. Order may interleave with
        // potential ER_New for the (briefly resting) buy leg if engine
        // emits OnOrderAccepted before matching — but the matching
        // engine emits trades as the sell aggressor sweeps the buy and
        // does NOT emit OnOrderAccepted for fully-filled aggressors.
        // We accept any frame mix as long as we receive at least 2
        // ER_Trade frames within a few seconds.
        int trades = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (trades < 2 && DateTime.UtcNow < deadline)
        {
            var er = await ReadFrameAsync(stream, TimeSpan.FromSeconds(2));
            if (er.TemplateId == EntryPointFrameReader.TidExecutionReportTrade) trades++;
        }
        Assert.Equal(2, trades);

        // Single UMDF packet expected: both legs processed in one
        // dispatch turn flush exactly once. Allow a brief race with the
        // dispatcher thread.
        var sinkDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (sink.Packets.Count < 1 && DateTime.UtcNow < sinkDeadline)
            await Task.Delay(20);
        Assert.True(sink.Packets.Count >= 1, $"expected >= 1 multicast packet, got {sink.Packets.Count}");
    }

    [Fact]
    public async Task UnknownInstrument_ProducesRejectExecutionReport()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var bogus = BuildSimpleNewOrder(clOrdId: 9001, secId: 999_999_999_999L,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 100_000);
        await stream.WriteAsync(bogus);

        var er = await ReadFrameAsync(stream, TimeSpan.FromSeconds(2));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportReject, er.TemplateId);
    }

    [Fact]
    public async Task MaxOpenOrdersPerFirm_ProducesBusinessLevelOrderReject()
    {
        var (cfg, sink) = BuildConfig();
        cfg.MaxOpenOrdersPerFirm = 1;
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(BuildSimpleNewOrder(
            clOrdId: 1001, secId: Petr, side: '1', ordType: '2', tif: '0',
            qty: 100, priceMantissa: 123_400));
        var accepted = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, accepted.TemplateId);

        await stream.WriteAsync(BuildSimpleNewOrder(
            clOrdId: 1002, secId: Petr, side: '1', ordType: '2', tif: '0',
            qty: 100, priceMantissa: 123_300));
        var rejected = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));

        Assert.Equal(EntryPointFrameReader.TidExecutionReportReject, rejected.TemplateId);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(rejected.Body.AsSpan(44, 4)));
    }

    [Fact]
    public async Task CancelByOrigClOrdId_RoundTripsExecutionReportCancel_WithBothClOrdIdAndOrigClOrdId()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // BUY PETR4 100 @ 12.34 with ClOrdId=4242 (rests).
        var newOrder = BuildSimpleNewOrder(clOrdId: 4242, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(newOrder);

        var erNew = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, erNew.TemplateId);

        // Cancel by OrigClOrdID only (OrderID=0). New ClOrdId=4243.
        var cancel = BuildOrderCancelRequest(clOrdId: 4243, secId: Petr,
            orderId: 0, origClOrdId: 4242, side: '1');
        await stream.WriteAsync(cancel);

        var erCancel = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, erCancel.TemplateId);

        // ER_Cancel body must carry both ClOrdID=4243 and OrigClOrdID=4242.
        // Per ExecutionReportEncoder layout: ClOrdID@20, OrigClOrdID@28.
        ulong clOrdIdField = BinaryPrimitives.ReadUInt64LittleEndian(erCancel.Body.AsSpan(20, 8));
        ulong origClOrdIdField = BinaryPrimitives.ReadUInt64LittleEndian(erCancel.Body.AsSpan(88, 8));
        Assert.Equal(4243UL, clOrdIdField);
        Assert.Equal(4242UL, origClOrdIdField);
    }

    [Fact]
    public async Task ReplaceByOrigClOrdId_LostPriority_RoundTripsCancelThenNew_WithOrigClOrdId()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var newOrder = BuildSimpleNewOrder(clOrdId: 5000, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(newOrder);
        var erNew = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, erNew.TemplateId);

        // Replace by OrigClOrdID with a NEW PRICE (loses priority): expect ER_Cancel+ER_New.
        var replace = BuildSimpleModifyOrder(clOrdId: 5001, secId: Petr,
            side: '1', qty: 100, priceMantissa: 123_500, orderId: 0, origClOrdId: 5000);
        await stream.WriteAsync(replace);

        var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, er2.TemplateId);
        ulong clOrd = BinaryPrimitives.ReadUInt64LittleEndian(er2.Body.AsSpan(20, 8));
        ulong origClOrd = BinaryPrimitives.ReadUInt64LittleEndian(er2.Body.AsSpan(88, 8));
        Assert.Equal(5001UL, clOrd);
        Assert.Equal(5000UL, origClOrd);

        var er3 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er3.TemplateId);
    }

    [Fact]
    public async Task InstrumentDefinitionPublisher_EmitsAllInstrumentsWithinOneCycle()
    {
        var (cfg, mboSink) = BuildConfig();
        // Enable InstrumentDef publisher with a short cadence so the host
        // emits a full cycle inside the test deadline.
        cfg.Channels[0].InstrumentDefinition = new InstrumentDefinitionConfig
        {
            ChannelNumber = 184,
            Group = "239.255.43.184",
            Port = 31184,
            Ttl = 0,
            CadenceMs = 50,
        };

        var idSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg,
            packetSinkFactory: _ => mboSink,
            instrumentDefSinkFactory: (_, _) => idSink);
        await host.StartAsync();

        // Wait up to 2s for the first cycle.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (idSink.Packets)
            {
                if (idSink.Packets.Count > 0) break;
            }
            await Task.Delay(20);
        }

        // Decode every SecurityDefinition_12 frame across all packets and
        // assert the consumer can resolve every configured SecurityID +
        // Symbol — i.e. a fresh subscriber learns the instrument table from
        // the bus alone.
        var instruments = B3.Exchange.Instruments.InstrumentLoader.LoadFromFile(
            cfg.Channels[0].InstrumentsFile);
        var expectedSecIds = instruments.Select(i => i.SecurityId).ToHashSet();
        var expectedSymbols = instruments.Select(i => i.Symbol).ToHashSet();

        var seenSecIds = new HashSet<long>();
        var seenSymbols = new HashSet<string>();

        const int FrameOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        const int FrameSize = FrameOffset + WireOffsets.SecDefBodyTotal;

        byte[][] snapshot;
        lock (idSink.Packets) snapshot = idSink.Packets.ToArray();
        Assert.NotEmpty(snapshot);

        foreach (var packet in snapshot)
        {
            // PacketHeader stamps configured InstrumentDef channel number.
            Assert.Equal((byte)184, packet[WireOffsets.PacketHeaderChannelOffset]);
            int p = WireOffsets.PacketHeaderSize;
            while (p + FrameSize <= packet.Length)
            {
                var bodySpan = packet.AsSpan(p + FrameOffset, WireOffsets.SecDefBlockLength);
                long secId = MemoryMarshal.Read<long>(bodySpan.Slice(WireOffsets.SecDefSecurityIdOffset, 8));
                seenSecIds.Add(secId);
                var symBytes = bodySpan.Slice(WireOffsets.SecDefSymbolOffset, 20);
                int n = symBytes.IndexOf((byte)0);
                if (n < 0) n = symBytes.Length;
                seenSymbols.Add(System.Text.Encoding.ASCII.GetString(symBytes.Slice(0, n)));
                p += FrameSize;

                // Every emitted frame should also be SBE-decodable.
                Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SecurityDefinition_12Data.TryParse(bodySpan, out _));
            }
        }

        Assert.Superset(expectedSecIds, seenSecIds);
        Assert.True(expectedSecIds.IsSubsetOf(seenSecIds),
            $"missing SecurityIDs: {string.Join(",", expectedSecIds.Except(seenSecIds))}");
        Assert.True(expectedSymbols.IsSubsetOf(seenSymbols),
            $"missing symbols: {string.Join(",", expectedSymbols.Except(seenSymbols))}");
    }

    [Fact]
    public async Task OrderMassActionRequest_CancelsAllRestingOrdersForSession()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // Place two resting BUY orders.
        await stream.WriteAsync(BuildSimpleNewOrder(clOrdId: 7001, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 100_000));
        var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);

        await stream.WriteAsync(BuildSimpleNewOrder(clOrdId: 7002, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 200, priceMantissa: 99_000));
        var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er2.TemplateId);

        // Mass-cancel all orders on this security (no Side filter).
        await stream.WriteAsync(BuildOrderMassActionRequest(clOrdId: 7100, secId: Petr));

        // The per-order cancellation ERs must precede the terminal accepted
        // report on the ordered FIXP business stream.
        var cancel1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel1.TemplateId);
        var cancel2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel2.TemplateId);

        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal((byte)'1', report.Body[44]); // MassActionResponse = ACCEPTED
        ulong reportClOrd = BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8));
        Assert.Equal(7100UL, reportClOrd);
    }

    [Fact]
    public async Task OrderMassActionRequest_SideFilter_OnlyCancelsMatchingSide()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // One BUY @ 100 and one SELL @ 200 — they don't cross.
        await stream.WriteAsync(BuildSimpleNewOrder(clOrdId: 8001, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 100_000));
        var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);

        await stream.WriteAsync(BuildSimpleNewOrder(clOrdId: 8002, secId: Petr,
            side: '2', ordType: '2', tif: '0', qty: 100, priceMantissa: 200_000));
        var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er2.TemplateId);

        // Mass-cancel only BUY side.
        await stream.WriteAsync(BuildOrderMassActionRequest(clOrdId: 8100, secId: Petr, side: '1'));

        var cancel = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel.TemplateId);
        // Verify it cancelled the BUY (ER_Cancel.Side @ body[18] = '1')
        Assert.Equal((byte)'1', cancel.Body[18]);

        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal((byte)'1', report.Body[48]); // Side filter echoed = Buy
    }

    [Fact]
    public async Task OrderMassActionRequest_ZeroMatchingOrders_EmitsTerminalAcceptedReport()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // No orders have been submitted. The first business response must
        // therefore be the terminal report, with no cancellation ER before it.
        await stream.WriteAsync(BuildOrderMassActionRequest(clOrdId: 9100, secId: Petr));

        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal(6, report.Version);
        Assert.Equal((byte)'1', report.Body[44]); // MassActionResponse = ACCEPTED
        Assert.Equal(9100UL,
            BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8)));
        // B3 schema 8.4.2 template 702 has no TotalAffectedOrders field.
        // HostRouterTests proves the terminal outcome count is zero; on wire,
        // the terminal ACCEPTED itself is the supported zero-match contract.
        Assert.Equal(0, report.Body[^1]); // empty Text varData
    }

    [Fact]
    public async Task OrderMassActionRequest_OrdTagIdFilter_Accepted()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // Build a request with OrdTagID set; no matching orders exist, but the filter is accepted.
        var frame = BuildOrderMassActionRequest(clOrdId: 9100, secId: Petr);
        frame[EntryPointFrameReader.WireHeaderSize + 29] = 7; // OrdTagID
        await stream.WriteAsync(frame);

        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal((byte)'1', report.Body[44]); // MassActionResponse = ACCEPTED
        Assert.Equal(9100UL,
            BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8)));
    }

    private readonly record struct UmdfFrame(ushort TemplateId, ushort BlockLength, ReadOnlyMemory<byte> Body);

    private static List<UmdfFrame> DecodeUmdfFrames(byte[] packet)
    {
        var frames = new List<UmdfFrame>();
        int cursor = WireOffsets.PacketHeaderSize;
        while (cursor < packet.Length)
        {
            Assert.True(cursor + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize <= packet.Length,
                $"truncated UMDF frame header at offset {cursor}");

            ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(cursor + WireOffsets.FramingHeaderMessageLengthOffset, 2));
            ushort encodingType = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(cursor + WireOffsets.FramingHeaderEncodingTypeOffset, 2));
            Assert.Equal((ushort)0, encodingType);
            Assert.True(messageLength >= WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize,
                $"invalid UMDF frame length {messageLength} at offset {cursor}");
            Assert.True(cursor + messageLength <= packet.Length,
                $"UMDF frame length {messageLength} overruns packet at offset {cursor}");

            int sbeHeader = cursor + WireOffsets.FramingHeaderSize;
            ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(sbeHeader, 2));
            ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(sbeHeader + 2, 2));
            int bodyOffset = sbeHeader + WireOffsets.SbeMessageHeaderSize;
            Assert.True(bodyOffset + blockLength <= cursor + messageLength,
                $"template {templateId} block length {blockLength} overruns frame");

            frames.Add(new UmdfFrame(templateId, blockLength, packet.AsMemory(bodyOffset, blockLength)));
            cursor += messageLength;
        }

        Assert.Equal(packet.Length, cursor);
        return frames;
    }

    private static async Task WaitForPacketCountAsync(
        RecordingPacketSink sink,
        int expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (sink.Count < expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(sink.Count >= expected,
            $"expected at least {expected} packets, got {sink.Count}");
    }

    private static async Task WriteNegotiateAsync(
        NetworkStream stream,
        uint sessionId,
        ulong sessionVerId,
        uint enteringFirm)
    {
        var credentials = Encoding.ASCII.GetBytes(
            $"{{\"auth_type\":\"basic\",\"username\":\"{sessionId}\",\"access_key\":\"\"}}");
        var buffer = new byte[EntryPointFrameReader.MaxInboundMessageLength];
        int length = EntryPointFixpFrameCodec.EncodeNegotiate(
            buffer,
            sessionId,
            sessionVerId,
            timestampNanos: 0,
            enteringFirm,
            onBehalfFirm: null,
            credentials,
            clientIp: "127.0.0.1"u8,
            clientAppName: "issue-553-regression"u8,
            clientAppVersion: "1"u8);
        await stream.WriteAsync(buffer.AsMemory(0, length));
    }

    private static async Task WriteEstablishAsync(
        NetworkStream stream,
        uint sessionId,
        ulong sessionVerId,
        uint nextSeqNo)
    {
        var buffer = new byte[EntryPointFrameReader.MaxInboundMessageLength];
        int length = EntryPointFixpFrameCodec.EncodeEstablish(
            buffer,
            sessionId,
            sessionVerId,
            timestampNanos: 0,
            keepAliveIntervalMillis: 60_000,
            nextSeqNo,
            cancelOnDisconnectType: 0,
            codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(buffer.AsMemory(0, length));
    }

    private static byte[] BuildOrderCancelRequest(ulong clOrdId, long secId, ulong orderId, ulong origClOrdId, char side)
    {
        // 12-byte composite header (SOFH+SBE) + 76-byte OrderCancelRequest (V6) body.
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 76];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 76, templateId: EntryPointFrameReader.TidOrderCancelRequest, version: 0);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(28, 8), secId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(36, 8), orderId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(44, 8), origClOrdId);
        body[52] = (byte)side;
        return frame;
    }

    private static byte[] BuildSimpleModifyOrder(ulong clOrdId, long secId, char side, long qty,
        long priceMantissa, ulong orderId, ulong origClOrdId)
    {
        // 12-byte composite header + 98-byte SimpleModifyOrderV2 body.
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 98];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 98, templateId: EntryPointFrameReader.TidSimpleModifyOrder, version: 2);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        body[56] = (byte)side;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(68, 8), priceMantissa);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(76, 8), orderId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(84, 8), origClOrdId);
        return frame;
    }

    private static byte[] BuildNewOrderCross(ulong crossId, long secId, long qty, long priceMantissa,
        ulong buyClOrdId, ulong sellClOrdId)
    {
        // 12-byte composite header + 84-byte root + 3-byte group header
        // + 2 × 22-byte sides + 1-byte deskID len + 1-byte memo len
        // (both empty) = 133 bytes total.
        const int RootSize = 84;
        const int GroupHeaderSize = 3;
        const int SideEntrySize = 22;
        int total = EntryPointFrameReader.WireHeaderSize + RootSize + GroupHeaderSize + 2 * SideEntrySize + 2;
        var frame = new byte[total];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: RootSize, templateId: EntryPointFrameReader.TidNewOrderCross, version: 6);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        body[18] = 0; // OrdType null = implicit Limit
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), crossId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(56, 8), (ulong)qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(64, 8), priceMantissa);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(72, 2), 65535);  // CrossedIndicator null
        body[74] = 255;  // CrossType null
        body[75] = 255;  // CrossPrioritization null
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(76, 8), 0UL); // MaxSweepQty null

        // NoSides group header + 2 entries (buy first then sell).
        int cursor = RootSize;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(cursor, 2), SideEntrySize);
        body[cursor + 2] = 2;
        cursor += GroupHeaderSize;

        body[cursor + 0] = (byte)'1'; // Side=Buy
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(cursor + 6, 4), 0xFFFFFFFFu); // FirmOptional null
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(cursor + 10, 8), buyClOrdId);
        cursor += SideEntrySize;

        body[cursor + 0] = (byte)'2'; // Side=Sell
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(cursor + 6, 4), 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(cursor + 10, 8), sellClOrdId);
        cursor += SideEntrySize;

        // Empty DeskID and Memo (length=0 each).
        body[cursor++] = 0;
        body[cursor++] = 0;
        return frame;
    }

    private static byte[] BuildSimpleNewOrder(ulong clOrdId, long secId, char side, char ordType,
        char tif, long qty, long priceMantissa, uint sessionId = 0, uint msgSeqNum = 0)
    {
        // 12-byte composite header + 82-byte SimpleNewOrderV2 body.
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), msgSeqNum);
        if (sessionId != 0)
        {
            ulong sendingTimeNanos = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
            BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(8, 8), sendingTimeNanos);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        body[56] = (byte)side;
        body[57] = (byte)ordType;
        body[58] = (byte)tif;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(68, 8), priceMantissa);
        return frame;
    }

    private static byte[] BuildOrderMassActionRequest(ulong clOrdId, char? side = null, long? secId = null)
    {
        // 12-byte composite header (SOFH+SBE) + 52-byte OrderMassActionRequest (V6) body.
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 52];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 52, templateId: EntryPointFrameReader.TidOrderMassActionRequest, version: 6);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        body[18] = 3;                  // MassActionType = CANCEL_ORDERS
        body[19] = 255;                // MassActionScope null
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        body[28] = 255;                // ExecRestatementReason null
        body[29] = 0;                  // OrdTagID null
        body[30] = side.HasValue ? (byte)side.Value : (byte)0;
        // body[31] padding; body[32..38] Asset null (zeros)
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(38, 8),
            secId.HasValue ? (ulong)secId.Value : 0UL);
        // body[46..52] InvestorID/SecurityExchange — leave as constant "BVMF"
        body[46] = (byte)'B';
        body[47] = (byte)'V';
        body[48] = (byte)'M';
        body[49] = (byte)'F';
        return frame;
    }

    private readonly record struct ReadFrame(ushort TemplateId, ushort Version, byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromMilliseconds(1);
        using var cts = new CancellationTokenSource(timeout);
        var headerBuf = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, headerBuf, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
        ushort encodingType = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(2, 2));
        if (encodingType != EntryPointFrameReader.SofhEncodingType)
            throw new InvalidOperationException($"unexpected SOFH encoding type 0x{encodingType:X4}");
        var sbeHeader = headerBuf.AsSpan(EntryPointFrameReader.SofhSize);
        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(2, 2));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(6, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, version, body);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0) throw new EndOfStreamException("connection closed");
            read += n;
        }
    }
}
