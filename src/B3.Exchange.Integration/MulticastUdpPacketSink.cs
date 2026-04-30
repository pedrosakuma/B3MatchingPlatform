using System.Net;
using System.Net.Sockets;

namespace B3.Exchange.Integration;

/// <summary>
/// Production <see cref="IUmdfPacketSink"/> backed by a UDP multicast socket.
/// One instance binds the socket; <see cref="Publish"/> is a synchronous
/// <c>SendTo</c> with no internal buffering. Drop the instance to release
/// the socket.
/// </summary>
public sealed class MulticastUdpPacketSink : IUmdfPacketSink, IDisposable
{
    private readonly Socket _socket;
    private readonly IPEndPoint _destination;

    public MulticastUdpPacketSink(IPAddress group, int port, IPAddress? localInterface = null, byte ttl = 1)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (localInterface != null)
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localInterface.GetAddressBytes());
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
        _destination = new IPEndPoint(group, port);
    }

    public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
    {
        // Span overload is supported on Socket.SendTo since .NET 6.
        _socket.SendTo(packet, SocketFlags.None, _destination);
    }

    public void Dispose() => _socket.Dispose();
}
