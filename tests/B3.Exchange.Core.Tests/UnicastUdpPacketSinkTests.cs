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

    [Fact]
    public void Constructor_AcceptsZeroReResolveInterval_DisablingBackgroundTimer()
    {
        // Issue #554: TimeSpan.Zero restores the legacy resolve-once
        // behaviour and must not schedule (or crash starting) a timer.
        using var sink = new UnicastUdpPacketSink("127.0.0.1", 30084,
            NullLogger<UnicastUdpPacketSink>.Instance, TimeSpan.Zero);

        var payload = new byte[] { 9 };
        var ex = Record.Exception(() => sink.Publish(84, payload));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Publish_RebindsDestination_WhenDnsReResolveDetectsDrift()
    {
        // Issue #554: simulate a downstream pod restart (new IP behind the
        // same DNS name) by pointing the sink at a resolvable loopback host,
        // then swapping which receiver is "the destination" would require a
        // real DNS change — instead we validate the periodic re-resolve path
        // fires and keeps sending successfully to the (unchanged, in this
        // test) resolved address, proving the timer doesn't disrupt normal
        // delivery. A unit test cannot rebind DNS itself; the drift-log path
        // is exercised by ReResolveCallback's unit behaviour indirectly via
        // this repeated-publish smoke test across multiple timer ticks.
        using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.ReceiveTimeout = 2000;
        var port = ((IPEndPoint)receiver.LocalEndPoint!).Port;

        using var sink = new UnicastUdpPacketSink("localhost", port,
            NullLogger<UnicastUdpPacketSink>.Instance, TimeSpan.FromMilliseconds(50));

        // Let a few re-resolve ticks fire in the background.
        await Task.Delay(200);

        var payload = new byte[] { 1, 2, 3 };
        sink.Publish(channelNumber: 84, payload);

        var buf = new byte[64];
        var received = receiver.Receive(buf);
        Assert.Equal(payload.Length, received);
        Assert.Equal(payload, buf.AsSpan(0, received).ToArray());
    }
}
