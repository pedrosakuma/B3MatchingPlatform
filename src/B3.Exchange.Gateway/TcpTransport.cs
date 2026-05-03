using System.Threading.Channels;
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
/// <para>Backpressure: the send queue is bounded with
/// <c>FullMode = DropWrite</c>. On overflow the transport closes the
/// connection rather than ballooning memory under a stuck peer; the
/// session is notified via the <c>onClose</c> delegate.</para>
/// </summary>
public sealed class TcpTransport : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger<TcpTransport> _logger;
    private readonly Channel<byte[]> _sendQueue;
    private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
    private readonly long _connectionId;
    private readonly Action<string>? _onClose;
    private readonly Action? _onSendQueueFull;
    private int _isOpen = 1;
    private long _lastOutboundTickMs;
    private Task? _sendTask;

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
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
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
    /// </summary>
    public bool TryEnqueueFrame(byte[] frame)
    {
        if (!IsOpen) return false;
        if (_sendQueue.Writer.TryWrite(frame)) return true;
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
                try
                {
                    await _streamWriteLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _streamWriteLock.Release();
                    }
                    Volatile.Write(ref _lastOutboundTickMs, Environment.TickCount64);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "tcp transport {ConnectionId} send IO error; closing", _connectionId);
                    Close("send-io-error");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Close("send-loop-exit");
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
        _streamWriteLock.Dispose();
    }
}
