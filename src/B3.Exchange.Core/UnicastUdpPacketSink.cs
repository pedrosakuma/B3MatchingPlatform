using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Bridge-network friendly <see cref="IUmdfPacketSink"/> backed by a plain
/// UDP unicast socket. Resolves the destination via DNS on construction
/// and sends every <see cref="Publish"/> as a synchronous <c>SendTo</c>.
///
/// Use this sink when the downstream UMDF consumer (e.g. the
/// <c>B3MarketDataPlatform</c> service) is reachable on a routable address
/// — typically a service name in the same docker-compose project — but a
/// multicast group is not. Wire format is identical to the multicast
/// path; only the L3 destination changes.
///
/// Issue #554 — the initial construction-time-only resolve silently
/// blackholed the feed on any downstream pod restart (new IP, same DNS
/// name), with zero signal on the sending side (UDP <c>SendTo</c> to a
/// stale-but-still-routable address just vanishes). A background timer
/// now re-resolves the configured host on a periodic interval and rebinds
/// the destination when the resolved address drifts, logging a warning
/// either way so an operator doesn't need packet-capture-level debugging
/// to notice DNS staleness.
/// </summary>
public sealed class UnicastUdpPacketSink : IUmdfPacketSink, IDisposable
{
    private static readonly TimeSpan DefaultReResolveInterval = TimeSpan.FromSeconds(15);

    private readonly Socket _socket;
    private readonly ILogger<UnicastUdpPacketSink> _logger;
    private readonly string _originalHost;
    private readonly int _port;
    private readonly TimeSpan _reResolveInterval;
    private readonly Timer? _reResolveTimer;
    private volatile bool _disposed;

    // Reference-type field: assignment is atomic, so Publish (dispatch
    // thread) reading it concurrently with the timer callback swapping it
    // (thread-pool thread) never observes a torn value. `volatile` keeps the
    // reader from caching a stale reference in a register.
    private volatile IPEndPoint _destination;

    public UnicastUdpPacketSink(string host, int port, ILogger<UnicastUdpPacketSink> logger)
        : this(host, port, logger, DefaultReResolveInterval)
    {
    }

    /// <param name="reResolveInterval">
    /// How often to re-resolve <paramref name="host"/> in the background and
    /// rebind the destination if the address changed. Pass
    /// <see cref="TimeSpan.Zero"/> (or negative) to disable periodic
    /// re-resolution and fall back to the legacy resolve-once-at-construction
    /// behaviour.
    /// </param>
    public UnicastUdpPacketSink(string host, int port, ILogger<UnicastUdpPacketSink> logger,
        TimeSpan reResolveInterval)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _originalHost = host;
        _port = port;
        _reResolveInterval = reResolveInterval;
        // Picking the first AddressFamily.InterNetwork record keeps
        // behaviour aligned with MulticastUdpPacketSink, which is
        // hard-coded to IPv4.
        var addresses = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
        if (addresses.Length == 0)
            throw new InvalidOperationException(
                $"unicast UDP sink: DNS lookup for '{host}' returned no IPv4 addresses");
        _destination = new IPEndPoint(addresses[0], port);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _logger.LogInformation(
            "unicast UDP sink configured to send to {Host} ({Address}):{Port}",
            host, addresses[0], port);

        if (reResolveInterval > TimeSpan.Zero)
        {
            // Non-recurring: the callback reschedules itself (in `finally`)
            // only after it finishes. A recurring `Timer(..., period, period)`
            // can fire again before a slow DNS lookup returns, letting an
            // older/stale resolution overwrite a newer one on `_destination`
            // once both callbacks complete out of order.
            _reResolveTimer = new Timer(ReResolveCallback, null, reResolveInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReResolveCallback(object? state)
    {
        try
        {
            IPAddress freshAddress;
            try
            {
                var addresses = Dns.GetHostAddresses(_originalHost, AddressFamily.InterNetwork);
                if (addresses.Length == 0)
                {
                    _logger.LogWarning(
                        "unicast UDP sink: re-resolve of '{Host}' returned no IPv4 addresses; keeping cached destination {Address}:{Port}",
                        _originalHost, _destination.Address, _port);
                    return;
                }
                freshAddress = addresses[0];
            }
            catch (Exception ex)
            {
                // Best-effort background re-resolve: a transient DNS outage must
                // not take down the sink or crash the timer thread. Keep sending
                // to the last-known-good address and try again next tick.
                _logger.LogWarning(ex,
                    "unicast UDP sink: re-resolve of '{Host}' failed; keeping cached destination {Address}:{Port}",
                    _originalHost, _destination.Address, _port);
                return;
            }

            var cached = _destination;
            if (!freshAddress.Equals(cached.Address))
            {
                _logger.LogWarning(
                    "unicast UDP sink: DNS drift detected for '{Host}' ({OldAddress} -> {NewAddress}); rebinding destination:{Port}",
                    _originalHost, cached.Address, freshAddress, _port);
                _destination = new IPEndPoint(freshAddress, _port);
            }
        }
        finally
        {
            // Rearm only if we haven't been disposed concurrently — `Change`
            // on a disposed Timer throws ObjectDisposedException, which would
            // otherwise escape on the thread-pool thread.
            if (!_disposed)
            {
                try
                {
                    _reResolveTimer?.Change(_reResolveInterval, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Race with Dispose() — nothing left to reschedule.
                }
            }
        }
    }

    public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
    {
        _socket.SendTo(packet, SocketFlags.None, _destination);
    }

    public void Dispose()
    {
        _disposed = true;
        _reResolveTimer?.Dispose();
        _socket.Dispose();
    }
}
