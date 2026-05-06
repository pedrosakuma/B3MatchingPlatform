namespace B3.Exchange.Matching;

/// <summary>
/// Engine-level snapshot consumed by the persistence layer (issue #260).
/// Captures every piece of <see cref="MatchingEngine"/> state required to
/// restore a working order book across a process restart, while leaving
/// transient artefacts (auction-top throttling, in-flight dispatch flags)
/// off the wire — those are recomputed lazily after restore.
///
/// <para><b>Out of scope:</b> auction indicative throttling
/// (<c>_auctionTopById</c>) — restoring it safely would require replaying
/// the trigger price stream and the omission only causes one transient
/// duplicate frame post-restart. Operators that depend on auction
/// throttling should drain in-flight indicative state before restart;
/// the limitation is documented in <c>docs/EXCHANGE-SIMULATOR.md</c>.</para>
///
/// <para>Stop orders (<c>_stopsBySymbol</c> / <c>_stopById</c>) are
/// persisted as of issue #262: the optional <see cref="Stops"/> list
/// carries every untriggered stop. Old snapshots that pre-date #262
/// deserialise with <see cref="Stops"/> null → treated as empty, so the
/// schema remains backward compatible without a version bump.</para>
/// </summary>
public sealed record EngineStateSnapshot(
    long NextOrderId,
    uint NextTradeId,
    uint RptSeq,
    IReadOnlyList<EngineStateSnapshot.PhaseEntry> Phases,
    IReadOnlyList<EngineStateSnapshot.BookSnapshot> Books,
    IReadOnlyList<RestingStopRecord>? Stops = null)
{
    public sealed record PhaseEntry(long SecurityId, TradingPhase Phase);

    public sealed record BookSnapshot(long SecurityId, IReadOnlyList<RestingOrderRecord> Orders);
}

/// <summary>
/// Persistable view of a single resting order — superset of
/// <see cref="RestingOrderView"/> that also carries the iceberg/Tif fields
/// required to faithfully rebuild a <c>RestingOrder</c> on restore.
/// </summary>
public sealed record RestingOrderRecord(
    long OrderId,
    string ClOrdId,
    Side Side,
    long PriceMantissa,
    long RemainingQuantity,
    uint EnteringFirm,
    ulong InsertTimestampNanos,
    TimeInForce Tif,
    long MaxFloor,
    long HiddenQuantity);

/// <summary>
/// Persistable view of a single untriggered stop order (issue #262). Mirrors
/// the engine's private <c>RestingStop</c>: enough fields to faithfully
/// reconstruct the parked order so that future trigger evaluations and
/// cancels behave exactly as before the restart. <see cref="LimitPriceMantissa"/>
/// is zero for <see cref="OrderType.StopLoss"/> and the limit price for
/// <see cref="OrderType.StopLimit"/>.
/// </summary>
public sealed record RestingStopRecord(
    long OrderId,
    string ClOrdId,
    long SecurityId,
    Side Side,
    OrderType StopType,
    TimeInForce Tif,
    long StopPxMantissa,
    long LimitPriceMantissa,
    long Quantity,
    uint EnteringFirm,
    ulong EnteredAtNanos);
