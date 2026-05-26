using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// OPT-03 / ADR 0013 — end-to-end: boot an <see cref="ExchangeHost"/> with
/// a single option series that expires today, place a resting limit order
/// on it, then call <see cref="ExchangeHost.TriggerDailyReset"/> and assert
/// the originating session receives an <c>ER_Cancel</c> and at least one
/// multicast packet containing the <c>SecurityStatus_3</c> CLOSE frame.
///
/// Multicast packets are captured by a recording <see cref="IUmdfPacketSink"/>
/// (the same pattern <c>ExchangeHostE2ETests</c> uses). The packet scanner
/// only checks for the presence of TemplateId 3 — packet schema fidelity is
/// already covered by <c>UmdfWireEncoderTests</c>.
/// </summary>
public class OptionExpiryE2ETests
{
    private const long OptSecId = 900000099111L;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    private static string WriteOptionInstrumentsJson(string scratchDir, DateOnly expirationDate)
    {
        Directory.CreateDirectory(scratchDir);
        var path = Path.Combine(scratchDir, "instruments-opt-today.json");
        var json = $$"""
        [
          {
            "symbol": "PETRZ12",
            "securityId": {{OptSecId}},
            "tickSize": "0.01",
            "minPx": "0.01",
            "maxPx": "999999.99",
            "currency": "BRL",
            "isin": "BRPETRZ12ZZ0",
            "securityType": "OPT",
            "lotSize": 1,
            "strikePrice": "12.00",
            "expirationDate": "{{expirationDate:yyyy-MM-dd}}",
            "putOrCall": "Call",
            "exerciseStyle": "American",
            "underlyingSecurityId": 900000000001,
            "underlyingSymbol": "PETR4",
            "contractMultiplier": "100"
          }
        ]
        """;
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task TriggerDailyReset_CancelsRestingOrderAndEmitsSecurityStatusCloseForExpiringOption()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory,
            "opt-t3-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrumentsPath = WriteOptionInstrumentsJson(scratch,
                expirationDate: DateOnly.FromDateTime(DateTime.UtcNow));

            var sink = new RecordingPacketSink();
            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 93,
                        IncrementalGroup = "239.255.42.93",
                        IncrementalPort = 30193,
                        Ttl = 0,
                        InstrumentsFile = instrumentsPath,
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
            await host.StartAsync();
            var ep = host.TcpEndpoint!;

            using var client = new TcpClient();
            await client.ConnectAsync(ep.Address, ep.Port);
            var stream = client.GetStream();

            // Rest a BUY @ 12.34 on the expiring option series.
            var newOrder = BuildSimpleNewOrder(clOrdId: 11_001, secId: OptSecId,
                side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
            await stream.WriteAsync(newOrder);
            var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);

            // Trigger end-of-trading-day. The sweep enqueues the
            // ExpireSecurity work item, the drain inside TriggerDailyReset
            // blocks until the dispatcher has processed it.
            int packetsBefore;
            lock (sink.Packets) packetsBefore = sink.Packets.Count;
            host.TriggerDailyReset(reason: "test-expiry");

            // The resting BUY should now come back as an ER_Cancel.
            var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, er2.TemplateId);
            // ER_Cancel body[18] carries Side; the resting order was Buy.
            Assert.Equal((byte)'1', er2.Body[18]);

            // The dispatcher must have emitted at least one new UMDF packet
            // since the sweep, and that packet (or any packet emitted
            // afterwards) must contain a SecurityStatus_3 frame (templateId
            // = 3) — the terminal CLOSE for the expired series.
            List<byte[]> packetsAfter;
            lock (sink.Packets) packetsAfter = sink.Packets.GetRange(packetsBefore, sink.Packets.Count - packetsBefore);
            Assert.NotEmpty(packetsAfter);
            Assert.Contains(packetsAfter, PacketContainsTemplateId3);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Walks the inc messages inside a UMDF packet and returns true if any
    /// SBE message header carries TemplateId == 3 (SecurityStatus_3).
    /// PacketHeader is 16 bytes; each inc message is prefixed by a 4-byte
    /// FramingHeader (length@0, encodingType@2) and then an 8-byte SBE
    /// message header (blockLength@0, templateId@2, schemaId@4,
    /// version@6).
    /// </summary>
    private static bool PacketContainsTemplateId3(byte[] packet)
    {
        const int PacketHeaderSize = 16;
        const int FramingHeaderSize = 4;
        int cursor = PacketHeaderSize;
        while (cursor + FramingHeaderSize + 8 <= packet.Length)
        {
            ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(cursor, 2));
            if (msgLen == 0 || cursor + msgLen > packet.Length) break;
            ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(cursor + FramingHeaderSize + 2, 2));
            if (templateId == 3) return true;
            cursor += msgLen;
        }
        return false;
    }

    // ---- helpers duplicated from ExchangeHostE2ETests (same wire format) ----

    private static byte[] BuildSimpleNewOrder(ulong clOrdId, long secId, char side, char ordType,
        char tif, long qty, long priceMantissa)
    {
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
