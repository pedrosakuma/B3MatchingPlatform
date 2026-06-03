using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// GAP-26 / issue #498 — daily Good-Till (GTC / unexpired-GTD) restatement
/// sweep at the trading-day boundary.
///
/// <para>At each daily reset the host enqueues one
/// <c>EnqueueOperatorRestateGt</c> command per channel dispatcher carrying
/// the trading day being closed as a B3 <c>LocalMktDate</c> (days since the
/// Unix epoch). On the dispatch thread the engine emits a private
/// <c>ExecutionReport_Modify</c> (<c>OrdStatus=RESTATED</c>,
/// <c>ExecRestatementReason=GT_RESTATEMENT</c>) for every surviving GTC order
/// and every GTD order whose <c>ExpireDate</c> is strictly after that date,
/// routed back to the originating session. Unlike the GTD-expiry sweep the
/// book is unchanged — there is no UMDF frame and no <c>RptSeq</c> advance
/// (ADR 0009).</para>
///
/// <para>Like <see cref="GtdExpirySweeper"/> the sweeper owns no timer: it is
/// invoked from <c>ExchangeHost.TriggerDailyReset</c> after the GTD-expiry
/// sweep but before the listener is torn down, so each restatement ER still
/// routes to its originating session. The boundary "today" is computed in the
/// configured daily-reset timezone (defaults to <c>America/Sao_Paulo</c>); the
/// engine itself stays clockless, receiving the date as a command argument.</para>
/// </summary>
public sealed class GtRestatementSweeper
{
    /// <summary>1970-01-01 expressed as a <see cref="DateOnly.DayNumber"/>,
    /// used to convert a calendar date to the wire's days-since-epoch
    /// <c>LocalMktDate</c>.</summary>
    private static readonly int UnixEpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    private readonly IReadOnlyList<ChannelDispatcher> _dispatchers;
    private readonly ILogger<GtRestatementSweeper> _logger;
    private readonly Func<DateOnly> _todayLocal;

    public GtRestatementSweeper(IReadOnlyList<ChannelDispatcher> dispatchers,
        ILogger<GtRestatementSweeper> logger,
        Func<DateOnly> todayLocal)
    {
        ArgumentNullException.ThrowIfNull(dispatchers);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(todayLocal);
        _dispatchers = dispatchers;
        _logger = logger;
        _todayLocal = todayLocal;
    }

    /// <summary>
    /// Enqueues a Good-Till restatement sweep on every channel for the trading
    /// day being closed.
    ///
    /// <para>When <paramref name="waitTimeout"/> is provided (default 5s) the
    /// call blocks until every enqueued sweep has finished on its dispatch
    /// thread — required from <c>TriggerDailyReset</c> so the gateway can
    /// flush the restatement ERs before <c>TerminateAllSessions</c> tears down
    /// the response channels. Pass <see cref="TimeSpan.Zero"/> to fire and
    /// forget.</para>
    ///
    /// <para>Returns the total number of orders restated across all channels.
    /// Per-channel enqueue failures are logged at warn level and do not abort
    /// the sweep.</para>
    /// </summary>
    public int Sweep(string trigger, TimeSpan? waitTimeout = null)
    {
        if (_dispatchers.Count == 0) return 0;
        var today = _todayLocal();
        int dayNumber = today.DayNumber - UnixEpochDayNumber;
        if (dayNumber < 0 || dayNumber > ushort.MaxValue)
        {
            _logger.LogError(
                "GT restatement sweep ({Trigger}): local market date {Today} is outside the LocalMktDate range; skipping",
                trigger, today);
            return 0;
        }
        ushort currentDate = (ushort)dayNumber;

        var pending = waitTimeout is { } t && t > TimeSpan.Zero
            ? new List<Task<ChannelDispatcher.RestateGtOutcome>>()
            : null;
        int enqueued = 0;
        int rejected = 0;
        foreach (var dispatcher in _dispatchers)
        {
            var completion = pending is null
                ? null
                : new TaskCompletionSource<ChannelDispatcher.RestateGtOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            bool ok;
            try
            {
                ok = dispatcher.EnqueueOperatorRestateGt(currentDate, completion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GT restatement sweep ({Trigger}): enqueue threw channel={Channel}",
                    trigger, dispatcher.ChannelNumber);
                rejected++;
                continue;
            }
            if (!ok)
            {
                _logger.LogWarning(
                    "GT restatement sweep ({Trigger}): enqueue rejected channel={Channel}",
                    trigger, dispatcher.ChannelNumber);
                rejected++;
                continue;
            }
            enqueued++;
            if (completion != null) pending!.Add(completion.Task);
        }

        int restated = 0;
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
                    "GT restatement sweep ({Trigger}): one or more RestateGt completions faulted",
                    trigger);
            }
            foreach (var task in pending)
            {
                if (task.IsCompletedSuccessfully) restated += task.Result.RestatedOrderCount;
            }
            if (!completedInTime)
            {
                int incomplete = pending.Count(t => !t.IsCompleted);
                _logger.LogError(
                    "GT restatement sweep ({Trigger}): timed out after {TimeoutMs}ms with {Incomplete} of {Enqueued} RestateGt completions still pending — TerminateAllSessions may close sessions before the restatement ER reaches the wire",
                    trigger, (int)waitTimeout!.Value.TotalMilliseconds, incomplete, enqueued);
            }
        }

        _logger.LogInformation(
            "GT restatement sweep ({Trigger}): channels={ChannelCount} enqueued={Enqueued} rejected={Rejected} restated={Restated} currentDate={CurrentDate} today={Today}",
            trigger, _dispatchers.Count, enqueued, rejected, restated, currentDate, today);
        return restated;
    }
}
