using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// E2E concurrency test (#180 / GAP-03): boots an <see cref="ExchangeHost"/>
/// with 4 channels, fires concurrent NewOrder traffic on a dedicated TCP
/// session per channel, and asserts:
///
/// <list type="bullet">
///   <item>No cross-talk: each channel's recording packet sink only sees
///   UMDF frames whose <c>SecurityID</c> belongs to that channel's
///   instrument set.</item>
///   <item>Per-channel UMDF <c>SequenceNumber</c> is monotonic and
///   strictly increasing on every emitted packet (no rollover, no
///   duplicates).</item>
///   <item>Every accepted order produces exactly one
///   <c>ExecutionReport_New</c> back to its session (no losses, no
///   delivery to the wrong session).</item>
/// </list>
///
/// Each channel hosts a single instrument with its own SecurityID prefix
/// (1xxx, 2xxx, 3xxx, 4xxx) so the cross-talk assertion is trivial.
/// </summary>
public class E2EConcurrentMultiChannelTests
{
    private const int ChannelCount = 4;
    private const int OrdersPerChannel = 75;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Enqueue(packet.ToArray());
    }

    private static long SecurityIdFor(int channelIndex) => 1_000_000L + (channelIndex + 1) * 100_000L + 1;

    private static string WriteInstrumentsFile(int channelIndex, string dir)
    {
        var path = Path.Combine(dir, $"instr-ch{channelIndex}.json");
        var symbol = $"SYM{channelIndex}X";
        var isin = $"BR{channelIndex:D3}TEST0001";
        var json = "[ { \"symbol\": \"" + symbol + "\", \"securityId\": " + SecurityIdFor(channelIndex) +
            ", \"tickSize\": \"0.01\", \"lotSize\": 1, \"minPx\": \"0.01\", \"maxPx\": \"999999.99\"," +
            " \"currency\": \"BRL\", \"isin\": \"" + isin + "\", \"securityType\": \"CS\" } ]";
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task FourChannels_ConcurrentTraffic_NoCrossTalk_MonotonicSequences_AllErsDelivered()
    {
        var tmp = Directory.CreateTempSubdirectory("b3e2econc-").FullName;
        try
        {
            var sinks = new RecordingPacketSink[ChannelCount];
            for (int i = 0; i < ChannelCount; i++) sinks[i] = new RecordingPacketSink();
            var allowedSecIds = new HashSet<long>(Enumerable.Range(0, ChannelCount).Select(SecurityIdFor));

            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            };
            for (int i = 0; i < ChannelCount; i++)
            {
                cfg.Channels.Add(new ChannelConfig
                {
                    ChannelNumber = (byte)(40 + i),
                    IncrementalGroup = $"239.255.41.{40 + i}",
                    IncrementalPort = 30200 + i,
                    Ttl = 0,
                    InstrumentsFile = WriteInstrumentsFile(i, tmp),
                });
            }

            await using var host = new ExchangeHost(cfg,
                packetSinkFactory: ch =>
                {
                    int idx = ch.ChannelNumber - 40;
                    return sinks[idx];
                });
            await host.StartAsync();
            var ep = host.TcpEndpoint!;

            // Per-channel producer: a TCP session that fires N alternating
            // BUY/SELL orders against that channel's single instrument and
            // counts the ER_New responses it gets back.
            var ackedPerChannel = new int[ChannelCount];
            var producerTasks = new Task[ChannelCount];
            for (int chIdx = 0; chIdx < ChannelCount; chIdx++)
            {
                int captured = chIdx;
                producerTasks[captured] = Task.Run(async () =>
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(ep.Address, ep.Port);
                    var stream = client.GetStream();
                    long secId = SecurityIdFor(captured);

                    for (int n = 0; n < OrdersPerChannel; n++)
                    {
                        ulong clOrdId = (ulong)((captured + 1) * 10_000 + n);
                        // Stagger price levels per order so they REST instead of
                        // cross — the test asserts exactly one ER_New per order.
                        char side = (n % 2 == 0) ? '1' : '2';
                        // tickSize 0.01 → mantissa step must be 100. Stagger
                        // each order by one tick to keep them resting on
                        // distinct price levels.
                        long px = (n % 2 == 0) ? 100_0000L + (long)n * 100L : 200_0000L + (long)n * 100L;
                        var frame = BuildSimpleNewOrder(clOrdId, secId, side, '2', '0', qty: 1, priceMantissa: px);
                        await stream.WriteAsync(frame);
                    }

                    // Drain ER_New responses with a deadline.
                    int got = 0;
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                    while (got < OrdersPerChannel && DateTime.UtcNow < deadline)
                    {
                        var er = await ReadFrameAsync(stream, deadline - DateTime.UtcNow);
                        if (er.TemplateId == EntryPointFrameReader.TidExecutionReportNew) got++;
                    }
                    ackedPerChannel[captured] = got;
                });
            }

            await Task.WhenAll(producerTasks);

            // Allow a small window for the dispatch threads to flush any
            // pending UMDF packets before we assert on the sinks.
            await Task.Delay(200);

            // Assertion 1: every order acked.
            for (int i = 0; i < ChannelCount; i++)
                Assert.True(ackedPerChannel[i] == OrdersPerChannel,
                    $"channel {i}: expected {OrdersPerChannel} ER_New, got {ackedPerChannel[i]}");

            // Assertion 2: no cross-talk + monotonic per-channel sequence.
            for (int i = 0; i < ChannelCount; i++)
            {
                long mySecId = SecurityIdFor(i);
                uint lastSeq = 0;
                int packetCount = 0;
                foreach (var pkt in sinks[i].Packets)
                {
                    packetCount++;
                    // PacketHeader: SequenceNumber at offset 4 (UInt32 LE).
                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4));
                    Assert.True(seq > lastSeq,
                        $"channel {i}: non-monotonic SequenceNumber: prev={lastSeq} cur={seq}");
                    lastSeq = seq;

                    // Walk every Incremental SBE message in this packet and
                    // verify the SecurityID (when present) belongs to this
                    // channel and not to any other. Per-message layout:
                    //   FramingHeader(4) [messageLength,encodingType]
                    //   SbeMessageHeader(8) [blockLen, tid, schema, version]
                    //   body(blockLen)
                    int cursor = WireOffsets.PacketHeaderSize;
                    while (cursor + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize <= pkt.Length)
                    {
                        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(cursor, 2));
                        int sbeOffset = cursor + WireOffsets.FramingHeaderSize;
                        ushort blockLen = BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(sbeOffset, 2));
                        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(sbeOffset + 2, 2));
                        int bodyOffset = sbeOffset + WireOffsets.SbeMessageHeaderSize;
                        // Order/Trade/Snapshot frames carry SecurityID at
                        // body-offset 0 (Int64 LE) per the UMDF V16 layout.
                        // Skip ChannelReset (tid=11) and SequenceReset (tid=2)
                        // which don't carry an instrument id.
                        if (tid != 11 && tid != 2 && bodyOffset + 8 <= pkt.Length)
                        {
                            long secId = BinaryPrimitives.ReadInt64LittleEndian(pkt.AsSpan(bodyOffset, 8));
                            Assert.True(secId == mySecId || !allowedSecIds.Contains(secId),
                                $"channel {i} packet contained SecurityID {secId} that belongs to a different channel (expected {mySecId})");
                        }
                        cursor += messageLength;
                    }
                }
                Assert.True(packetCount > 0, $"channel {i}: no UMDF packets emitted");
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
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

    private readonly record struct ReadFrame(ushort TemplateId, ushort Version);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromMilliseconds(1);
        using var cts = new CancellationTokenSource(timeout);
        var headerBuf = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, headerBuf, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
        var sbeHeader = headerBuf.AsSpan(EntryPointFrameReader.SofhSize);
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(2, 2));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(6, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, version);
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
