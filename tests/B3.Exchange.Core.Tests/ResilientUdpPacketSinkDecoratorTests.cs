using System.Net.Sockets;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Issue #172 — UDP publish errors must be contained inside the sink:
/// a SocketException from the underlying transport must NOT propagate
/// out of <see cref="IUmdfPacketSink.Publish"/>; it must bump the right
/// per-kind counter on <see cref="ChannelMetrics"/> and continue. The
/// dispatcher loop is the consumer of this guarantee — without it, any
/// transient route loss would kill the channel just like the engine
/// crash class fixed in #170.
/// </summary>
public class ResilientUdpPacketSinkDecoratorTests
{
    private sealed class ThrowingSink : IUmdfPacketSink
    {
        public Exception? NextThrow;
        public int PublishCalls;
        public int SuccessfulPublishCalls;
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            PublishCalls++;
            if (NextThrow is { } ex) { NextThrow = null; throw ex; }
            SuccessfulPublishCalls++;
        }
    }

    private static (ResilientUdpPacketSinkDecorator dec, ThrowingSink inner, ChannelMetrics m) Build()
    {
        var inner = new ThrowingSink();
        var m = new ChannelMetrics(channelNumber: 7);
        var dec = new ResilientUdpPacketSinkDecorator(
            inner, NullLogger<ResilientUdpPacketSinkDecorator>.Instance, m);
        return (dec, inner, m);
    }

    [Fact]
    public void Publish_HappyPath_ForwardsToInner_NoMetric()
    {
        var (dec, inner, m) = Build();
        dec.Publish(7, new byte[] { 1, 2, 3 });
        Assert.Equal(1, inner.SuccessfulPublishCalls);
        Assert.Equal(0, m.PublishErrors);
        Assert.Equal(0, m.PublishErrorsHostUnreachable);
        Assert.Equal(0, m.PublishErrorsMessageTooLarge);
    }

    [Fact]
    public void Publish_HostUnreachableSocketException_Caught_AndKindCounterIncrements()
    {
        var (dec, inner, m) = Build();
        inner.NextThrow = new SocketException((int)SocketError.HostUnreachable);
        dec.Publish(7, new byte[] { 1, 2, 3 });
        Assert.Equal(1, m.PublishErrorsHostUnreachable);
        Assert.Equal(0, m.PublishErrors);
        Assert.Equal(0, m.PublishErrorsMessageTooLarge);

        // Loop survives — next publish goes through.
        dec.Publish(7, new byte[] { 9, 9 });
        Assert.Equal(1, inner.SuccessfulPublishCalls);
    }

    [Fact]
    public void Publish_MessageSizeSocketException_Caught_AsMessageTooLarge()
    {
        var (dec, inner, m) = Build();
        inner.NextThrow = new SocketException((int)SocketError.MessageSize);
        dec.Publish(7, new byte[] { 1 });
        Assert.Equal(1, m.PublishErrorsMessageTooLarge);
        Assert.Equal(0, m.PublishErrorsHostUnreachable);
        Assert.Equal(0, m.PublishErrors);
    }

    [Fact]
    public void Publish_OtherSocketException_Caught_AsGenericSocketError()
    {
        var (dec, inner, m) = Build();
        inner.NextThrow = new SocketException((int)SocketError.ConnectionReset);
        dec.Publish(7, new byte[] { 1 });
        Assert.Equal(1, m.PublishErrors);
        Assert.Equal(0, m.PublishErrorsHostUnreachable);
        Assert.Equal(0, m.PublishErrorsMessageTooLarge);
    }

    [Fact]
    public void Publish_ObjectDisposedException_SilentlySwallowed_NoMetric()
    {
        // Race with Dispose during shutdown is benign — no counter, no log.
        var (dec, inner, m) = Build();
        inner.NextThrow = new ObjectDisposedException("Socket");
        dec.Publish(7, new byte[] { 1 });
        Assert.Equal(0, m.PublishErrors);
        Assert.Equal(0, m.PublishErrorsHostUnreachable);
        Assert.Equal(0, m.PublishErrorsMessageTooLarge);
    }

    [Fact]
    public void Publish_NonSocketException_PropagatesUp()
    {
        // Logic errors must be visible immediately — never silently dropped.
        var (dec, inner, _) = Build();
        inner.NextThrow = new InvalidOperationException("logic bug");
        Assert.Throws<InvalidOperationException>(() => dec.Publish(7, new byte[] { 1 }));
    }

    [Fact]
    public void Publish_SustainedFailure_CounterMonotonic_NoExceptionEscapes()
    {
        // 100 publishes all fail with HostUnreachable; counter is exact,
        // no exception escapes. Log throttling is a side benefit (we don't
        // observe the log here — just the contract that the loop survives).
        var (dec, inner, m) = Build();
        for (int i = 0; i < 100; i++)
        {
            inner.NextThrow = new SocketException((int)SocketError.HostUnreachable);
            dec.Publish(7, new byte[] { (byte)i });
        }
        Assert.Equal(100, m.PublishErrorsHostUnreachable);
    }
}
