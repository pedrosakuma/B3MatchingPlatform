using System.Runtime.InteropServices;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

public sealed class ExchangeHostTwoPhaseStartupTests
{
    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _packets = new();

        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (_gate) _packets.Add(packet.ToArray());
        }

        public byte[][] Snapshot()
        {
            lock (_gate) return _packets.Select(packet => (byte[])packet.Clone()).ToArray();
        }
    }

    [Fact]
    public async Task LaterChannelRecoveryFailure_PublishesNothing_AndConsumesPreparedEpoch()
    {
        string root = Path.Combine(AppContext.BaseDirectory,
            "host-two-phase-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "state");
        Directory.CreateDirectory(dataDir);
        try
        {
            const byte firstChannel = 71;
            const byte failingChannel = 72;
            const long firstSecurity = 900_000_071_001L;
            const long failingSecurity = 900_000_072_001L;
            string firstInstruments = WriteInstrument(root, firstChannel, firstSecurity);
            string failingInstruments = WriteInstrument(root, failingChannel, failingSecurity);

            var seeder = new FileChannelStatePersister(
                dataDir, NullLogger<FileChannelStatePersister>.Instance, generations: 1);
            seeder.Save(CreateSnapshot(firstChannel, firstSecurity, version: 7, invalidOwner: false));
            seeder.Save(CreateSnapshot(failingChannel, failingSecurity, version: 12, invalidOwner: true));

            var incremental = new Dictionary<byte, RecordingPacketSink>
            {
                [firstChannel] = new RecordingPacketSink(),
                [failingChannel] = new RecordingPacketSink(),
            };
            var snapshot = new RecordingPacketSink();
            var instrumentDefinition = new RecordingPacketSink();
            var failedConfig = BuildConfig(
                dataDir, firstInstruments, failingInstruments, includeFailingChannel: true,
                enablePublishers: true);

            await using (var failedHost = new ExchangeHost(
                failedConfig,
                packetSinkFactory: channel => incremental[channel.ChannelNumber],
                snapshotSinkFactory: (_, _) => snapshot,
                instrumentDefSinkFactory: (_, _) => instrumentDefinition))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => failedHost.StartAsync());
                Assert.Contains("snapshot owner refers to orderId", error.Message);

                await Task.Delay(200);
                Assert.Null(failedHost.TcpEndpoint);
                Assert.Empty(incremental[firstChannel].Snapshot());
                Assert.Empty(incremental[failingChannel].Snapshot());
                Assert.Empty(snapshot.Snapshot());
                Assert.Empty(instrumentDefinition.Snapshot());
            }

            var afterFailedBoot = new FileChannelStatePersister(
                dataDir, NullLogger<FileChannelStatePersister>.Instance, generations: 1)
                .TryLoad(firstChannel);
            Assert.NotNull(afterFailedBoot);
            Assert.Equal((ushort)8, afterFailedBoot!.SequenceVersion);
            Assert.Equal(0u, afterFailedBoot.SequenceNumber);

            var restartSink = new RecordingPacketSink();
            var restartConfig = BuildConfig(
                dataDir, firstInstruments, failingInstruments, includeFailingChannel: false,
                enablePublishers: false);
            await using var restartedHost = new ExchangeHost(
                restartConfig,
                packetSinkFactory: _ => restartSink);
            await restartedHost.StartAsync();

            var reset = Assert.Single(restartSink.Snapshot());
            Assert.Equal((ushort)9, MemoryMarshal.Read<ushort>(
                reset.AsSpan(WireOffsets.PacketHeaderSequenceVersionOffset, 2)));
            Assert.Equal(1u, MemoryMarshal.Read<uint>(
                reset.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4)));
            int sbeHeaderOffset = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize;
            Assert.Equal((ushort)11, MemoryMarshal.Read<ushort>(
                reset.AsSpan(sbeHeaderOffset + 2, 2)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static HostConfig BuildConfig(
        string dataDir,
        string firstInstruments,
        string failingInstruments,
        bool includeFailingChannel,
        bool enablePublishers)
    {
        var config = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
        };
        config.Channels.Add(new ChannelConfig
        {
            ChannelNumber = 71,
            IncrementalGroup = "239.255.71.1",
            IncrementalPort = 30711,
            Ttl = 0,
            InstrumentsFile = firstInstruments,
            Persistence = new PersistenceConfig { DataDir = dataDir, Generations = 1 },
            Snapshot = enablePublishers
                ? new SnapshotChannelConfig
                {
                    Group = "239.255.71.2",
                    Port = 31711,
                    Ttl = 0,
                    CadenceMs = 1,
                }
                : null,
            InstrumentDefinition = enablePublishers
                ? new InstrumentDefinitionConfig
                {
                    ChannelNumber = 171,
                    Group = "239.255.71.3",
                    Port = 32711,
                    Ttl = 0,
                    CadenceMs = 1,
                }
                : null,
            PriceBandPublishIntervalMs = enablePublishers ? 1 : 0,
        });
        if (includeFailingChannel)
        {
            config.Channels.Add(new ChannelConfig
            {
                ChannelNumber = 72,
                IncrementalGroup = "239.255.72.1",
                IncrementalPort = 30712,
                Ttl = 0,
                InstrumentsFile = failingInstruments,
                Persistence = new PersistenceConfig { DataDir = dataDir, Generations = 1 },
            });
        }
        return config;
    }

    private static string WriteInstrument(string root, byte channel, long securityId)
    {
        string path = Path.Combine(root, $"instruments-{channel}.json");
        File.WriteAllText(path, $$"""
        [
          {
            "symbol": "T{{channel}}",
            "securityId": {{securityId}},
            "tickSize": "0.01",
            "lotSize": 1,
            "minPx": "0.01",
            "maxPx": "1000.00",
            "lowerPriceBand": "1.00",
            "upperPriceBand": "999.00",
            "currency": "BRL",
            "isin": "BRTEST{{channel}}000",
            "securityType": "CS"
          }
        ]
        """);
        return path;
    }

    private static ChannelStateSnapshot CreateSnapshot(
        byte channel,
        long securityId,
        ushort version,
        bool invalidOwner)
    {
        var engine = new EngineStateSnapshot(
            NextOrderId: 1,
            NextTradeId: 1,
            RptSeq: 0,
            Phases: [new EngineStateSnapshot.PhaseEntry(securityId, TradingPhase.Open)],
            Books: [new EngineStateSnapshot.BookSnapshot(securityId, [])]);
        IReadOnlyList<OrderOwnerSnapshot> owners = invalidOwner
            ? [new OrderOwnerSnapshot(99, "57099", 7, 99, Side.Buy, securityId)]
            : [];
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: channel,
            SequenceNumber: 44,
            SequenceVersion: version,
            Engine: engine,
            Owners: owners);
    }
}
