using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Lifecycle facet of <see cref="ChannelDispatcher"/> (issue #168 split):
/// thread bootstrap (<see cref="Start"/> / <c>RunLoop</c>), heartbeat,
/// single-thread invariant assert, shutdown / disposal, and the
/// <see cref="TestProbe"/> seam.
/// </summary>
public sealed partial class ChannelDispatcher
{
    public void Start()
    {
        _logger.LogInformation("channel {ChannelNumber} dispatcher starting", ChannelNumber);
        _loopTask = Task.Factory.StartNew(() => RunLoop(_cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Issue #138 / PR-5: dispatch loop runs synchronously on the LongRunning
    // thread so the single-writer invariant (engine state, packet buffer,
    // sequence counters, audit writer, dedup index) holds. A previous async
    // implementation used `await reader.WaitToReadAsync(ct).AsTask().WaitAsync(...)`
    // which let the continuation hop to a thread-pool thread after each idle
    // wait — silent in Release (AssertOnLoopThread compiles out) but caught
    // by the Debug assert and observable as 504 timeouts in E2E tests.
    private void RunLoop(CancellationToken ct)
    {
        _loopThread = Thread.CurrentThread;
        _engine.BindToDispatchThread(_loopThread);
        LoadPersistedStateOnLoopThread();

        var reader = _inbound.Reader;
        var heartbeatMs = (int)Math.Max(1, HeartbeatInterval.TotalMilliseconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RecordHeartbeat();
                var waitTask = reader.WaitToReadAsync(ct).AsTask();
                bool signaled;
                try { signaled = waitTask.Wait(heartbeatMs, ct); }
                catch (OperationCanceledException) { return; /* expected: dispatch loop cancelled during shutdown */ }
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { return; /* same: wrapped in Task.Wait */ }
                if (!signaled) continue; // heartbeat timeout — re-record and loop

                bool more;
                try { more = waitTask.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; /* expected: dispatch loop cancelled during shutdown */ }
                if (!more) return; // channel completed

                while (reader.TryRead(out var item))
                {
                    try
                    {
                        ProcessOne(item);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _metrics?.IncDispatcherCrashes();
                        LogDispatcherCrash(ex, ChannelNumber, item.Kind,
                            item.HasSession ? item.Session.ToString() : "(no-session)",
                            item.Firm, item.ClOrdId);
                    }
                    RecordHeartbeat();
                }
            }
        }
        catch (OperationCanceledException) { /* expected: dispatch loop cancelled during shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber} dispatch loop terminated unexpectedly", ChannelNumber);
        }
        finally
        {
            try { FlushPendingSnapshotOnShutdown(); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: final shutdown snapshot flush failed",
                    ChannelNumber);
            }
        }
    }

    private void RecordHeartbeat()
        => _metrics?.RecordTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// Asserts the calling thread is the dispatch-loop thread. Compiled out
    /// in Release builds. Guards every mutation path — engine state, packet
    /// buffer, sequence counters, and the per-session order-id index — to
    /// catch any future producer-thread regression at test time (issue #138).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOnLoopThread()
    {
        var t = _loopThread;
        // _loopThread is null only before Start() (e.g. unit tests calling
        // ProcessOne directly on the test thread). Allow that case.
        System.Diagnostics.Debug.Assert(
            t == null || Thread.CurrentThread == t,
            $"ChannelDispatcher mutation off the dispatch loop thread "
            + $"(channel={ChannelNumber}, expected={t?.ManagedThreadId}, "
            + $"actual={Thread.CurrentThread.ManagedThreadId})");
    }

    /// <summary>
    /// Test hook: simulate a wedged dispatcher by cancelling the loop
    /// without disposing. Subsequent enqueues remain accepted by the
    /// channel but will never be processed, so liveness probes can verify
    /// that <c>/health/live</c> flips to 503 within the configured stale
    /// threshold.
    /// </summary>
    internal void KillForTesting()
    {
        try { _cts.Cancel(); } catch { }
    }

    /// <summary>
    /// Returns a test-only probe that exposes drain/kill helpers without
    /// requiring reflection or InternalsVisibleTo. The probe is the
    /// supported test seam — internal fields and methods are not part of
    /// the supported surface and can change without notice.
    /// </summary>
    public TestProbe CreateTestProbe() => new TestProbe(this);

    /// <summary>
    /// Test-only probe encapsulating drain/kill operations on a
    /// <see cref="ChannelDispatcher"/>. Production code must not call
    /// <see cref="CreateTestProbe"/>; the probe exists solely so tests
    /// can drive the dispatcher synchronously without piercing
    /// encapsulation via reflection.
    /// </summary>
    public sealed class TestProbe
    {
        private readonly ChannelDispatcher _disp;

        internal TestProbe(ChannelDispatcher disp) => _disp = disp;

        /// <summary>
        /// Synchronously processes every pending <c>WorkItem</c> on the
        /// dispatcher's inbound queue using the dispatcher's own
        /// <c>ProcessOne</c> path. The dispatcher loop is bypassed; the
        /// caller's thread does the work. Useful in unit tests that
        /// enqueue commands and need a deterministic point at which the
        /// engine has observed them.
        /// </summary>
        public void DrainInbound()
        {
            var reader = _disp._inbound.Reader;
            while (reader.TryRead(out var item))
            {
                _disp.ProcessOne(item);
            }
        }

        /// <summary>
        /// Cancels the dispatcher loop without disposing. See
        /// <see cref="ChannelDispatcher.KillForTesting"/>.
        /// </summary>
        public void Kill() => _disp.KillForTesting();

        /// <summary>
        /// Test seam for the cooperative-shutdown final snapshot flush
        /// (issue #267). Production code never calls this directly —
        /// it runs from the loop's <c>finally</c> block. Tests that
        /// drive the dispatcher via <see cref="DrainInbound"/> bypass
        /// the loop and so must trigger the shutdown hook explicitly.
        /// </summary>
        public void FlushPendingSnapshotOnShutdown() => _disp.FlushPendingSnapshotOnShutdown();
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("channel {ChannelNumber} dispatcher stopping (sequenceNumber={SequenceNumber})",
            ChannelNumber, SequenceNumber);
        _inbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        if (_loopTask != null) { try { await _loopTask.ConfigureAwait(false); } catch { } }
        // Issue #268: ensure any in-flight async snapshot is durable
        // before tearing down. The writer drains its pending mailbox
        // inside StopAsync.
        if (_asyncSnapshotWriter is { } writer)
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: BackgroundSnapshotWriter shutdown drain failed",
                    ChannelNumber);
            }
        }
        // Issue #269: release the WAL file handle so the underlying
        // file can be deleted by tests / operator tooling. The
        // dispatcher loop has already flushed its final snapshot
        // (which also truncates the WAL) in the loop's finally block,
        // so the on-disk WAL is empty at this point under a clean
        // shutdown.
        if (_wal is IDisposable disposableWal)
        {
            try { disposableWal.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: WAL dispose failed", ChannelNumber);
            }
        }
        _cts.Dispose();
    }
}
