namespace B3.Exchange.Matching;

/// <summary>
/// Engine-level snapshot consumed by the persistence layer (issue #260).
/// Captures every piece of <see cref="MatchingEngine"/> state required to
/// restore a working order book across a process restart, while leaving
/// transient artefacts (auction-top throttling, in-flight dispatch flags)
/// off the wire — those are recomputed lazily after restore.
///
/// <para><b>Out of scope (v1):</b> stop orders (<c>_stopsBySymbol</c>) and
/// auction indicative throttling (<c>_auctionTopById</c>) — restoring these
/// safely would require replaying the trigger price stream. Operators that
/// rely on them should drain in-flight stops before restart; the omission
/// is documented in <c>docs/EXCHANGE-SIMULATOR.md</c>.</para>
/// </summary>
public sealed record EngineStateSnapshot(
    long NextOrderId,
    uint NextTradeId,
    uint RptSeq,
    IReadOnlyList<EngineStateSnapshot.PhaseEntry> Phases,
    IReadOnlyList<EngineStateSnapshot.BookSnapshot> Books)
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
