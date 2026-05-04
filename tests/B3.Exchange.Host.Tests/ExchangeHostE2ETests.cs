using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Core;
using B3.Umdf.WireEncoder;

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
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    private static (HostConfig cfg, RecordingPacketSink sink) BuildConfig()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
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

    private static string ResolveRepoFile(string relPath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relPath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"could not locate {relPath} from {AppContext.BaseDirectory}");
    }

    [Fact]
    public async Task NewOrder_RoundTripsExecutionReportNew_ThenCrossingOrderProducesTrade()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        // First order: BUY PETR4 100 @ 12.34
        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var newOrder = BuildSimpleNewOrder(clOrdId: 1001, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(newOrder);

        var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);
        Assert.Equal(6, er1.Version);

        // Second order on the SAME session: SELL PETR4 100 @ 12.34 → fully
        // crosses the resting BUY. Aggressor session sees ER_Trade (no New —
        // fully filled). Passive owner is the same session, so it also sees
        // a Trade ER (i.e. two ER_Trade frames total).
        var sellOrder = BuildSimpleNewOrder(clOrdId: 2001, secId: Petr,
            side: '2', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(sellOrder);

        var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.True(
            er2.TemplateId == EntryPointFrameReader.TidExecutionReportTrade,
            $"expected ER_Trade(203) but got tid={er2.TemplateId}");

        var er3 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.True(
            er3.TemplateId == EntryPointFrameReader.TidExecutionReportTrade,
            $"expected second ER_Trade(203) but got tid={er3.TemplateId}");

        // Multicast side: at least 2 packets emitted (BUY add + SELL cross
        // batch). Each packet starts with the 16-byte UMDF PacketHeader.
        // Poll briefly: FlushPacket runs on the dispatcher thread after the
        // ER frames are queued, so it may lag the client's ER read slightly.
        var sinkDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (sink.Packets.Count < 2 && DateTime.UtcNow < sinkDeadline)
            await Task.Delay(20);
        Assert.True(sink.Packets.Count >= 2, $"expected >= 2 multicast packets, got {sink.Packets.Count}");
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

        // Expect: OrderMassActionReport(ACCEPTED) first, then 2 ER_Cancel
        // frames (one per resting order) — order between the two cancels
        // is FIFO-by-orderId and not part of the assertion.
        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal((byte)'1', report.Body[44]); // MassActionResponse = ACCEPTED
        ulong reportClOrd = BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8));
        Assert.Equal(7100UL, reportClOrd);

        var cancel1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel1.TemplateId);
        var cancel2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel2.TemplateId);
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

        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal((byte)'1', report.Body[48]); // Side filter echoed = Buy

        var cancel = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel.TemplateId);
        // Verify it cancelled the BUY (ER_Cancel.Side @ body[18] = '1')
        Assert.Equal((byte)'1', cancel.Body[18]);
    }

    [Fact]
    public async Task OrderMassActionRequest_OrdTagIdFilter_RejectedAsUnsupportedFeature()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // Build a request with OrdTagID set (unsupported feature).
        var frame = BuildOrderMassActionRequest(clOrdId: 9100, secId: Petr);
        frame[EntryPointFrameReader.WireHeaderSize + 29] = 7; // OrdTagID
        await stream.WriteAsync(frame);

        var report = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal((byte)'0', report.Body[44]); // MassActionResponse = REJECTED
        Assert.Equal((byte)0, report.Body[45]);   // RejectReason = MASS_ACTION_NOT_SUPPORTED
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
        char tif, long qty, long priceMantissa)
    {
        // 12-byte composite header + 82-byte SimpleNewOrderV2 body.
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
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
