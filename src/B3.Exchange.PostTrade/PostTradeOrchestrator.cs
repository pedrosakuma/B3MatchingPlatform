using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Default in-process implementation of
/// <see cref="IPostTradeOrchestrator"/>. Composes
/// <see cref="BustValidator"/>, <see cref="BustDedupIndex"/>,
/// <see cref="IPostTradeSink"/> and <see cref="IAmendmentsPublisher"/>;
/// owns the routing monitor shared with <c>EodFillsExporter</c>.
///
/// Extracted from <c>ChannelDispatcher</c> for issue #380 — see
/// ADR 0010 for the boundary contract. All public methods are safe to
/// call from any thread; the dispatcher only ever calls
/// <see cref="ProcessBust"/> from its loop thread, but the
/// <see cref="RoutingLock"/> property is observed by the EOD exporter
/// on a different thread.
/// </summary>
public sealed class PostTradeOrchestrator : IPostTradeOrchestrator
{
    private readonly IPostTradeSink _sink;
    private readonly BustDedupIndex _dedup;
    private readonly string _auditRootDir;
    private readonly string? _dropRootDir;
    private readonly IAmendmentsPublisher? _amendmentsPublisher;
    private readonly ILogger _logger;

    public PostTradeOrchestrator(
        IPostTradeSink sink,
        BustDedupIndex dedup,
        string auditRootDir,
        string? dropRootDir = null,
        IAmendmentsPublisher? amendmentsPublisher = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(dedup);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDir);
        _sink = sink;
        _dedup = dedup;
        _auditRootDir = auditRootDir;
        _dropRootDir = dropRootDir;
        _amendmentsPublisher = amendmentsPublisher;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public object RoutingLock { get; } = new();

    /// <inheritdoc />
    public BustProcessOutcome ProcessBust(in BustRequest request, byte channel, int tradeDateDaysSinceEpoch)
    {
        var result = BustValidator.Validate(_auditRootDir, channel, request, _dedup);

        switch (result.Kind)
        {
            case BustValidationKind.Accept:
                {
                    var bust = new BustRecord(
                        request.TradeId, request.AttemptTransactTimeNanos,
                        result.MatchedFill.SecurityId, request.ReasonCode, request.BusterFirm,
                        request.CorrelationId, DeclaredTradeDateDays: tradeDateDaysSinceEpoch);

                    // ADR 0008 §3 routing: under the routing lock, decide
                    // pre-EOD vs post-EOD based on the existence of the
                    // target day's fills.csv.done sidecar. The lock closes
                    // the race with EodFillsExporter, which holds the same
                    // monitor between its cancelled-set scan and the .done
                    // rename.
                    DateOnly bustFileDate = request.TradeDate;
                    bool isPostEod = false;
                    lock (RoutingLock)
                    {
                        if (_dropRootDir != null && DoneSidecarExists(_dropRootDir, channel, request.TradeDate))
                        {
                            bustFileDate = DateOnly.FromDateTime(DateTime.UtcNow);
                            isPostEod = true;
                        }
                        _sink.OnBust(bust, bustFileDate);
                        _dedup.Add(request.TradeId, request.CorrelationId, request.TradeDate);
                    }

                    // ADR 0008 §4: post-EOD busts publish/refresh
                    // amendments.csv for the original trade's day so
                    // external consumers see the late correction.
                    if (isPostEod && _amendmentsPublisher != null && _dropRootDir != null)
                    {
                        try
                        {
                            _amendmentsPublisher.Publish(_auditRootDir, _dropRootDir, channel, request.TradeDate, DateTime.UtcNow);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "amendments.csv publish failed for channel={Channel} date={Date}; bust persisted but downstream consumers will only see it on next operator retry or restart",
                                channel, request.TradeDate);
                        }
                    }
                    break;
                }
            case BustValidationKind.IdempotentReplay:
                {
                    // No new audit write, no UMDF emit. BUT: if the
                    // original accept was a post-EOD bust whose
                    // amendments.csv publish failed (logged-only failure
                    // under §4), this is the operator's retry — re-run
                    // the publish so the missing row finally lands.
                    // AmendmentsPublisher regenerates the file in full
                    // from the audit log so this is naturally idempotent.
                    if (_amendmentsPublisher != null && _dropRootDir != null
                        && DoneSidecarExists(_dropRootDir, channel, request.TradeDate))
                    {
                        try
                        {
                            _amendmentsPublisher.Publish(_auditRootDir, _dropRootDir, channel, request.TradeDate, DateTime.UtcNow);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "amendments.csv republish on idempotent replay failed for channel={Channel} date={Date}; further retries required",
                                channel, request.TradeDate);
                        }
                    }
                    break;
                }
            case BustValidationKind.MissingDay:
                // No audit-log write (ADR §2.3).
                break;
            case BustValidationKind.UnknownTradeId:
            case BustValidationKind.AlreadyBustedDifferentCorrelation:
            case BustValidationKind.SecurityIdMismatch:
                {
                    ushort code = result.Kind switch
                    {
                        BustValidationKind.UnknownTradeId => (ushort)1,
                        BustValidationKind.AlreadyBustedDifferentCorrelation => (ushort)2,
                        BustValidationKind.SecurityIdMismatch => (ushort)3,
                        _ => (ushort)0,
                    };
                    var reject = new RejectAttemptRecord(
                        request.TradeId, request.AttemptTransactTimeNanos,
                        tradeDateDaysSinceEpoch, code, request.BusterFirm, request.CorrelationId);
                    _sink.OnRejectAttempt(reject);
                    break;
                }
        }

        return new BustProcessOutcome(result.Kind, result.MatchedFill, result.ExistingCorrelationId);
    }

    private static bool DoneSidecarExists(string dropRootDir, byte channel, DateOnly tradeDate)
    {
        return File.Exists(Path.Combine(
            dropRootDir,
            channel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            "fills.csv.done"));
    }
}
