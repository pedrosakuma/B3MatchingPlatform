using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// GAP-23 / issue #499 — end-of-trading-day Good-Till-Date (GTD) expiry
/// sweep.
///
/// <para>At the end of each trading day the host enqueues one
/// <c>EnqueueOperatorExpireGtd</c> command per channel dispatcher carrying
/// the trading day being closed as a B3 <c>LocalMktDate</c> (days since the
/// Unix epoch). On the dispatch thread the engine cancels every resting GTD
/// order whose <c>ExpireDate</c> is on or before that date, driving a
/// per-order <c>ER_Cancel</c> back to the originating session and a UMDF
/// <c>OrderDelete</c> frame under the channel's single-writer invariant
/// (ADR 0009).</para>
///
/// <para>Like <see cref="OptionExpirySweeper"/> the sweeper does not own a
/// timer — it is invoked from <c>ExchangeHost.TriggerDailyReset</c> before
/// the listener is torn down, so the per-order <c>ER_Cancel</c> still routes
/// to its originating session. The boundary "today" is computed in the
/// configured daily-reset timezone (B3 local market date defaults to
/// <c>America/Sao_Paulo</c>); the engine itself stays clockless (ADR 0009),
/// receiving the date as an explicit command argument.</para>
/// </summary>
public sealed class GtdExpirySweeper
{
    /// <summary>1970-01-01 expressed as a <see cref="DateOnly.DayNumber"/>,
    /// used to convert a calendar date to the wire's days-since-epoch
    /// <c>LocalMktDate</c>.</summary>
    private static readonly int UnixEpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    private readonly IReadOnlyList<ChannelDispatcher> _dispatchers;
    private readonly ILogger<GtdExpirySweeper> _logger;
    private readonly Func<DateOnly> _todayLocal;

    public GtdExpirySweeper(IReadOnlyList<ChannelDispatcher> dispatchers,
        ILogger<GtdExpirySweeper> logger,
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
    /// Builds the timezone-aware "today" provider for the sweeper from the
    /// configured daily-reset timezone. Falls back to UTC when the timezone
    /// id is unknown so the host never fails to start over a bad config.
    /// </summary>
    public static Func<DateOnly> BuildTodayProvider(string? timezoneId, ILogger logger)
    {
        TimeZoneInfo tz;
        try
        {
            tz = string.IsNullOrWhiteSpace(timezoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning(ex,
                "GTD expiry: unknown timezone '{Timezone}', falling back to UTC for the local market date",
                timezoneId);
            tz = TimeZoneInfo.Utc;
        }
        return () => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
    }

    /// <summary>
    /// Enqueues a GTD expiry sweep on every channel for the trading day
    /// being closed.
    ///
    /// <para>When <paramref name="waitTimeout"/> is provided (default 5s)
    /// the call blocks until every enqueued sweep has finished on its
    /// dispatch thread — required from <c>TriggerDailyReset</c> so the
    /// gateway can flush ER_Cancel frames before <c>TerminateAllSessions</c>
    /// tears down the response channels. Pass <see cref="TimeSpan.Zero"/> to
    /// fire and forget.</para>
    ///
    /// <para>Returns the total number of GTD orders cancelled across all
    /// channels (0 when nothing had reached its ExpireDate). Per-channel
    /// enqueue failures are logged at warn level and do not abort the
    /// sweep.</para>
    /// </summary>
    public int Sweep(string trigger, TimeSpan? waitTimeout = null)
    {
        if (_dispatchers.Count == 0) return 0;
        var today = _todayLocal();
        int dayNumber = today.DayNumber - UnixEpochDayNumber;
        if (dayNumber < 0 || dayNumber > ushort.MaxValue)
        {
            _logger.LogError(
                "GTD expiry sweep ({Trigger}): local market date {Today} is outside the LocalMktDate range; skipping",
                trigger, today);
            return 0;
        }
        ushort currentDate = (ushort)dayNumber;

        var pending = waitTimeout is { } t && t > TimeSpan.Zero
            ? new List<Task<ChannelDispatcher.ExpireGtdOutcome>>()
            : null;
        int enqueued = 0;
        int rejected = 0;
        foreach (var dispatcher in _dispatchers)
        {
            var completion = pending is null
                ? null
                : new TaskCompletionSource<ChannelDispatcher.ExpireGtdOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            bool ok;
            try
            {
                ok = dispatcher.EnqueueOperatorExpireGtd(currentDate, completion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GTD expiry sweep ({Trigger}): enqueue threw channel={Channel}",
                    trigger, dispatcher.ChannelNumber);
                rejected++;
                continue;
            }
            if (!ok)
            {
                _logger.LogWarning(
                    "GTD expiry sweep ({Trigger}): enqueue rejected channel={Channel}",
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
                    "GTD expiry sweep ({Trigger}): one or more ExpireGtd completions faulted",
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
                    "GTD expiry sweep ({Trigger}): timed out after {TimeoutMs}ms with {Incomplete} of {Enqueued} ExpireGtd completions still pending — TerminateAllSessions may close sessions before ER_Cancel reaches the wire",
                    trigger, (int)waitTimeout!.Value.TotalMilliseconds, incomplete, enqueued);
            }
        }

        _logger.LogInformation(
            "GTD expiry sweep ({Trigger}): channels={ChannelCount} enqueued={Enqueued} rejected={Rejected} cancelled={Cancelled} currentDate={CurrentDate} today={Today}",
            trigger, _dispatchers.Count, enqueued, rejected, cancelled, currentDate, today);
        return cancelled;
    }
}
