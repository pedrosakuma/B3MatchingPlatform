using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Single-slot mailbox that hands off <see cref="ChannelStateSnapshot"/>
/// captures from the dispatch loop to a dedicated writer thread (issue
/// #268). The loop thread invokes <see cref="Submit"/> with the freshly
/// captured POCO, which is published via an interlocked swap and the
/// writer is signalled — the loop never blocks on serialization or
/// fsync.
///
/// <para>Backpressure semantics are <em>last-write-wins</em>: if
/// <see cref="Submit"/> is called while a previous snapshot is still
/// pending, the older one is discarded (and counted via
/// <see cref="ChannelMetrics.IncSnapshotDroppedByBackpressure"/>) and
/// replaced by the newer one. This is safe because each snapshot
/// represents the complete channel state at a point in time — there is
/// no incremental information lost. The trade-off is RPO: a crash
/// while a Submit-but-not-yet-written snapshot is in flight loses the
/// most recent commands. Operators that need zero-RPO must keep the
/// synchronous (non-async) writer.</para>
///
/// <para>On <see cref="StopAsync"/> the writer drains the final
/// pending snapshot before returning, so cooperative shutdown still
/// produces a durable on-disk image of the most recent submitted
/// state.</para>
/// </summary>
public sealed class BackgroundSnapshotWriter : IAsyncDisposable
{
    private readonly IChannelStatePersister _persister;
    private readonly ILogger _logger;
    private readonly ChannelMetrics? _metrics;
    private readonly byte _channelNumber;
    private readonly Action<ChannelStateSnapshot>? _onSaved;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    private sealed record PendingSnapshot(
        ChannelStateSnapshot Snapshot,
        long SaveGeneration);

    // Single-slot mailbox. Mutated only via Interlocked.Exchange so the
    // writer thread can read-and-clear without a lock and Submit can
    // overwrite without contention.
    private PendingSnapshot? _pending;
    private int _stopped;

    public BackgroundSnapshotWriter(
        byte channelNumber,
        IChannelStatePersister persister,
        ILogger logger,
        ChannelMetrics? metrics = null,
        Action<ChannelStateSnapshot>? onSaved = null)
    {
        _channelNumber = channelNumber;
        _persister = persister;
        _logger = logger;
        _metrics = metrics;
        _onSaved = onSaved;
        // The writer is a long-lived background task; LongRunning hints
        // the scheduler to use a dedicated thread instead of a
        // ThreadPool slot, since this task spends most of its life
        // blocked on the semaphore or in fsync.
        _writerTask = Task.Factory.StartNew(
            WriterLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Hands a freshly captured snapshot to the writer thread. Returns
    /// immediately. If a previous snapshot was still pending, it is
    /// discarded (last-write-wins) and the backpressure counter is
    /// incremented.
    /// </summary>
    public void Submit(ChannelStateSnapshot snapshot)
    {
        long saveGeneration = _persister.CaptureSaveGeneration(
            snapshot.ChannelNumber);
        var prior = Interlocked.Exchange(
            ref _pending,
            new PendingSnapshot(snapshot, saveGeneration));
        if (prior is not null)
        {
            _metrics?.IncSnapshotDroppedByBackpressure();
        }
        // Release the semaphore if not already signalled. A
        // SemaphoreSlim with capacity 1 will throw if released twice
        // without a wait between; suppress that to coalesce signals.
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task WriterLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            DrainOnce();
        }
        // Drain any final pending snapshot deposited between the last
        // signal and the cancellation request so cooperative shutdown
        // does not lose work.
        DrainOnce();
    }

    private void DrainOnce()
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is null) return;
        var snap = pending.Snapshot;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_persister.TrySave(
                    snap,
                    pending.SaveGeneration,
                    out var bytes))
            {
                _logger.LogInformation(
                    "channel {ChannelNumber}: discarded background snapshot from reset generation {SaveGeneration}",
                    _channelNumber, pending.SaveGeneration);
                return;
            }
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (_metrics is { } m)
            {
                m.SnapshotWrite.ObserveTicks(elapsed);
                m.IncSnapshotSaveOk();
                if (bytes > 0) m.SetSnapshotLastSizeBytes(bytes);
                m.SetSnapshotLastSuccessUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            // Issue #269: invoke optional post-save callback so the
            // dispatcher can truncate the WAL on the writer thread,
            // guaranteeing the WAL is only ever truncated after a
            // durable snapshot exists on disk.
            if (_onSaved is { } cb)
            {
                try { cb(snap); }
                catch (Exception cbEx)
                {
                    _logger.LogError(cbEx,
                        "channel {ChannelNumber}: background snapshot writer onSaved callback threw",
                        _channelNumber);
                }
            }
        }
        catch (Exception ex)
        {
            _metrics?.IncSnapshotSaveFailure();
            _logger.LogError(ex,
                "channel {ChannelNumber}: background snapshot writer Save failed",
                _channelNumber);
        }
    }

    /// <summary>
    /// Signals the writer to stop, drains any remaining pending
    /// snapshot, and awaits the writer task. Safe to call multiple
    /// times — only the first call drains.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        try { await _writerTask.ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _signal.Dispose();
        _cts.Dispose();
    }
}
