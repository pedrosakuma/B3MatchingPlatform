namespace B3.Exchange.Core.Tests;

public class ChaosUdpPacketSinkDecoratorTests
{
    private sealed class RecordingSink : IUmdfPacketSink
    {
        public List<(byte Channel, byte[] Bytes)> Sent { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
            => Sent.Add((channelNumber, packet.ToArray()));
    }

    private static byte[] Pkt(byte tag) => new[] { tag, (byte)0xAA, (byte)0xBB };

    [Fact]
    public void DropProbabilityOne_NoPacketsLeaveSink()
    {
        var inner = new RecordingSink();
        using var chaos = new ChaosUdpPacketSinkDecorator(
            inner,
            new ChaosConfig { DropProbability = 1.0, Seed = 1 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosUdpPacketSinkDecorator>.Instance);

        for (int i = 0; i < 10; i++)
            chaos.Publish(1, Pkt((byte)i));

        Assert.Empty(inner.Sent);
    }

    [Fact]
    public void DuplicateProbabilityOne_EmitsTwoCopies()
    {
        var inner = new RecordingSink();
        using var chaos = new ChaosUdpPacketSinkDecorator(
            inner,
            new ChaosConfig { DuplicateProbability = 1.0, Seed = 2 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosUdpPacketSinkDecorator>.Instance);

        chaos.Publish(1, Pkt(0xCC));
        chaos.Publish(1, Pkt(0xDD));

        Assert.Equal(4, inner.Sent.Count);
        Assert.Equal(0xCC, inner.Sent[0].Bytes[0]);
        Assert.Equal(0xCC, inner.Sent[1].Bytes[0]);
        Assert.Equal(0xDD, inner.Sent[2].Bytes[0]);
        Assert.Equal(0xDD, inner.Sent[3].Bytes[0]);
    }

    [Fact]
    public void NoChaos_ActsAsPassthrough()
    {
        var inner = new RecordingSink();
        using var chaos = new ChaosUdpPacketSinkDecorator(
            inner,
            new ChaosConfig(), // all probabilities 0
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosUdpPacketSinkDecorator>.Instance);

        for (int i = 0; i < 5; i++) chaos.Publish(7, Pkt((byte)i));

        Assert.Equal(5, inner.Sent.Count);
        Assert.All(inner.Sent, p => Assert.Equal(7, p.Channel));
    }

    [Fact]
    public void ReorderProbabilityOne_DefersPacketByAtMostMaxLag()
    {
        // With probability 1 and maxLag = 2, every packet enters the delay queue
        // at lag in [1,2]. Sequence of 6 inputs should still produce 6 outputs
        // (no loss), but order is shuffled and order-of-emission is bounded
        // by the maxLag window.
        var inner = new RecordingSink();
        using var chaos = new ChaosUdpPacketSinkDecorator(
            inner,
            new ChaosConfig { ReorderProbability = 1.0, ReorderMaxLagPackets = 2, Seed = 42 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosUdpPacketSinkDecorator>.Instance);

        for (int i = 0; i < 6; i++) chaos.Publish(1, Pkt((byte)i));

        // After 6 publishes, some may still be pending; flush via Dispose.
        // Capture count before flush + after to sanity-check.
        int before = inner.Sent.Count;
        // Re-dispose explicitly via a using-scope to flush.
        chaos.Dispose();
        int after = inner.Sent.Count;

        Assert.Equal(6, after);
        Assert.True(after >= before);

        // Order: a packet emitted at index k must have been queued at most
        // maxLag (=2) Publish-calls before its emission. Verify no packet
        // index advances more than maxLag positions backward.
        // Simpler: each emitted index must be within ±maxLag of its arrival.
        for (int k = 0; k < after; k++)
        {
            int arrived = inner.Sent[k].Bytes[0];
            int diff = arrived - k;
            Assert.True(Math.Abs(diff) <= 2,
                $"packet {arrived} emitted at slot {k} (diff={diff}) exceeds maxLag=2");
        }
    }

    [Fact]
    public void DroppedPacket_DoesNotFreezeReorderQueue()
    {
        // Mix drop=0.5 + reorder=0.5 with a fixed seed; assert reorder queue
        // drains by Dispose-time so no packet is "stuck" forever.
        var inner = new RecordingSink();
        using (var chaos = new ChaosUdpPacketSinkDecorator(
            inner,
            new ChaosConfig
            {
                DropProbability = 0.5,
                ReorderProbability = 0.5,
                ReorderMaxLagPackets = 2,
                Seed = 7,
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosUdpPacketSinkDecorator>.Instance))
        {
            for (int i = 0; i < 20; i++) chaos.Publish(1, Pkt((byte)i));
        }
        // Dispose flushes any pending delayed packets — assert >0 emitted (the seed
        // shouldn't drop everything).
        Assert.True(inner.Sent.Count > 0);
    }

    [Fact]
    public void IncrementsMetricsCounters()
    {
        var inner = new RecordingSink();
        var metrics = new ChannelMetrics(channelNumber: 9);
        using var chaos = new ChaosUdpPacketSinkDecorator(
            inner,
            new ChaosConfig { DropProbability = 1.0, Seed = 1 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChaosUdpPacketSinkDecorator>.Instance,
            metrics);

        for (int i = 0; i < 4; i++) chaos.Publish(9, Pkt((byte)i));

        Assert.Equal(4, metrics.ChaosDropped);
        Assert.Equal(0, metrics.ChaosDuplicated);
        Assert.Equal(0, metrics.ChaosReordered);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_RejectsOutOfRangeProbability(double p)
    {
        var cfg = new ChaosConfig { DropProbability = p };
        Assert.Throws<ArgumentException>(cfg.Validate);
    }

    [Fact]
    public void Validate_ReorderRequiresPositiveMaxLag()
    {
        var cfg = new ChaosConfig { ReorderProbability = 0.5, ReorderMaxLagPackets = 0 };
        Assert.Throws<ArgumentException>(cfg.Validate);
    }
}
