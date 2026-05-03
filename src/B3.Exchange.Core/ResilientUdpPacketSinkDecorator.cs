using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Issue #172 — <see cref="IUmdfPacketSink"/> decorator that contains
/// publish-time exceptions raised by the underlying socket sink. Without
/// this wrapper, a transient network failure on the multicast publish
/// path (NIC down, route lost, ARP timeout) would propagate out of the
/// dispatcher's <c>FlushPacket</c> and abort the entire channel — the
/// market-data side counterpart of the dispatcher containment fix in
/// #170.
///
/// The decorator catches every <see cref="SocketException"/>, classifies
/// it into one of three buckets, increments the matching
/// <see cref="ChannelMetrics"/> counter
/// (<c>exch_umdf_publish_errors_total{kind=...}</c>), and logs at
/// <see cref="LogLevel.Warning"/> with a per-kind throttle so a sustained
/// outage does not flood the log. <see cref="ObjectDisposedException"/>
/// (race against <see cref="Dispose"/>) is also swallowed silently. Other
/// exceptions are re-thrown — they indicate logic errors that ops should
/// see immediately.
/// </summary>
public sealed class ResilientUdpPacketSinkDecorator : IUmdfPacketSink, IDisposable
{
    private readonly IUmdfPacketSink _inner;
    private readonly ChannelMetrics? _metrics;
    private readonly ILogger<ResilientUdpPacketSinkDecorator> _logger;

    // Per-kind log throttle: log only when the count crosses a power-of-two
    // boundary so the first error, then 2nd, 4th, 8th, ... are logged but a
    // sustained 100k/s flood is bounded to log2(N) lines.
    private long _logSocketError;
    private long _logHostUnreachable;
    private long _logMessageTooLarge;

    public ResilientUdpPacketSinkDecorator(
        IUmdfPacketSink inner,
        ILogger<ResilientUdpPacketSinkDecorator> logger,
        ChannelMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _logger = logger;
        _metrics = metrics;
    }

    public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
    {
        try
        {
            _inner.Publish(channelNumber, packet);
        }
        catch (ObjectDisposedException)
        {
            // Race with Dispose() during shutdown — swallow.
        }
        catch (SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                case SocketError.NetworkDown:
                case SocketError.HostDown:
                    _metrics?.IncPublishErrorHostUnreachable();
                    if (ShouldLog(ref _logHostUnreachable))
                        _logger.LogWarning(ex, "umdf publish: host unreachable on channel {Channel} (socketErr={SocketErr}); packet dropped",
                            channelNumber, ex.SocketErrorCode);
                    break;
                case SocketError.MessageSize:
                    _metrics?.IncPublishErrorMessageTooLarge();
                    if (ShouldLog(ref _logMessageTooLarge))
                        _logger.LogError(ex, "umdf publish: packet too large for transport on channel {Channel} (size={Size}); packet dropped — engine bug",
                            channelNumber, packet.Length);
                    break;
                default:
                    _metrics?.IncPublishErrorSocketError();
                    if (ShouldLog(ref _logSocketError))
                        _logger.LogWarning(ex, "umdf publish: socket error on channel {Channel} (socketErr={SocketErr}); packet dropped",
                            channelNumber, ex.SocketErrorCode);
                    break;
            }
        }
    }

    private static bool ShouldLog(ref long counter)
    {
        long n = Interlocked.Increment(ref counter);
        // True at n = 1, 2, 4, 8, 16, ... (n & (n-1)) == 0 picks powers of two.
        return (n & (n - 1)) == 0;
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
