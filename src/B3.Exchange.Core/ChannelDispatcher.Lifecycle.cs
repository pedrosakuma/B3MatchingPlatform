using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Lifecycle facet of <see cref="ChannelDispatcher"/> (issue #168 split):
/// thread bootstrap (<see cref="Start"/> / <see cref="RunLoopAsync"/>),
/// heartbeat, single-thread invariant assert, shutdown / disposal,
/// and the <see cref="TestProbe"/> seam.
/// </summary>
public sealed partial class ChannelDispatcher
{
    public void Start()
    {
        _logger.LogInformation("channel {ChannelNumber} dispatcher starting", ChannelNumber);
        _loopTask = Task.Factory.StartNew(() => RunLoopAsync(_cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Capture the dispatch-loop thread identity on entry so
        // AssertOnLoopThread() in mutation paths can enforce the
        // single-writer invariant in DEBUG builds (issue #138).
        _loopThread = Thread.CurrentThread;
        // Issue #169: bind the matching engine eagerly so any off-thread
        // engine call is caught on the very first invocation, not just
        // after the first in-thread call has latched the owner.
        _engine.BindToDispatchThread(_loopThread);

        // Heartbeat is recorded on every loop wakeup (whether triggered by
        // new work or by the periodic timeout) so a stuck/dead dispatch
        // thread is detected by /health/live within HeartbeatInterval +
        // probe threshold.
        try
        {
            var reader = _inbound.Reader;
            while (!ct.IsCancellationRequested)
            {
                RecordHeartbeat();
                Task<bool> waitTask = reader.WaitToReadAsync(ct).AsTask();
                bool more;
                try
                {
                    more = await waitTask.WaitAsync(HeartbeatInterval, ct)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Timeout: loop and re-record the heartbeat on next iteration.
                    continue;
                }
                catch (OperationCanceledException) { return; }

                if (!more) return; // channel completed
                while (reader.TryRead(out var item))
                {
                    // Issue #170: contain unhandled exceptions per work-item
                    // so a single bad command (engine bug, sink throw, etc.)
                    // cannot kill the dispatch loop and silence the entire
                    // channel. We log with full context and bump
                    // exch_dispatcher_crash_total; the loop continues so
                    // healthy commands still get serviced. Cancellation
                    // bypasses containment because it is the cooperative
                    // shutdown path — let it bubble to the outer handler.
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber} dispatch loop terminated unexpectedly", ChannelNumber);
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
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("channel {ChannelNumber} dispatcher stopping (sequenceNumber={SequenceNumber})",
            ChannelNumber, SequenceNumber);
        _inbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        if (_loopTask != null) { try { await _loopTask.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
