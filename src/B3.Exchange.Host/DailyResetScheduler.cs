using System.Globalization;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// Once-per-day scheduler for the trading-day rollover (#GAP-09 / issue
/// #47, spec §4.5.1). On <see cref="Start"/> it computes the next firing
/// instant in UTC from a local "HH:mm" + IANA-timezone pair, arms a
/// <see cref="Timer"/>, and re-arms after each fire. The action runs on a
/// pool thread (Timer callback) so the implementation must be quick and
/// non-blocking — typically a single call to
/// <c>EntryPointListener.TerminateAllSessions</c>.
///
/// <para>Idempotent <see cref="Trigger"/> is exposed for the operator
/// HTTP endpoint and for tests; it bypasses the schedule entirely.</para>
/// </summary>
public sealed class DailyResetScheduler : IAsyncDisposable
{
    private readonly TimeSpan _localTime;
    private readonly TimeZoneInfo _timezone;
    private readonly Action _action;
    private readonly ILogger<DailyResetScheduler> _logger;
    private readonly Func<DateTimeOffset>? _utcNowOverride;
    private Timer? _timer;
    private int _disposed;

    public DailyResetScheduler(string schedule, string timezone, Action action,
        ILogger<DailyResetScheduler> logger,
        Func<DateTimeOffset>? utcNowOverride = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(logger);
        _localTime = ParseHHmm(schedule);
        _timezone = ResolveTimezone(timezone);
        _action = action;
        _logger = logger;
        _utcNowOverride = utcNowOverride;
    }

    /// <summary>Local time-of-day at which the scheduler fires.</summary>
    public TimeSpan LocalTime => _localTime;
    public TimeZoneInfo Timezone => _timezone;

    public void Start()
    {
        if (_timer is not null) throw new InvalidOperationException("scheduler already started");
        var now = UtcNow();
        var next = ComputeNextFiringUtc(now, _localTime, _timezone);
        var delay = next - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _logger.LogInformation(
            "daily-reset scheduler armed: schedule={Schedule:hh\\:mm} tz={Tz} next-firing-utc={Next:o} (in {DelayMin:n1}min)",
            _localTime, _timezone.Id, next, delay.TotalMinutes);
        _timer = new Timer(OnTimer, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Run the rollover action immediately, regardless of
    /// schedule. Used by the operator HTTP endpoint and by tests. Does
    /// NOT shift the next scheduled firing.</summary>
    public void Trigger()
    {
        try { _action(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "daily-reset action threw on manual trigger");
            throw;
        }
    }

    private void OnTimer(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _logger.LogInformation("daily-reset firing (scheduled)");
            _action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "daily-reset action threw");
        }
        finally
        {
            // Re-arm for the next 24h boundary even if the action threw,
            // so a transient failure doesn't permanently disable the
            // rollover.
            if (Volatile.Read(ref _disposed) == 0)
            {
                var now = UtcNow();
                var next = ComputeNextFiringUtc(now, _localTime, _timezone);
                var delay = next - now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                try { _timer?.Change(delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                _logger.LogInformation(
                    "daily-reset re-armed: next-firing-utc={Next:o} (in {DelayMin:n1}min)",
                    next, delay.TotalMinutes);
            }
        }
    }

    private DateTimeOffset UtcNow() => _utcNowOverride?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Compute the next UTC instant at which the local wall-clock time
    /// in <paramref name="tz"/> equals <paramref name="localTime"/> and
    /// is strictly after <paramref name="nowUtc"/>. Handles DST gaps
    /// (the local time may be invalid on the first candidate day, in
    /// which case the next valid day is returned) and DST folds (the
    /// earlier of the two valid instants is returned).
    /// </summary>
    public static DateTimeOffset ComputeNextFiringUtc(
        DateTimeOffset nowUtc, TimeSpan localTime, TimeZoneInfo tz)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        // Candidate: today at HH:mm in the local zone.
        for (int dayOffset = 0; dayOffset < 14; dayOffset++)
        {
            var candidateLocal = localNow.Date.AddDays(dayOffset).Add(localTime);
            // Skip candidates not strictly after now.
            // (DateTime arithmetic on .Date drops the offset, which is what
            // we want; we re-anchor below.)
            if (tz.IsInvalidTime(candidateLocal))
                continue; // local time falls in a DST spring-forward gap
            DateTimeOffset candidateUtc;
            if (tz.IsAmbiguousTime(candidateLocal))
            {
                // DST fall-back fold: the local wall-clock time maps to two
                // distinct UTC instants. We want the earlier UTC instant
                // (so the rollover doesn't fire a second time during the
                // repeated hour). offsets.Min() would pick the most
                // negative offset, which actually corresponds to the
                // LATER UTC instant — convert each candidate explicitly
                // and pick by absolute UTC time.
                var offsets = tz.GetAmbiguousTimeOffsets(candidateLocal);
                DateTimeOffset earliest = default;
                bool seeded = false;
                foreach (var off in offsets)
                {
                    var u = new DateTimeOffset(candidateLocal, off).ToUniversalTime();
                    if (!seeded || u < earliest) { earliest = u; seeded = true; }
                }
                candidateUtc = earliest;
            }
            else
            {
                var offset = tz.GetUtcOffset(candidateLocal);
                candidateUtc = new DateTimeOffset(candidateLocal, offset).ToUniversalTime();
            }
            if (candidateUtc > nowUtc) return candidateUtc;
        }
        // Fallback: should never reach here unless `tz` is completely
        // pathological (every candidate invalid for two weeks). Anchor
        // 24h out so the host doesn't busy-spin re-arming the timer.
        return nowUtc.AddHours(24);
    }

    private static TimeSpan ParseHHmm(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("dailyReset.schedule must be a non-empty 'HH:mm' string", nameof(s));
        if (!TimeSpan.TryParseExact(s, @"h\:mm", CultureInfo.InvariantCulture, out var ts) &&
            !TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out ts))
        {
            throw new FormatException($"dailyReset.schedule '{s}' is not a valid 'HH:mm' value");
        }
        if (ts < TimeSpan.Zero || ts >= TimeSpan.FromHours(24))
            throw new FormatException($"dailyReset.schedule '{s}' must be within [00:00,24:00)");
        return ts;
    }

    private static TimeZoneInfo ResolveTimezone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("dailyReset.timezone must be a non-empty IANA identifier", nameof(id));
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException(
                $"dailyReset.timezone '{id}' is not a recognized IANA / Windows timezone identifier on this host. " +
                "On Linux, ensure the tzdata package is installed.", nameof(id), ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        var t = _timer;
        _timer = null;
        return t?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
