using B3.Exchange.Core;
using B3.Exchange.Instruments;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// OPT-03 / ADR 0013 — end-of-trading-day option-series expiry sweep.
///
/// <para>At the end of each trading day the host walks the configured
/// option instruments and, for every series whose
/// <see cref="Instrument.ExpirationDate"/> is on or before the supplied
/// "today" (UTC), enqueues an <c>EnqueueOperatorExpireSecurity</c>
/// command on the channel that owns the security. The dispatcher then
/// cancels every resting order for the series and transitions the
/// trading phase to <see cref="B3.Exchange.Matching.TradingPhase.Close"/>
/// in one packet under the channel's single-writer invariant (ADR
/// 0009). The instrument stays loaded for the remainder of the calendar
/// day; the next-day boot rejects it via <c>InstrumentLoader</c>
/// (T-1).</para>
///
/// <para>The sweeper does not own a timer — it is invoked from
/// <c>ExchangeHost.TriggerDailyReset</c> (the natural "end of trading
/// day" boundary in this codebase). This keeps the expiry decision
/// aligned with the rest of the daily-rollover flow: the listener is
/// not yet torn down, so any per-order <c>ER_Cancel</c> still routes to
/// the originating session.</para>
/// </summary>
public sealed class OptionExpirySweeper
{
    /// <summary>One configured option instrument together with the
    /// dispatcher that owns its <c>SecurityId</c>.</summary>
    public interface IExpireSink
    {
        long SecurityId { get; }
        string Symbol { get; }
        DateOnly ExpirationDate { get; }
        byte ChannelNumber { get; }

        /// <summary>Returns <c>false</c> when the inbound queue rejects
        /// the enqueue (queue full / WAL halted). The sweeper logs and
        /// continues with the next series.</summary>
        bool EnqueueExpire(TaskCompletionSource<ChannelDispatcher.ExpireSecurityOutcome>? completion);
    }

    private readonly IReadOnlyList<IExpireSink> _sinks;
    private readonly ILogger<OptionExpirySweeper> _logger;
    private readonly Func<DateOnly> _todayUtc;

    public OptionExpirySweeper(IReadOnlyList<IExpireSink> sinks,
        ILogger<OptionExpirySweeper> logger,
        Func<DateOnly>? todayUtcOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(logger);
        _sinks = sinks;
        _logger = logger;
        _todayUtc = todayUtcOverride ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>True when the host has at least one configured option
    /// series — the daily-reset path uses this to skip the sweep work
    /// + log entirely on equities-only deployments.</summary>
    public bool HasOptionSeries => _sinks.Count > 0;

    /// <summary>
    /// Walks every configured option series and, for each one whose
    /// expiration date is on or before <see cref="_todayUtc"/>,
    /// enqueues an <c>ExpireSecurity</c> command on the owning channel.
    ///
    /// <para>When <paramref name="waitTimeout"/> is provided (default
    /// 5s) the call blocks until every enqueued command has finished
    /// processing on the dispatcher thread — this is required from
    /// <c>TriggerDailyReset</c> so the gateway can flush ER_Cancel
    /// frames before <c>TerminateAllSessions</c> tears down the
    /// response channels. Pass <see cref="TimeSpan.Zero"/> to fire and
    /// forget.</para>
    ///
    /// <para>Returns the number of series that were enqueued. Per-series
    /// enqueue failures are logged at warn level and do not abort the
    /// sweep.</para>
    /// </summary>
    public int SweepExpiredSeries(string trigger, TimeSpan? waitTimeout = null)
    {
        if (_sinks.Count == 0) return 0;
        var today = _todayUtc();
        int enqueued = 0;
        int skipped = 0;
        int rejected = 0;
        var pending = waitTimeout is { } t && t > TimeSpan.Zero
            ? new List<Task>()
            : null;
        foreach (var sink in _sinks)
        {
            if (sink.ExpirationDate > today)
            {
                skipped++;
                continue;
            }
            var completion = pending is null
                ? null
                : new TaskCompletionSource<ChannelDispatcher.ExpireSecurityOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            bool ok;
            try
            {
                ok = sink.EnqueueExpire(completion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "option-expiry sweep ({Trigger}): enqueue threw symbol={Symbol} secId={SecurityId} channel={Channel}",
                    trigger, sink.Symbol, sink.SecurityId, sink.ChannelNumber);
                rejected++;
                continue;
            }
            if (!ok)
            {
                _logger.LogWarning(
                    "option-expiry sweep ({Trigger}): enqueue rejected symbol={Symbol} secId={SecurityId} channel={Channel}",
                    trigger, sink.Symbol, sink.SecurityId, sink.ChannelNumber);
                rejected++;
                continue;
            }
            enqueued++;
            if (completion != null) pending!.Add(completion.Task);
            _logger.LogInformation(
                "option-expiry sweep ({Trigger}): enqueued symbol={Symbol} secId={SecurityId} channel={Channel} expirationDate={ExpirationDate} today={Today}",
                trigger, sink.Symbol, sink.SecurityId, sink.ChannelNumber, sink.ExpirationDate, today);
        }
        if (pending is { Count: > 0 })
        {
            try
            {
                Task.WaitAll(pending.ToArray(), waitTimeout!.Value);
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex,
                    "option-expiry sweep ({Trigger}): one or more ExpireSecurity completions faulted",
                    trigger);
            }
        }
        _logger.LogInformation(
            "option-expiry sweep ({Trigger}): enqueued={Enqueued} skipped={Skipped} rejected={Rejected} total={Total} today={Today}",
            trigger, enqueued, skipped, rejected, _sinks.Count, today);
        return enqueued;
    }

