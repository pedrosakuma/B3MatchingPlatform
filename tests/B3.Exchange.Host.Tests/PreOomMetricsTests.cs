using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

public class PreOomMetricsTests
{
    private const long Petr = 900_000_000_001L;

    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    [Fact]
    public async Task MetricsExposePreOomGauges_AfterRestingOrders()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory,
            "wal-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Http = new HttpConfig { Listen = "127.0.0.1:0", LivenessStaleMs = 60000 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 84,
                        IncrementalGroup = "239.255.42.84",
                        IncrementalPort = 30184,
                        Ttl = 0,
                        InstrumentsFile = instrumentsPath,
                        Persistence = new PersistenceConfig
                        {
                            DataDir = dataDir,
                            Throttle = new SnapshotThrottleConfig { EveryNCommands = 1000 },
                            Wal = new WalConfig { Enabled = true, FsyncPerWrite = false },
                        },
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host.TcpEndpoint!.Address, host.TcpEndpoint.Port);
            var stream = tcp.GetStream();

            await stream.WriteAsync(BuildSimpleNewOrder(1001, Petr, '1', '2', '0', 100, 123_400));
            await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
            await stream.WriteAsync(BuildSimpleNewOrder(1002, Petr, '1', '2', '0', 200, 123_300));
            await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));

            using var http = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };
            string body = "";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                body = await http.GetStringAsync("/metrics");
                if (body.Contains($"exch_book_live_orders{{channel=\"84\",security_id=\"{Petr}\"}} 2", StringComparison.Ordinal)
                    && MetricValue(body, "exch_wal_total_size_bytes") > 0)
                    break;
                await Task.Delay(25);
            }

            Assert.Contains($"exch_book_live_orders{{channel=\"84\",security_id=\"{Petr}\"}} 2", body);
            Assert.True(MetricValue(body, "exch_wal_size_bytes", "channel=\"84\"") > 0, body);
            Assert.True(MetricValue(body, "exch_wal_total_size_bytes") > 0, body);
            Assert.True(MetricValue(body, "exch_wal_last_write_unixms", "channel=\"84\"") > 0, body);
            Assert.True(MetricValue(body, "exch_fixp_retransmit_buffer_full_percent", "firm=\"unknown\"") > 0, body);
            Assert.DoesNotContain("fixp_journal_", body);
        }
        finally
        {
            TempDirs.TryDelete(dataDir);
        }
    }

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

    private static async Task ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var headerBuf = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, headerBuf, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        await ReadExactAsync(stream, body, cts.Token);
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

    private static double MetricValue(string body, string name, string? requiredLabel = null)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(name, StringComparison.Ordinal)) continue;
            if (requiredLabel is not null && !line.Contains(requiredLabel, StringComparison.Ordinal)) continue;
            int space = line.LastIndexOf(' ');
            if (space < 0) continue;
            return double.Parse(line[(space + 1)..], CultureInfo.InvariantCulture);
        }
        return 0;
    }
}
