using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// Tracks pending / durable seq watermarks and owns the optional
/// background fsync thread used in
/// <see cref="WalFsyncMode.GroupCommit"/> mode. Extracted from
/// <see cref="FileChannelWriteAheadLog"/>; semantics are
/// unchanged.
/// </summary>
internal sealed class WalDurabilityCoordinator : IDisposable
{
    private readonly bool _groupCommit;
    private readonly TimeSpan _groupCommitInterval;
    private readonly Func<long> _fsyncCallback;
    private readonly object _durabilityLock = new();
    private readonly ManualResetEventSlim _fsyncSignal;
    private readonly CancellationTokenSource? _stopFsyncThread;
    private readonly Thread? _fsyncThread;
    private long _pendingSeq;
    private long _durableSeq;

    public WalDurabilityCoordinator(
        byte channelNumber,
        bool groupCommit,
        TimeSpan groupCommitInterval,
        Func<long> fsyncCallback,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(fsyncCallback);
        ArgumentNullException.ThrowIfNull(logger);
        _groupCommit = groupCommit;
        _groupCommitInterval = groupCommit
            ? (groupCommitInterval > TimeSpan.Zero ? groupCommitInterval : TimeSpan.FromMilliseconds(1))
            : TimeSpan.Zero;
        _fsyncCallback = fsyncCallback;
        _fsyncSignal = new ManualResetEventSlim(initialState: false);
        if (_groupCommit)
        {
            _stopFsyncThread = new CancellationTokenSource();
            _fsyncThread = new Thread(GroupCommitFsyncLoop)
            {
                IsBackground = true,
                Name = $"wal-fsync-ch{channelNumber}",
            };
            _fsyncThread.Start();
        }
    }

    public long PendingSeq => Interlocked.Read(ref _pendingSeq);
    public long DurableSeq => Interlocked.Read(ref _durableSeq);

    public void RecordPending(long seq)
    {
        Interlocked.Exchange(ref _pendingSeq, seq);
        if (_groupCommit)
        {
            _fsyncSignal.Set();
        }
    }

    public void RecordPendingAndDurable(long seq)
    {
        Interlocked.Exchange(ref _pendingSeq, seq);
        Interlocked.Exchange(ref _durableSeq, seq);
    }

    public void WaitForDurable(long seq, CancellationToken cancellationToken = default)
    {
        if (seq <= 0) return;
        if (Interlocked.Read(ref _durableSeq) >= seq) return;
        if (!_groupCommit)
        {
            return;
        }
        _fsyncSignal.Set();
        lock (_durabilityLock)
        {
            while (Interlocked.Read(ref _durableSeq) < seq)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_durabilityLock, _groupCommitInterval);
            }
        }
    }

    public void AdvanceDurableSeqOnTruncate()
    {
        long pending = Interlocked.Read(ref _pendingSeq);
        long durable = Interlocked.Read(ref _durableSeq);
        if (pending > durable)
        {
            Interlocked.Exchange(ref _durableSeq, pending);
            lock (_durabilityLock) Monitor.PulseAll(_durabilityLock);
        }
    }

    public void PulseShutdown()
    {
        lock (_durabilityLock)
        {
            Monitor.PulseAll(_durabilityLock);
        }
    }

    private void GroupCommitFsyncLoop()
    {
        var stopToken = _stopFsyncThread!.Token;
        while (!stopToken.IsCancellationRequested)
        {
            try { _fsyncSignal.Wait(_groupCommitInterval, stopToken); }
            catch (OperationCanceledException) { break; }
            _fsyncSignal.Reset();
            FsyncOnce();
        }
        FsyncOnce();
    }

    private void FsyncOnce()
    {
        long pending = _fsyncCallback();
        if (pending <= 0) return;
        lock (_durabilityLock)
        {
            long current = Interlocked.Read(ref _durableSeq);
            if (pending > current)
            {
                Interlocked.Exchange(ref _durableSeq, pending);
            }
            Monitor.PulseAll(_durabilityLock);
        }
    }

    public void Dispose()
    {
        if (_groupCommit && _stopFsyncThread is not null && _fsyncThread is not null)
        {
            try { _stopFsyncThread.Cancel(); } catch { /* ignore */ }
            _fsyncSignal.Set();
            try { _fsyncThread.Join(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            _stopFsyncThread.Dispose();
        }
        _fsyncSignal.Dispose();
        PulseShutdown();
    }
}