    /// <summary>
    /// Production sink that bridges an <see cref="Instrument"/> +
    /// <see cref="ChannelDispatcher"/> pair to the expire-security
    /// enqueue path. Kept as a nested type so test code can drop in
    /// its own <see cref="IExpireSink"/> without depending on a live
    /// dispatcher.
    /// </summary>
    public sealed class DispatcherExpireSink : IExpireSink
    {
        private readonly ChannelDispatcher _dispatcher;
        public long SecurityId { get; }
        public string Symbol { get; }
        public DateOnly ExpirationDate { get; }
        public byte ChannelNumber => _dispatcher.ChannelNumber;

        public DispatcherExpireSink(Instrument instrument, ChannelDispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(instrument);
            ArgumentNullException.ThrowIfNull(dispatcher);
            if (instrument.ExpirationDate is null)
                throw new ArgumentException("DispatcherExpireSink requires an instrument with an ExpirationDate.", nameof(instrument));
            _dispatcher = dispatcher;
            SecurityId = instrument.SecurityId;
            Symbol = instrument.Symbol;
            ExpirationDate = instrument.ExpirationDate.Value;
        }

        public bool EnqueueExpire(TaskCompletionSource<ChannelDispatcher.ExpireSecurityOutcome>? completion)
            => _dispatcher.EnqueueOperatorExpireSecurity(SecurityId, completion);
    }

    /// <summary>
    /// Builds the production sink set from the host's per-channel
    /// instrument lists. Skips non-option instruments and instruments
    /// without an <see cref="Instrument.ExpirationDate"/>.
    /// </summary>
    public static IReadOnlyList<IExpireSink> BuildSinks(
        IEnumerable<(Instrument Instrument, ChannelDispatcher Dispatcher)> instrumentsByDispatcher)
    {
        ArgumentNullException.ThrowIfNull(instrumentsByDispatcher);
        var result = new List<IExpireSink>();
        foreach (var (inst, disp) in instrumentsByDispatcher)
        {
            if (!InstrumentSecurityTypes.IsOption(inst.SecurityType)) continue;
            if (inst.ExpirationDate is null) continue;
            result.Add(new DispatcherExpireSink(inst, disp));
        }
        return result;
    }
}
