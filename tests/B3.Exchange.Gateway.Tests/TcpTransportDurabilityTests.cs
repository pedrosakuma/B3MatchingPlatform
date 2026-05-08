using B3.Exchange.Contracts;
using B3.Exchange.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #312: the send loop MUST not write a frame whose
/// <see cref="DurabilityHandle"/> is active until the barrier
/// signals durability for the frame's seq. Frames carrying
/// <see cref="DurabilityHandle.None"/> bypass the wait and hit
/// the wire immediately (preserving the pre-#312 fast path for
/// keep-alives, SessionReject, etc.).
/// </summary>
public class TcpTransportDurabilityTests
{
    /// <summary>
    /// Releases <see cref="WaitForDurable"/> only when
    /// <see cref="Release"/> is called. The send loop must observe
    /// "frame waited" semantics for any seq &gt; 0.
    /// </summary>
    private sealed class GatedBarrier : IDurabilityBarrier
    {
        private readonly ManualResetEventSlim _gate = new(false);
        public int WaitCount;
        public long LastAwaitedSeq;
        public void Release() => _gate.Set();
        public void WaitForDurable(long seq, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref WaitCount);
            Volatile.Write(ref LastAwaitedSeq, seq);
            _gate.Wait(cancellationToken);
        }
    }

    [Fact]
    public async Task SendLoop_BlocksFrameUntilBarrierSignalsDurability()
    {
        // Pair a MemoryStream with a TaskCompletionSource we set on first write.
        using var ms = new MemoryStream();
        using var observed = new SemaphoreSlim(0, 1);
        var observingStream = new ObservingMemoryStream(ms, observed);
        await using var transport = new TcpTransport(
            connectionId: 1, stream: observingStream,
            logger: NullLogger<TcpTransport>.Instance,
            sendQueueCapacity: 8);
        using var cts = new CancellationTokenSource();
        transport.StartSendLoop(cts.Token);

        var barrier = new GatedBarrier();
        var frame = new byte[] { 1, 2, 3, 4 };
        Assert.True(transport.TryEnqueueFrame(frame, new DurabilityHandle(barrier, Seq: 7)));

        // Frame must NOT have been written yet — the loop is parked in
        // WaitForDurable. Allow scheduler latency, then assert silence.
        await Task.Delay(150);
        Assert.True(barrier.WaitCount >= 1, $"send loop never entered WaitForDurable (WaitCount={barrier.WaitCount})");
        Assert.Equal(7, Volatile.Read(ref barrier.LastAwaitedSeq));
        Assert.Equal(0, ms.Length);

        // Release the barrier and assert the frame lands on the stream.
        barrier.Release();
        Assert.True(await observed.WaitAsync(TimeSpan.FromSeconds(5)),
            "send loop did not write the frame after barrier release");
        Assert.Equal(frame, ms.ToArray());

        cts.Cancel();
    }

    [Fact]
    public async Task SendLoop_DoesNotWait_WhenDurabilityHandleIsNone()
    {
        using var ms = new MemoryStream();
        using var observed = new SemaphoreSlim(0, 1);
        var observingStream = new ObservingMemoryStream(ms, observed);
        await using var transport = new TcpTransport(
            connectionId: 2, stream: observingStream,
            logger: NullLogger<TcpTransport>.Instance,
            sendQueueCapacity: 8);
        using var cts = new CancellationTokenSource();
        transport.StartSendLoop(cts.Token);

        var frame = new byte[] { 9, 9, 9 };
        Assert.True(transport.TryEnqueueFrame(frame));

        Assert.True(await observed.WaitAsync(TimeSpan.FromSeconds(5)),
            "default DurabilityHandle.None path should send immediately");
        Assert.Equal(frame, ms.ToArray());

        cts.Cancel();
    }

    /// <summary>
    /// MemoryStream wrapper that releases <paramref name="signal"/>
    /// the first time the loop writes anything — lets the test wait
    /// deterministically rather than polling.
    /// </summary>
    private sealed class ObservingMemoryStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly SemaphoreSlim _signal;
        private int _signalled;
        public ObservingMemoryStream(MemoryStream inner, SemaphoreSlim signal)
        { _inner = inner; _signal = signal; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            if (Interlocked.Exchange(ref _signalled, 1) == 0) _signal.Release();
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _inner.Write(buffer.Span);
            if (Interlocked.Exchange(ref _signalled, 1) == 0) _signal.Release();
            return ValueTask.CompletedTask;
        }
    }
}
