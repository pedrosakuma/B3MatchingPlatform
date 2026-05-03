using B3.Exchange.Contracts;

namespace B3.Exchange.Core;

/// <summary>
/// Counts every packet/byte published through an <see cref="IUmdfPacketSink"/>
/// and forwards to the inner sink unchanged. Used to populate
/// <c>exch_umdf_packets_total{channel,feed}</c> and
/// <c>exch_umdf_bytes_total{channel,feed}</c> for the snapshot and
/// instrument-definition feeds (issue #174). The incremental feed is
/// counted directly inside <see cref="ChannelDispatcher.FlushPacket"/>
/// because it has access to the same packet length without an extra
/// decorator wrap.
/// </summary>
public sealed class CountingUdpPacketSinkDecorator : IUmdfPacketSink, IDisposable
{
    private readonly IUmdfPacketSink _inner;
    private readonly ChannelMetrics _metrics;
    private readonly UmdfFeedKind _feed;

    public CountingUdpPacketSinkDecorator(IUmdfPacketSink inner, ChannelMetrics metrics, UmdfFeedKind feed)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _feed = feed;
    }

    public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
    {
        // Count BEFORE delegating so a throwing inner sink does not mask
        // the measurement (we still observed the produce-side intent).
        _metrics.IncUmdfPacket(_feed, packet.Length);
        _inner.Publish(channelNumber, packet);
    }

    public void Dispose()
    {
        if (_inner is IDisposable d) d.Dispose();
    }
}
