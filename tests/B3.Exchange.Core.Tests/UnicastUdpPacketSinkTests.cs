using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Core.Tests;

public class UnicastUdpPacketSinkTests
{
    [Fact]
    public void Publish_DeliversPacket_ToConfiguredLoopbackUdpEndpoint()
    {
        // Bind a transient receiver on loopback to capture what the sink sends.
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.ReceiveTimeout = 2000;
        var port = ((IPEndPoint)receiver.LocalEndPoint!).Port;

        using var sink = new UnicastUdpPacketSink("127.0.0.1", port,
            NullLogger<UnicastUdpPacketSink>.Instance);

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        sink.Publish(channelNumber: 84, payload);

        var buf = new byte[64];
        var received = receiver.Receive(buf);
        Assert.Equal(payload.Length, received);
        Assert.Equal(payload, buf.AsSpan(0, received).ToArray());
    }

    [Fact]
    public void Constructor_Throws_WhenHostnameDoesNotResolve()
    {
        // RFC 6761 reserves `.invalid` so this is guaranteed never to resolve.
        Assert.ThrowsAny<Exception>(() =>
            new UnicastUdpPacketSink("nonexistent-host.invalid", 30084,
                NullLogger<UnicastUdpPacketSink>.Instance));
    }
}
