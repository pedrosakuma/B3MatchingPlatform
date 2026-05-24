using System.Buffers;
using System.Collections.Concurrent;

namespace B3.Exchange.Gateway;

/// <summary>
/// Reference-counted, ArrayPool-backed outbound frame. The encoder, transport
/// queue, and retransmit ring can share the same bytes and only return them to
/// the pool after the last asynchronous consumer releases its reference.
/// </summary>
internal sealed class PooledOutboundFrame
{
    private static readonly ConcurrentStack<PooledOutboundFrame> s_framePool = new();

    private int _refCount;

    private PooledOutboundFrame()
    {
        Buffer = Array.Empty<byte>();
    }

    public byte[] Buffer { get; private set; }

    public int Length { get; private set; }

    public Span<byte> Span => Buffer.AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(0, Length);

    public static PooledOutboundFrame Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!s_framePool.TryPop(out var frame))
        {
            frame = new PooledOutboundFrame();
        }

        frame.Buffer = ArrayPool<byte>.Shared.Rent(length);
        frame.Length = length;
        Volatile.Write(ref frame._refCount, 1);
        return frame;
    }

    public void AddRef()
    {
        int refs = Interlocked.Increment(ref _refCount);
        if (refs <= 1)
        {
            throw new InvalidOperationException("Cannot retain a released outbound frame.");
        }
    }

    public void Release()
    {
        int refs = Interlocked.Decrement(ref _refCount);
        if (refs > 0) return;
        if (refs < 0)
        {
            throw new InvalidOperationException("Outbound frame released too many times.");
        }

        byte[] buffer = Buffer;
        Buffer = Array.Empty<byte>();
        Length = 0;
        ArrayPool<byte>.Shared.Return(buffer);
        s_framePool.Push(this);
    }
}
