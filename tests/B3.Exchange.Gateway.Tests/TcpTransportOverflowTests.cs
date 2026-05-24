using B3.Exchange.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

public class TcpTransportOverflowTests
{
    [Fact]
    public async Task TryEnqueueFrame_WhenQueueIsFull_ReturnsFalseClosesTransportAndReleasesPooledReference()
    {
        string? closeReason = null;
        await using var transport = new TcpTransport(
            connectionId: 1,
            stream: new MemoryStream(),
            logger: NullLogger<TcpTransport>.Instance,
            sendQueueCapacity: 1,
            onClose: reason => closeReason = reason);

        Assert.True(transport.TryEnqueueFrame(new byte[] { 1 }));

        var pooled = PooledOutboundFrame.Rent(4);
        pooled.Span.Fill(0x42);

        Assert.False(transport.TryEnqueueFrame(pooled));
        Assert.False(transport.IsOpen);
        Assert.Equal("send-queue-full", closeReason);

        pooled.Release();
        Assert.Equal(0, pooled.Length);
        Assert.Empty(pooled.Buffer);
    }
}
