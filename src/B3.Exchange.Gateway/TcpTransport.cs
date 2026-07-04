using System.Threading.Channels;
using B3.Exchange.Contracts;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway;

/// <summary>
/// Transport-only half of a per-connection FIXP session. Owns the
/// underlying <see cref="Stream"/> (typically a <see cref="System.Net.Sockets.NetworkStream"/>),
/// a single send loop draining a bounded outbound queue, and the
/// write-lock that serialises *all* writes to the stream — including
/// emergency Terminate sends that bypass the queue.
///
/// <para>Knows nothing about FIXP framing, SBE templates, sequence
/// numbers, or session identity. It pumps opaque, fully-encoded byte
/// arrays. The owning <see cref="FixpSession"/> drives the receive loop
/// directly off <see cref="Stream"/> so it can cooperate with the
/// liveness watchdog without crossing a callback boundary on every
/// frame.</para>
///
/// <para>Backpressure: the send queue is bounded and uses non-blocking
/// <c>TryWrite</c> calls. On overflow the transport closes the
/// connection rather than ballooning memory under a stuck peer; the
/// session is notified via the <c>onClose</c> delegate.</para>
/// </summary>
public sealed class TcpTransport : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger<TcpTransport> _logger;
    private readonly Channel<OutboundFrame> _sendQueue;
    private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
    private readonly long _connectionId;
    private readonly Action<string>? _onClose;
    private readonly Action? _onSendQueueFull;
    private int _isOpen = 1;
    private long _lastOutboundTickMs;
    private Task? _sendTask;
    /// <summary>
    /// Issue #487: tracks frames that have been accepted into the send
    /// queue but not yet fully written to (or dropped from) the socket.
    /// Incremented by the producer (<see cref="TryEnqueueFrame(byte[], DurabilityHandle)"/>)
    /// the instant a frame is admitted — before it becomes visible in the
    /// bounded channel — and decremented by the single consumer once the
    /// frame has been written, errored, or released. Counting from the
    /// producer side (rather than after the send loop dequeues) closes the
    /// race window between the channel dequeue and the consumer's
    /// bookkeeping: without it a frame could be invisible to
    /// <see cref="WaitForSendQueueDrainAsync"/> (out of the queue but not
    /// yet counted in-flight) and be dropped by a concurrent
    /// <see cref="Close"/> before reaching the wire.
    /// </summary>
    private int _inFlightFrames;

    /// <summary>
    /// Issue #312: per-frame envelope tying the encoded bytes to the
    /// optional durability handle the send loop must satisfy before
    /// writing them to the socket. <see cref="DurabilityHandle.None"/>
    /// (the default for non-ER frames such as keep-alive,
    /// SessionReject, BusinessMessageReject) skips the wait.
    /// </summary>
    private readonly record struct OutboundFrame(byte[]? Bytes, PooledOutboundFrame? PooledFrame, DurabilityHandle Durability)
    {
        public ReadOnlyMemory<byte> Memory => PooledFrame is { } pooled ? pooled.Memory : Bytes!;

        public void Release()
        {
            PooledFrame?.Release();
        }
    }

    /// <summary>
    /// Underlying stream. Exposed for the session-level receive loop —
    /// the session reads framed bytes directly so it can update liveness
    /// counters frame-by-frame without an indirection.
    /// </summary>
    public Stream Stream => _stream;

    public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

    /// <summary>
    /// Approximate number of pre-encoded frames sitting in the outbound
    /// queue. <see cref="ChannelReader{T}.Count"/> is O(1) on a bounded
    /// channel; safe to scrape from /metrics.
    /// </summary>
    public int SendQueueDepth => _sendQueue.Reader.Count;

    /// <summary>
    /// <see cref="Environment.TickCount64"/> reading captured at the end
    /// of the last successful stream write (queued send or direct send).
    /// The <see cref="FixpSession"/> reads this for its idle-timeout
    /// watchdog.
    /// </summary>
    public long LastOutboundTickMs => Volatile.Read(ref _lastOutboundTickMs);

    /// <summary>
    /// Issue #487: waits until the send queue drains to empty AND all
    /// in-flight frames have been written to the socket, or the timeout
    /// expires. Returns true if drained within the timeout, false if
    /// timed out or transport closed. Call before <see cref="Close"/> to
    /// ensure pending frames reach the wire.
    /// </summary>
    public async Task<bool> WaitForSendQueueDrainAsync(TimeSpan timeout)
    {
        if (!IsOpen) return false;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        // Must wait for both: queued frames AND in-flight frames (admitted to
        // the queue but not yet written). _inFlightFrames is incremented by the
        // producer before the frame becomes visible in the channel and
        // decremented by the consumer after WriteAsync completes / the frame is
        // released, so there is no window where an outstanding frame is
        // invisible to this check.
        while (SendQueueDepth > 0 || Volatile.Read(ref _inFlightFrames) > 0)
        {
            if (!IsOpen) return false;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(5).ConfigureAwait(false);
        }
        return true;
    }

    public TcpTransport(long connectionId, Stream stream, ILogger<TcpTransport> logger,
        int sendQueueCapacity, Action<string>? onClose = null,
        Action? onSendQueueFull = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionId = connectionId;
        _stream = stream;
        _logger = logger;
        _onClose = onClose;
        _onSendQueueFull = onSendQueueFull;
        _sendQueue = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Volatile.Write(ref _lastOutboundTickMs, Environment.TickCount64);
    }

    /// <summary>
    /// Starts the send loop. Must be called once. The receive loop is
    /// the session's responsibility (it reads <see cref="Stream"/>
    /// directly so it can refresh liveness counters frame-by-frame).
    /// </summary>
    public void StartSendLoop(CancellationToken ct)
    {
        _sendTask = Task.Run(() => RunSendLoopAsync(ct), ct);
    }

    /// <summary>
    /// Hands a fully-encoded outbound frame to the send queue. Returns
    /// <c>false</c> if the transport is closed or the queue overflowed
    /// (in which case the transport is closed as a side-effect to avoid
    /// unbounded memory growth under a stuck peer).
    ///
    /// <para>Issue #312: the optional <paramref name="durability"/>
    /// pair tells the send loop which WAL seq must be fsynced before
    /// the bytes leave the host. <see cref="DurabilityHandle.None"/>
    /// (the default) means "send immediately" — the pre-#312
    /// behaviour preserved by every keep-alive / SessionReject /
    /// BusinessMessageReject call site.</para>
    /// </summary>
    public bool TryEnqueueFrame(byte[] frame, DurabilityHandle durability = default)
    {
        if (!IsOpen) return false;
        // Count the frame as in-flight BEFORE it becomes visible in the queue
        // so a concurrent drain never observes it as fully drained while it is
        // still pending (see _inFlightFrames docs / issue #487).
        Interlocked.Increment(ref _inFlightFrames);
        if (_sendQueue.Writer.TryWrite(new OutboundFrame(frame, null, durability))) return true;
        Interlocked.Decrement(ref _inFlightFrames);
        try { _onSendQueueFull?.Invoke(); } catch { }
        Close("send-queue-full");
        return false;
    }

    internal bool TryEnqueueFrame(PooledOutboundFrame frame, DurabilityHandle durability = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!IsOpen) return false;
        frame.AddRef();
        Interlocked.Increment(ref _inFlightFrames);
        if (_sendQueue.Writer.TryWrite(new OutboundFrame(null, frame, durability))) return true;
        Interlocked.Decrement(ref _inFlightFrames);
        frame.Release();
        try { _onSendQueueFull?.Invoke(); } catch { }
        Close("send-queue-full");
        return false;
    }

    /// <summary>
    /// Writes <paramref name="frame"/> directly to the stream, bypassing
    /// the send queue. Used by the session to flush a Terminate before
    /// closing — the queue might be racing with an in-flight write or
    /// about to be cancelled, so we acquire the write lock and write
    /// inline. Always single-shot; never retried.
    /// </summary>
    public async Task SendDirectAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (!IsOpen) return;
        await _streamWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            Volatile.Write(ref _lastOutboundTickMs, Environment.TickCount64);
        }
        finally
        {
            _streamWriteLock.Release();
        }
    }

    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Issue #487: _inFlightFrames was incremented by the producer
                // when this frame was admitted to the queue; decrement it once
                // the frame has been written or released (finally below).
                try
                {
                    try
                    {
                        // Issue #312: never let an ER (or any frame tagged
                        // with a durability handle) hit the wire before its
                        // covering WAL record is fsynced. Synchronous wait
                        // is fine here — the send loop is the only consumer
                        // of the queue and we WANT it to back-pressure on
                        // un-durable frames; that's the whole point of the
                        // gating contract.
                        if (frame.Durability.IsActive)
                        {
                            frame.Durability.Barrier!.WaitForDurable(frame.Durability.Seq, ct);
                        }
                        await _streamWriteLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await _stream.WriteAsync(frame.Memory, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _streamWriteLock.Release();
                        }
                    }
                    finally
                    {
                        frame.Release();
                    }
                    Volatile.Write(ref _lastOutboundTickMs, Environment.TickCount64);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "tcp transport {ConnectionId} send IO error; closing", _connectionId);
                    Close("send-io-error");
                    return;
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlightFrames);
                }
            }
        }
        catch (OperationCanceledException) { /* expected: send loop cancelled during transport close */ }
        finally
        {
            Close("send-loop-exit");
            ReleaseQueuedFrames();
        }
    }

    private void ReleaseQueuedFrames()
    {
        while (_sendQueue.Reader.TryRead(out var frame))
        {
            // Frames still queued at close were counted in-flight by the
            // producer but never written; balance the counter as we drop them.
            Interlocked.Decrement(ref _inFlightFrames);
            frame.Release();
        }
    }

    /// <summary>
    /// Marks the transport closed and notifies the owning session via
    /// the <c>onClose</c> callback. Idempotent. Subsequent
    /// <see cref="TryEnqueueFrame"/> calls return false; the send loop
    /// exits when the writer completes.
    /// </summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return;
        _logger.LogInformation("tcp transport {ConnectionId} closing: {Reason}", _connectionId, reason);
        _sendQueue.Writer.TryComplete();
        try { _onClose?.Invoke(reason); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Close("dispose");
        try { if (_sendTask != null) await _sendTask.ConfigureAwait(false); } catch { }
        ReleaseQueuedFrames();
        _streamWriteLock.Dispose();
    }
}
