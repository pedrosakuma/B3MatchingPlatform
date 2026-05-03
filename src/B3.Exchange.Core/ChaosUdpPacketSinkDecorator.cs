using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// <see cref="IUmdfPacketSink"/> decorator that injects controlled packet loss,
/// duplication, and reordering into an inner sink. Designed for testing the
/// companion <c>SbeB3UmdfConsumer</c>'s recovery paths (snapshot bootstrap,
/// gap detection) without external network-shaping tools.
///
/// <para>NOT thread-safe; like the inner sinks it expects to be called from a
/// single channel-thread caller (one <see cref="ChannelDispatcher"/> per sink
/// instance). Counters are exposed via <see cref="ChannelMetrics.IncChaosDropped"/>
/// etc.</para>
/// </summary>
public sealed class ChaosUdpPacketSinkDecorator : IUmdfPacketSink, IDisposable
{
    private readonly IUmdfPacketSink _inner;
    private readonly ChaosConfig _config;
    private readonly ChannelMetrics? _metrics;
    private readonly ILogger<ChaosUdpPacketSinkDecorator> _logger;
    private readonly Random _rng;
    // Tiny FIFO of (countdown, payload) for reordered packets. Capacity is
    // bounded by ReorderMaxLagPackets so memory stays trivial.
    private readonly List<DelayedPacket> _delayed;

    public ChaosUdpPacketSinkDecorator(
        IUmdfPacketSink inner,
        ChaosConfig config,
        ILogger<ChaosUdpPacketSinkDecorator> logger,
        ChannelMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        config.Validate();
        _inner = inner;
        _config = config;
        _metrics = metrics;
        _logger = logger;
        _rng = new Random(config.Seed);
        _delayed = new List<DelayedPacket>(capacity: Math.Max(1, config.ReorderMaxLagPackets));
        _logger.LogInformation(
            "chaos decorator active: drop={Drop:P2} dup={Dup:P2} reorder={Reorder:P2} maxLag={MaxLag} seed={Seed}",
            config.DropProbability, config.DuplicateProbability, config.ReorderProbability,
            config.ReorderMaxLagPackets, config.Seed);
    }

    public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
    {
        // Drop: decide first; a dropped packet still counts for reorder lag
        // bookkeeping (i.e. it occupies a slot in the wire stream we just
        // chose not to fill). Drained reordered packets that were due before
        // this slot are still emitted so dropping a packet does not freeze
        // the reorder queue.
        if (_config.DropProbability > 0 && _rng.NextDouble() < _config.DropProbability)
        {
            _metrics?.IncChaosDropped();
            DrainDueDelayed(channelNumber);
            return;
        }

        // Reorder: hold this packet aside; emit later. Drain any other
        // delayed packets whose lag has expired before this one.
        if (_config.ReorderProbability > 0 && _rng.NextDouble() < _config.ReorderProbability)
        {
            int lag = 1 + _rng.Next(_config.ReorderMaxLagPackets);
            _delayed.Add(new DelayedPacket(channelNumber, packet.ToArray(), lag));
            _metrics?.IncChaosReordered();
            DrainDueDelayed(channelNumber);
            return;
        }

        // Send + maybe duplicate.
        _inner.Publish(channelNumber, packet);
        if (_config.DuplicateProbability > 0 && _rng.NextDouble() < _config.DuplicateProbability)
        {
            _inner.Publish(channelNumber, packet);
            _metrics?.IncChaosDuplicated();
        }

        DrainDueDelayed(channelNumber);
    }

    private void DrainDueDelayed(byte currentChannel)
    {
        if (_delayed.Count == 0) return;
        // Decrement every entry; emit those that hit 0. We iterate by index
        // so removal does not invalidate subsequent indices.
        for (int i = 0; i < _delayed.Count;)
        {
            var d = _delayed[i];
            d.Lag--;
            if (d.Lag <= 0)
            {
                _inner.Publish(d.ChannelNumber, d.Payload);
                _delayed.RemoveAt(i);
            }
            else
            {
                _delayed[i] = d;
                i++;
            }
        }
    }

    public void Dispose()
    {
        // Flush any remaining delayed packets so we don't silently lose them
        // on shutdown. The inner sink's Dispose still happens via host
        // wiring (decorator does not own the inner socket).
        for (int i = 0; i < _delayed.Count; i++)
            _inner.Publish(_delayed[i].ChannelNumber, _delayed[i].Payload);
        _delayed.Clear();
    }

    private struct DelayedPacket
    {
        public byte ChannelNumber;
        public byte[] Payload;
        public int Lag;
        public DelayedPacket(byte ch, byte[] payload, int lag) { ChannelNumber = ch; Payload = payload; Lag = lag; }
    }
}
