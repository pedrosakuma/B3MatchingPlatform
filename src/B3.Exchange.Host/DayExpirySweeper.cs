using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// Issue #506 — end-of-trading-day Day-order expiry sweep.
///
/// <para>At the end of each trading day the host enqueues one
/// <c>EnqueueOperatorExpireDay</c> command per channel dispatcher. On the
/// dispatch thread the engine cancels every resting <c>TimeInForce.Day</c>
/// order — and every parked Day stop — across the channel's books, driving a
/// terminal <c>ExecutionReport</c> (<c>OrdStatus=EXPIRED 'C'</c>) back to the
/// originating session and a UMDF <c>OrderDelete</c> frame under the channel's
/// single-writer invariant (ADR 0009).</para>
///
/// <para>Like <see cref="GtdExpirySweeper"/> the sweeper owns no timer — it is
/// invoked from <c>ExchangeHost.TriggerDailyReset</c> before the listener is
/// torn down, so the per-order ER routes to its originating session. Unlike the
/// GTD sweep, Day expiry is <em>unconditional</em> (every resting Day order
/// expires at the boundary regardless of date), so the sweeper carries no
/// local-market-date and the engine stays clockless (ADR 0009).</para>
/// </summary>
public sealed class DayExpirySweeper
{
    private readonly IReadOnlyList<ChannelDispatcher> _dispatchers;
    private readonly ILogger<DayExpirySweeper> _logger;

    public DayExpirySweeper(IReadOnlyList<ChannelDispatcher> dispatchers,
        ILogger<DayExpirySweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatchers);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatchers = dispatchers;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a Day-order expiry sweep on every channel.
    ///
    /// <para>When <paramref name="waitTimeout"/> is provided (default 5s) the
    /// call blocks until every enqueued sweep has finished on its dispatch
    /// thread — required from <c>TriggerDailyReset</c> so the gateway can flush
    /// the terminal ERs before <c>TerminateAllSessions</c> tears down the
    /// response channels. Pass <see cref="TimeSpan.Zero"/> to fire and
    /// forget.</para>
    ///
    /// <para>Returns the total number of Day orders / parked Day stops
    /// cancelled across all channels. Per-channel enqueue failures are logged
    /// and do not abort the sweep.</para>
    /// </summary>
    public int Sweep(string trigger, TimeSpan? waitTimeout = null)
    {
        if (_dispatchers.Count == 0) return 0;

        var pending = waitTimeout is { } t && t > TimeSpan.Zero
            ? new List<Task<ChannelDispatcher.ExpireDayOutcome>>()
            : null;
        int enqueued = 0;
        int rejected = 0;
        foreach (var dispatcher in _dispatchers)
        {
            var completion = pending is null
                ? null
                : new TaskCompletionSource<ChannelDispatcher.ExpireDayOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            bool ok;
            try
            {
                ok = dispatcher.EnqueueOperatorExpireDay(completion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Day expiry sweep ({Trigger}): enqueue threw channel={Channel}",
                    trigger, dispatcher.ChannelNumber);
                rejected++;
                continue;
            }
            if (!ok)
            {
                _logger.LogWarning(
                    "Day expiry sweep ({Trigger}): enqueue rejected channel={Channel}",
                    trigger, dispatcher.ChannelNumber);
                rejected++;
                continue;
            }
            enqueued++;
            if (completion != null) pending!.Add(completion.Task);
        }

        int cancelled = 0;
        if (pending is { Count: > 0 })
        {
            bool completedInTime;
            try
            {
                completedInTime = Task.WaitAll(pending.ToArray(), waitTimeout!.Value);
            }
            catch (AggregateException ex)
            {
                completedInTime = pending.TrueForAll(t => t.IsCompleted);
                _logger.LogWarning(ex,
                    "Day expiry sweep ({Trigger}): one or more ExpireDay completions faulted",
                    trigger);
            }
            foreach (var task in pending)
            {
                if (task.IsCompletedSuccessfully) cancelled += task.Result.CancelledOrderCount;
            }
            if (!completedInTime)
            {
                int incomplete = pending.Count(t => !t.IsCompleted);
                _logger.LogError(
                    "Day expiry sweep ({Trigger}): timed out after {TimeoutMs}ms with {Incomplete} of {Enqueued} ExpireDay completions still pending — TerminateAllSessions may close sessions before the terminal ER reaches the wire",
                    trigger, (int)waitTimeout!.Value.TotalMilliseconds, incomplete, enqueued);
            }
        }

        _logger.LogInformation(
            "Day expiry sweep ({Trigger}): channels={ChannelCount} enqueued={Enqueued} rejected={Rejected} cancelled={Cancelled}",
            trigger, _dispatchers.Count, enqueued, rejected, cancelled);
        return cancelled;
    }
}
