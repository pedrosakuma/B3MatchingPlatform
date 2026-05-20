namespace B3.Exchange.PostTrade;

/// <summary>
/// Result of <see cref="IPostTradeOrchestrator.ProcessBust"/>.
/// Surfaces the validator kind plus the matched fill (Accept only) so
/// the caller — the per-channel dispatcher — can emit the UMDF
/// <c>TradeBust_57</c> frame without re-scanning the audit log.
/// </summary>
public readonly record struct BustProcessOutcome(
    BustValidationKind Kind,
    PostTradeRecord MatchedFill,
    ulong ExistingCorrelationId);

/// <summary>
/// Post-trade boundary port (issue #380). Owns every piece of bust
/// validation state and IO that used to live on the dispatcher: the
/// dedup index, the audit-log root, the EOD drop directory, the
/// amendments publisher and the routing lock shared with
/// <c>EodFillsExporter</c>. The dispatcher retains the wire-encoding
/// half — frame emission, sequence numbering, command boundary
/// callbacks — and delegates the rest through this interface.
///
/// Composition (default <see cref="PostTradeOrchestrator"/> impl):
/// <list type="bullet">
///   <item>Validation funnels through <see cref="BustValidator"/>.</item>
///   <item>Idempotency tracking funnels through <see cref="BustDedupIndex"/>.</item>
///   <item>Audit/dedup writes funnel through <see cref="IPostTradeSink"/>.</item>
///   <item>Post-EOD republish funnels through <see cref="IAmendmentsPublisher"/>.</item>
///   <item>All disk-visible state changes happen under <see cref="RoutingLock"/>.</item>
/// </list>
/// </summary>
public interface IPostTradeOrchestrator
{
    /// <summary>
    /// Synchronization barrier shared with <c>EodFillsExporter</c>.
    /// Exposed so the EOD exporter can take the same monitor between
    /// its cancelled-set scan and the <c>fills.csv.done</c> rename,
    /// closing the race against late operator busts (ADR 0008 §3).
    /// </summary>
    object RoutingLock { get; }

    /// <summary>
    /// Validates the request, persists the resulting audit / dedup
    /// state under <see cref="RoutingLock"/>, fires reject-attempt
    /// records for terminal-reject kinds and publishes the
    /// <c>amendments.csv</c> refresh on post-EOD accept (and on
    /// idempotent replay against a closed day). Returns the validator
    /// verdict plus — for Accept — the matched fill so the caller can
    /// build the UMDF <c>TradeBust_57</c> frame.
    /// </summary>
    /// <param name="request">Caller-supplied bust request, already
    /// range-checked by the dispatcher (tradeDate must fit
    /// <c>LocalMktDate</c>).</param>
    /// <param name="channel">Channel number for per-channel file
    /// layout under the audit/drop roots.</param>
    /// <param name="tradeDateDaysSinceEpoch">Pre-computed days-since-Unix
    /// for the rejected attempt's audit row (echoed back into the
    /// <see cref="RejectAttemptRecord"/>); avoids re-deriving it here.</param>
    BustProcessOutcome ProcessBust(in BustRequest request, byte channel, int tradeDateDaysSinceEpoch);
}
