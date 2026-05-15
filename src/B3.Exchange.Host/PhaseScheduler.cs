using System.Globalization;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// Issue #321: target abstraction for <see cref="PhaseScheduler"/>. The
/// scheduler does not know whether the underlying enqueue is wired to a
/// <see cref="ChannelDispatcher"/> or a test double; it only sees the
/// security set it can route to and the two enqueue verbs.
/// </summary>
public interface IPhaseScheduledTarget
{
    /// <summary>Every securityId the host can route through to a channel.
    /// Used as the implicit target list for empty
    /// <c>PhaseSchedulerGroup.Instruments</c>.</summary>
    IReadOnlyCollection<long> RoutedSecurityIds { get; }

    /// <summary>Returns <c>false</c> if the security is unknown / queue
    /// rejected the enqueue.</summary>
    bool EnqueueSetPhase(long securityId, TradingPhase target);

    /// <summary>Returns <c>false</c> if the security is unknown / queue
    /// rejected the enqueue.</summary>
    bool EnqueueUncrossAuction(long securityId, TradingPhase target);
}

internal sealed class DispatcherRoutingPhaseTarget : IPhaseScheduledTarget
{
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _routing;

    public DispatcherRoutingPhaseTarget(IReadOnlyDictionary<long, ChannelDispatcher> routing)
    {
        _routing = routing;
    }

    public IReadOnlyCollection<long> RoutedSecurityIds => (IReadOnlyCollection<long>)_routing.Keys;

    public bool EnqueueSetPhase(long securityId, TradingPhase target)
        => _routing.TryGetValue(securityId, out var disp)
            && disp.EnqueueOperatorSetTradingPhase(securityId, target);

    public bool EnqueueUncrossAuction(long securityId, TradingPhase target)
        => _routing.TryGetValue(securityId, out var disp)
            && disp.EnqueueOperatorUncrossAuction(securityId, target);
}

/// <summary>
/// Issue #321: arms a <see cref="Timer"/> per
/// <see cref="PhaseSchedulerEntry"/> in the configured groups so the host
/// drives the daily phase plan automatically (e.g. at 10:00 → uncross
/// opening auction; at 17:25 → enter FinalClosingCall; etc.). Modeled on
/// <see cref="DailyResetScheduler"/>: each entry computes its next firing
/// instant by interpreting <c>HH:mm</c> in the configured IANA timezone
/// and re-arms after each fire.
/// </summary>
public sealed class PhaseScheduler : IAsyncDisposable
{
    private readonly PhaseSchedulerConfig _config;
    private readonly IPhaseScheduledTarget _target;
    private readonly MetricsRegistry? _metrics;
    private readonly ILogger<PhaseScheduler> _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly List<ScheduledEntry> _entries = new();
    private int _disposed;

    public PhaseScheduler(PhaseSchedulerConfig config, IReadOnlyDictionary<long, ChannelDispatcher> routing,
        MetricsRegistry? metrics, ILogger<PhaseScheduler> logger,
        Func<DateTimeOffset>? utcNowOverride = null)
        : this(config, new DispatcherRoutingPhaseTarget(routing), metrics, logger, utcNowOverride)
    { }

    public PhaseScheduler(PhaseSchedulerConfig config, IPhaseScheduledTarget target,
        MetricsRegistry? metrics, ILogger<PhaseScheduler> logger,
        Func<DateTimeOffset>? utcNowOverride = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _target = target;
        _metrics = metrics;
        _logger = logger;
        _utcNow = utcNowOverride ?? (() => DateTimeOffset.UtcNow);
        BuildEntries();
    }

    private void BuildEntries()
    {
        foreach (var group in _config.Groups)
        {
            var tz = ResolveTimezone(group.Timezone);
            IReadOnlyList<long> instruments = group.Instruments.Count == 0
                ? _target.RoutedSecurityIds.ToArray()
                : group.Instruments;
            foreach (var entry in group.Schedule)
            {
                var local = ParseHHmm(entry.At);
                var action = ParseAction(entry.Action);
                var target = ParsePhase(entry.Target);
                _entries.Add(new ScheduledEntry(group.Name, tz, local, action, target, instruments));
            }
        }
    }

    public IReadOnlyList<ScheduledEntry> Entries => _entries;

    public void Start()
    {
        var now = _utcNow();
        foreach (var entry in _entries) ArmEntry(entry, now);
        _logger.LogInformation("phase scheduler armed: entries={Count}", _entries.Count);
    }

    /// <summary>
    /// Test seam: enqueue every entry whose next firing instant is
    /// &lt;= the current (overridden) clock without waiting for a
    /// <see cref="Timer"/>. Re-arming is skipped — the test owns the
    /// clock. Returns the count of entries fired.
    /// </summary>
    public int RunDueNow()
    {
        var now = _utcNow();
        int fired = 0;
        foreach (var entry in _entries)
        {
            var next = ComputeNextFiringUtc(now.AddSeconds(-1), entry.LocalTime, entry.Timezone);
            if (next <= now)
            {
                FireEntry(entry);
                fired++;
            }
        }
        return fired;
    }

