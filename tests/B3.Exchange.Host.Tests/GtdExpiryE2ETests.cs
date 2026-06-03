using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// GAP-23 / issue #499 — end-to-end: boot an <see cref="ExchangeHost"/>,
/// place a resting GTD order (full <c>NewOrderSingle_102</c> with a
/// <c>TimeInForce=GTD</c> and an <c>ExpireDate</c> on or before today) over
/// the wire, then call <see cref="ExchangeHost.TriggerDailyReset"/> and
/// assert the originating session receives an <c>ER_Cancel</c> — proving the
/// wire→engine→sweeper→gateway GTD expiry path works through the host.
/// </summary>
public class GtdExpiryE2ETests
{
    private const long SecId = 900000000001L;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    private static string WriteEquityInstrumentsJson(string scratchDir)
    {
        Directory.CreateDirectory(scratchDir);
        var path = Path.Combine(scratchDir, "instruments-gtd.json");
        var json = $$"""
        [
          {
            "symbol": "PETR4",
            "securityId": {{SecId}},
            "tickSize": "0.01",
            "minPx": "0.01",
            "maxPx": "999999.99",
            "currency": "BRL",
            "isin": "BRPETRACNPR6",
            "securityType": "EQUITY",
            "lotSize": 100
          }
        ]
        """;
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task TriggerDailyReset_CancelsRestingGtdOrderWithElapsedExpireDate()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory,
            "gtd-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrumentsPath = WriteEquityInstrumentsJson(scratch);
            var sink = new RecordingPacketSink();
            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 94,
                        IncrementalGroup = "239.255.42.94",
                        IncrementalPort = 30194,
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

            // GTD ExpireDate a few days in the past: unambiguously elapsed
            // regardless of the UTC-vs-local-market-date boundary, so the
            // daily-reset sweep must cancel it. (A past-dated GTD order is
            // accepted at entry — the engine is clockless — and swept here.)
            int epochDay = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber
                - new DateOnly(1970, 1, 1).DayNumber;
            ushort expireDate = (ushort)(epochDay - 5);

            var newOrder = BuildNewOrderSingleGtd(clOrdId: 12_001, secId: SecId,
                side: '1', qty: 100, priceMantissa: 123_400, expireDate: expireDate);
            await stream.WriteAsync(newOrder);
            var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);

            host.TriggerDailyReset(reason: "test-gtd-expiry");

            // The resting GTD BUY should come back as an ER_Cancel.
            var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, er2.TemplateId);
            // ER_Cancel body[18] carries Side; the resting order was Buy.
            Assert.Equal((byte)'1', er2.Body[18]);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task TriggerDailyReset_KeepsRestingGtdOrderWithFutureExpireDate()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory,
            "gtd-e2e-keep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrumentsPath = WriteEquityInstrumentsJson(scratch);
            var sink = new RecordingPacketSink();
            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 95,
                        IncrementalGroup = "239.255.42.95",
                        IncrementalPort = 30195,
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

            // ExpireDate well in the future → the sweep must NOT cancel it.
            int epochDay = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber
                - new DateOnly(1970, 1, 1).DayNumber;
            ushort expireDate = (ushort)(epochDay + 30);

            var newOrder = BuildNewOrderSingleGtd(clOrdId: 12_101, secId: SecId,
                side: '1', qty: 100, priceMantissa: 123_400, expireDate: expireDate);
            await stream.WriteAsync(newOrder);
            var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);

            host.TriggerDailyReset(reason: "test-gtd-keep");

            // The future-dated GTD survives the sweep — and as of GAP-26 (#498)
            // it is restated for the new trading day: a private ER_Modify with
            // OrdStatus=RESTATED('R') and ExecRestatementReason=GT_RESTATEMENT(1).
            var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportModify, er2.TemplateId);
            Assert.Equal((byte)'R', er2.Body[19]);
            Assert.Equal((byte)1, er2.Body[179]);
            Assert.Equal((byte)'6', er2.Body[117]);   // TimeInForce echoed = GTD
            Assert.Equal(expireDate, BinaryPrimitives.ReadUInt16LittleEndian(er2.Body.AsSpan(118, 2)));
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Builds a full NewOrderSingle_102 (V2) frame with a GTD
    /// TimeInForce and the given ExpireDate. BlockLength = 125; no
    /// var-data trailer (Account/Memo omitted).
    /// </summary>
    private static byte[] BuildNewOrderSingleGtd(ulong clOrdId, long secId, char side,
        long qty, long priceMantissa, ushort expireDate)
    {
        const int BlockLength = 125;
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + BlockLength];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: BlockLength, templateId: EntryPointFrameReader.TidNewOrderSingle, version: 2);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        body[56] = (byte)side;
        body[57] = (byte)'2';          // OrdType = Limit
        body[58] = (byte)'6';          // TimeInForce = GTD
        body[59] = 255;                // Routing = null
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(68, 8), priceMantissa);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(76, 8), long.MinValue); // StopPx null
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(105, 2), expireDate);
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
