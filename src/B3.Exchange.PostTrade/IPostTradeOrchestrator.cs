namespace B3.Exchange.PostTrade;

/// <summary>
/// Result of <see cref="IPostTradeOrchestrator.ProcessBust"/>.
/// Surfaces the validator kind plus the matched fill (Accept only) so
/// the caller — the per-channel dispatcher — can emit the UMDF
/// <c>TradeBust_57</c> frame without re-scanning the audit log.
///
/// <para><see cref="IsPostEodAccept"/> signals that the Accept landed
/// after the target day's <c>fills.csv.done</c> sidecar had been
/// produced. The caller MUST invoke
/// <see cref="IPostTradeOrchestrator.PublishPostEodAmendments"/>
/// after the UMDF frame is flushed so the persisted-but-unannounced
/// window matches the legacy in-dispatcher ordering (ADR 0008 §4 /
/// ADR 0010 §"Implementation notes"): audit write → UMDF emit →
/// amendments file. Skipping the call when <see cref="IsPostEodAccept"/>
/// is true leaves the bust persisted but invisible in
/// <c>amendments.csv</c> until the next operator retry or restart.</para>
/// </summary>
public readonly record struct BustProcessOutcome(
    BustValidationKind Kind,
    PostTradeRecord MatchedFill,
    ulong ExistingCorrelationId,
    bool IsPostEodAccept);

/// <summary>
/// Post-trade boundary port (issue #380). Owns every piece of bust
/// validation state and IO that used to live on the dispatcher: the
/// dedup index, the audit-log root, the EOD drop directory, the
/// amendments publisher and the routing lock shared with
/// <c>EodFillsExporter</c>. The dispatcher retains the wire-encoding
/// half — frame emission, sequence numbering, command boundary
/// callbacks — and delegates the rest through this interface.
///
/// <para><b>Threading contract:</b> implementations MUST be safe for
/// the <see cref="RoutingLock"/> property to be observed from any
/// thread (the EOD exporter holds it from its own loop). The mutating
/// methods (<see cref="ProcessBust"/>,
/// <see cref="PublishPostEodAmendments"/>) are <i>thread-affine</i> —
/// the dispatcher invariably calls them from its single loop thread
/// (ADR 0009), and the default implementation relies on that to keep
/// the dedup-check / dedup-add pair atomic without a second lock.
/// Callers outside the dispatcher loop MUST serialize their own
/// invocations.</para>
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
    /// state under <see cref="RoutingLock"/>, and fires reject-attempt
    /// records for terminal-reject kinds. On <see cref="BustValidationKind.Accept"/>
    /// the post-EOD amendments republish is <i>not</i> performed here
    /// so the caller can sequence it after a successful UMDF frame
    /// flush — see <see cref="BustProcessOutcome.IsPostEodAccept"/>.
    /// On <see cref="BustValidationKind.IdempotentReplay"/> against a
    /// closed day the amendments republish is performed inline (no
    /// UMDF emit follows so there is no ordering concern).
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

    /// <summary>
    /// Republishes <c>amendments.csv</c> for the target day after the
    /// dispatcher has successfully flushed the <c>TradeBust_57</c>
    /// frame. MUST only be called for outcomes where
    /// <see cref="BustProcessOutcome.IsPostEodAccept"/> is <c>true</c>.
    /// Failures are logged and swallowed (ADR 0008 §4): the bust is
    /// already persisted on disk; operators retry via the same
    /// idempotent endpoint to land the missing row.
    /// </summary>
    void PublishPostEodAmendments(in BustRequest request, byte channel);
}
