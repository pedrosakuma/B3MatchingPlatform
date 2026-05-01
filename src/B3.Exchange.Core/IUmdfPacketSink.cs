namespace B3.Exchange.Core;

/// <summary>
/// Sink for fully-formed UMDF incremental packets ready for the wire.
/// Implementations are typically a multicast UDP socket; tests substitute
/// an in-memory recorder.
///
/// Implementations MUST be safe to call from a single channel-thread caller
/// (no internal synchronization required as long as exactly one
/// <see cref="ChannelDispatcher"/> targets a given sink).
/// </summary>
public interface IUmdfPacketSink
{
    /// <summary>Synchronously enqueue/send the packet bytes.</summary>
    void Publish(byte channelNumber, ReadOnlySpan<byte> packet);
}
