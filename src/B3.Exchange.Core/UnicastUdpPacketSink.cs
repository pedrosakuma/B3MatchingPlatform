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
/// </summary>
public sealed class UnicastUdpPacketSink : IUmdfPacketSink, IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _destination;
    private readonly ILogger<UnicastUdpPacketSink> _logger;
    private readonly string _originalHost;

    public UnicastUdpPacketSink(string host, int port, ILogger<UnicastUdpPacketSink> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _originalHost = host;
        // Resolve once at construction. If the operator wants follow-the-DNS
        // semantics they should restart the host (config reload is not
        // currently a feature). Picking the first AddressFamily.InterNetwork
        // record keeps behaviour aligned with MulticastUdpPacketSink which
        // is hard-coded to IPv4.
        var addresses = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
        if (addresses.Length == 0)
            throw new InvalidOperationException(
                $"unicast UDP sink: DNS lookup for '{host}' returned no IPv4 addresses");
        _destination = new IPEndPoint(addresses[0], port);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _logger.LogInformation(
            "unicast UDP sink configured to send to {Host} ({Address}):{Port}",
            host, addresses[0], port);
    }

    public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
    {
        _socket.SendTo(packet, SocketFlags.None, _destination);
    }

    public void Dispose() => _socket.Dispose();
}