    private void ArmEntry(ScheduledEntry entry, DateTimeOffset now)
    {
        var next = ComputeNextFiringUtc(now, entry.LocalTime, entry.Timezone);
        var delay = next - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        entry.Timer?.Dispose();
        entry.Timer = new Timer(_ => OnTimer(entry), null, delay, Timeout.InfiniteTimeSpan);
        _logger.LogInformation(
            "phase scheduler entry armed: group={Group} action={Action} target={Target} at={At:hh\\:mm} tz={Tz} next-firing-utc={Next:o}",
            entry.GroupName, entry.Action, entry.TargetPhase, entry.LocalTime, entry.Timezone.Id, next);
    }

    private void OnTimer(ScheduledEntry entry)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { FireEntry(entry); }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
                ArmEntry(entry, _utcNow());
        }
    }

    private void FireEntry(ScheduledEntry entry)
    {
        foreach (var secId in entry.Instruments)
        {
            bool ok;
            try
            {
                ok = entry.Action == ScheduledAction.UncrossAuction
                    ? _target.EnqueueUncrossAuction(secId, entry.TargetPhase)
                    : _target.EnqueueSetPhase(secId, entry.TargetPhase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "phase scheduler fire threw: group={Group} action={Action} target={Target} secId={SecurityId}",
                    entry.GroupName, entry.Action, entry.TargetPhase, secId);
                continue;
            }
            if (!ok)
            {
                _logger.LogWarning(
                    "phase scheduler enqueue rejected: group={Group} action={Action} target={Target} secId={SecurityId}",
                    entry.GroupName, entry.Action, entry.TargetPhase, secId);
                continue;
            }
            // The trigger label distinguishes scheduler-driven transitions
            // from operator HTTP overrides. We do not know the previous
            // phase off-thread; record TargetPhase for both labels so
            // operators can still pivot on the target distribution.
            _metrics?.IncPhaseTransition(secId, entry.TargetPhase, entry.TargetPhase, "scheduled");
            _logger.LogInformation(
                "phase scheduler fired: group={Group} action={Action} target={Target} secId={SecurityId}",
                entry.GroupName, entry.Action, entry.TargetPhase, secId);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        foreach (var entry in _entries)
        {
            try { entry.Timer?.Dispose(); }
            catch { }
            entry.Timer = null;
        }
        return ValueTask.CompletedTask;
    }

    public sealed class ScheduledEntry
    {
        public string GroupName { get; }
        public TimeZoneInfo Timezone { get; }
        public TimeSpan LocalTime { get; }
        public ScheduledAction Action { get; }
        public TradingPhase TargetPhase { get; }
        public IReadOnlyList<long> Instruments { get; }
        internal Timer? Timer { get; set; }

        internal ScheduledEntry(string groupName, TimeZoneInfo tz, TimeSpan localTime,
            ScheduledAction action, TradingPhase targetPhase, IReadOnlyList<long> instruments)
        {
            GroupName = groupName;
            Timezone = tz;
            LocalTime = localTime;
            Action = action;
            TargetPhase = targetPhase;
            Instruments = instruments;
        }
    }

    public enum ScheduledAction { EnterPhase, UncrossAuction }

    private static ScheduledAction ParseAction(string s)
    {
        if (string.Equals(s, "EnterPhase", StringComparison.OrdinalIgnoreCase)) return ScheduledAction.EnterPhase;
        if (string.Equals(s, "UncrossAuction", StringComparison.OrdinalIgnoreCase)) return ScheduledAction.UncrossAuction;
        throw new FormatException($"phaseScheduler.action '{s}' is not a recognized action (EnterPhase|UncrossAuction)");
    }

    private static TradingPhase ParsePhase(string s)
    {
        if (Enum.TryParse<TradingPhase>(s, ignoreCase: true, out var phase))
            return phase;
        throw new FormatException($"phaseScheduler.target '{s}' is not a recognized TradingPhase");
    }

    private static TimeSpan ParseHHmm(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("phaseScheduler.at must be a non-empty 'HH:mm' string", nameof(s));
        if (!TimeSpan.TryParseExact(s, @"h\:mm", CultureInfo.InvariantCulture, out var ts) &&
            !TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out ts))
        {
            throw new FormatException($"phaseScheduler.at '{s}' is not a valid 'HH:mm' value");
        }
        if (ts < TimeSpan.Zero || ts >= TimeSpan.FromHours(24))
            throw new FormatException($"phaseScheduler.at '{s}' must be within [00:00,24:00)");
        return ts;
    }

    private static TimeZoneInfo ResolveTimezone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("phaseScheduler.timezone must be a non-empty IANA identifier", nameof(id));
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException(
                $"phaseScheduler.timezone '{id}' is not recognized. On Linux, ensure tzdata is installed.",
                nameof(id), ex);
        }
    }

    /// <summary>
    /// Same DST-aware computation as <see cref="DailyResetScheduler.ComputeNextFiringUtc"/>.
    /// </summary>
    public static DateTimeOffset ComputeNextFiringUtc(
        DateTimeOffset nowUtc, TimeSpan localTime, TimeZoneInfo tz)
        => DailyResetScheduler.ComputeNextFiringUtc(nowUtc, localTime, tz);
}
