namespace B3.Exchange.Matching;

public enum Side : byte { Buy, Sell }

/// <summary>
/// TimeInForce values supported by the matching engine. Wire mapping
/// lives in the gateway decoder; semantic implications:
/// <list type="bullet">
///   <item><c>Day</c>: rests until end of day (no daily-reset operator
///   command exists yet, so behaves as if persistent).</item>
///   <item><c>IOC</c>/<c>FOK</c>: marketable-only; remainder is cancelled.</item>
///   <item><c>Gtc</c>: persistent across hypothetical daily-resets. Currently
///   identical to <c>Day</c> until the daily-reset operator command lands.</item>
///   <item><c>Gtd</c>: rejected with <see cref="RejectReason.TimeInForceNotSupported"/>
///   until the wire path plumbs <c>ExpireDate</c> through
///   <see cref="NewOrderCommand"/>.</item>
///   <item><c>AtClose</c>: only accepted while the instrument is in
///   <see cref="TradingPhase.FinalClosingCall"/>.</item>
///   <item><c>GoodForAuction</c>: only accepted while the instrument is in
///   <see cref="TradingPhase.Reserved"/> (pre-open / auction).</item>
/// </list>
/// </summary>
public enum TimeInForce : byte { Day, IOC, FOK, Gtc, Gtd, AtClose, GoodForAuction }

public enum OrderType : byte { Limit, Market, StopLoss, StopLimit, MarketWithLeftover }

/// <summary>
/// Per-instrument trading phase (gap-functional §5 / #201). The values
/// map directly to the SBE <c>SecurityTradingStatus</c> enum used in
/// UMDF <c>SecurityStatus_3</c> so the integration layer can encode
/// without translation.
/// </summary>
public enum TradingPhase : byte
{
    /// <summary>Trading halt (PAUSE = 2 in UMDF).</summary>
    Pause = 2,
    /// <summary>Closed; no orders accepted (CLOSE = 4 in UMDF).</summary>
    Close = 4,
    /// <summary>Continuous trading (OPEN = 17 in UMDF).</summary>
    Open = 17,
    /// <summary>Forbidden / unavailable for trading (FORBIDDEN = 18 in UMDF).</summary>
    Forbidden = 18,
    /// <summary>Pre-open / Reserved auction phase (RESERVED = 21 in UMDF).</summary>
    Reserved = 21,
    /// <summary>Final closing call (FINAL_CLOSING_CALL = 101 in UMDF).</summary>
    FinalClosingCall = 101,
}

/// <summary>
/// Reasons a matching command can be rejected. Some rejects occur before any state change;
/// others (e.g., <see cref="SelfTradePrevention"/>) may occur after fills against other firms.
/// </summary>
public enum RejectReason : byte
{
    UnknownInstrument,
    PriceOutOfBand,
    PriceNotOnTick,
    PriceNonPositive,
    QuantityNotMultipleOfLot,
    QuantityNonPositive,
    UnknownOrderId,
    FokUnfillable,
    MarketNoLiquidity,
    MarketNotImmediateOrCancel,
    InvalidTimeInForceForMarket,
    /// <summary>The aggressor would have crossed against a resting order from
    /// the same <see cref="NewOrderCommand.EnteringFirm"/> and the channel's
    /// <see cref="SelfTradePrevention"/> policy is configured to cancel the
    /// aggressor's residual quantity.</summary>
    SelfTradePrevention,
    /// <summary>The instrument's current <see cref="TradingPhase"/>
    /// disallows new orders for the requested
    /// <see cref="TimeInForce"/>. Issue #201.</summary>
    MarketClosed,
    /// <summary>The requested <see cref="TimeInForce"/> is wire-valid but
    /// not yet implemented in the matching engine (e.g. <c>Gtd</c> until
    /// <c>ExpireDate</c> is plumbed through the inbound command).
    /// Issue #202.</summary>
    TimeInForceNotSupported,
    /// <summary>The aggressor specified a non-zero
    /// <see cref="NewOrderCommand.MinQty"/> but the immediately fillable
    /// quantity against the opposite book at the order's limit price was
    /// less than that minimum. The order is rejected before any state
    /// change. Issue #203 (MinQty subset).</summary>
    MinQtyNotMet,
    /// <summary>A request field was outside the engine's accepted range
    /// (e.g. <see cref="NewOrderCommand.MinQty"/> &gt;
    /// <see cref="NewOrderCommand.Quantity"/>). Issue #203.</summary>
    InvalidField,
}

/// <summary>
/// Per-channel self-trade prevention policy. Evaluated by the engine each time
/// an aggressor would cross against a resting order whose
/// <c>EnteringFirm</c> matches the aggressor's. Default <see cref="None"/>
/// preserves the original "trade as today" behaviour.
/// </summary>
public enum SelfTradePrevention : byte
{
    /// <summary>No self-trade prevention; aggressor and maker trade normally.</summary>
    None,
    /// <summary>Cancel the aggressor's remaining (post-other-firm-fills) residual
    /// and stop further matching. Trades already executed against other firms
    /// stand. The conflicting resting order is left untouched.</summary>
    CancelAggressor,
    /// <summary>Cancel the conflicting resting order and continue matching the
    /// aggressor against the next maker (which may be from a different firm).</summary>
    CancelResting,
    /// <summary>Cancel both the conflicting resting order and the aggressor's
    /// residual; stop further matching.</summary>
    CancelBoth,
}

/// <summary>
/// Reason an order leaves the book via <see cref="IMatchingEventSink.OnOrderCanceled"/>.
/// </summary>
public enum CancelReason : byte
{
    /// <summary>Explicit <see cref="CancelOrderCommand"/> from the client.</summary>
    Client,
    /// <summary>IOC remainder after partial fill.</summary>
    IocRemainder,
    /// <summary>Market remainder after liquidity was exhausted.</summary>
    MarketRemainder,
    /// <summary>Replace lost time priority — old resting order is logically deleted
    /// before the new one is inserted (caller emits DEL+NEW MBO frames).</summary>
    ReplaceLostPriority,
    /// <summary>Resting order removed by the channel's <see cref="SelfTradePrevention"/>
    /// policy because an incoming aggressor from the same firm would have
    /// crossed against it.</summary>
    SelfTradePrevention,
    /// <summary>Resting order cancelled by an OrderMassActionRequest
    /// (template 701 → ER_Cancel; spec §4.8 / #GAP-19).</summary>
    MassCancel,
}

/// <summary>
/// New order command (Limit DAY/IOC/FOK or Market IOC/FOK).
/// All timestamps are caller-supplied UNIX-epoch nanoseconds; the engine
/// does not consult any clock so tests are fully deterministic.
/// </summary>
public sealed record NewOrderCommand(
    string ClOrdId,
    long SecurityId,
    Side Side,
    OrderType Type,
    TimeInForce Tif,
    long PriceMantissa,
    long Quantity,
    uint EnteringFirm,
    ulong EnteredAtNanos)
{
    /// <summary>
    /// Optional minimum-fill (FIX MinQty). When non-zero, the engine
    /// requires that at submission time the immediately fillable quantity
    /// against the opposite side at this order's limit (or any price for
    /// market orders) is at least <c>MinQty</c>; otherwise the order is
    /// rejected with <see cref="RejectReason.MinQtyNotMet"/> and never
    /// touches the book. Must satisfy <c>0 &lt; MinQty &lt;= Quantity</c>;
    /// values outside that range yield
    /// <see cref="RejectReason.InvalidField"/>. Default 0 means "no
    /// minimum-fill constraint" and preserves legacy behaviour.
    /// Issue #203 (MinQty subset).
    /// </summary>
    public ulong MinQty { get; init; }

    /// <summary>
    /// Optional iceberg visible quantity (FIX MaxFloor). When non-zero
    /// and strictly less than <see cref="Quantity"/>, the engine exposes
    /// only <c>MaxFloor</c> shares at a time on the book; on full
    /// consumption of the visible slice the engine replenishes a new
    /// visible slice from the hidden reserve and re-inserts the order at
    /// the back of the same price level, losing time priority. Must
    /// satisfy <c>0 &lt; MaxFloor &lt;= Quantity</c> and be a multiple of
    /// the instrument's lot size; values outside that range yield
    /// <see cref="RejectReason.InvalidField"/>. Iceberg requires
    /// <see cref="OrderType.Limit"/> and a resting TIF
    /// (<see cref="TimeInForce.Day"/> or <see cref="TimeInForce.Gtc"/>);
    /// IOC/FOK/Market combinations are rejected with
    /// <see cref="RejectReason.InvalidField"/>. <c>MaxFloor == Quantity</c>
    /// is accepted as a no-op (degenerate iceberg with no hidden reserve).
    /// Default 0 means "not an iceberg". Issue #211.
    /// </summary>
    public ulong MaxFloor { get; init; }

    /// <summary>
    /// Optional stop trigger price (FIX StopPx). Required for
    /// <see cref="OrderType.StopLoss"/> and <see cref="OrderType.StopLimit"/>;
    /// must be 0 for <see cref="OrderType.Limit"/> and <see cref="OrderType.Market"/>.
    /// Buy stops trigger when the last trade price is &gt;= StopPx; sell
    /// stops trigger when the last trade price is &lt;= StopPx. On
    /// trigger, a StopLoss is routed as a Market order and a StopLimit
    /// as a Limit order at <see cref="PriceMantissa"/>. Issue #214.
    /// </summary>
    public long StopPxMantissa { get; init; }
}

public sealed record CancelOrderCommand(
    string ClOrdId,
    long SecurityId,
    long OrderId,
    ulong EnteredAtNanos);

/// <summary>
/// Replace command. <see cref="NewQuantity"/> is interpreted as the new
/// <em>remaining open quantity</em> (not the original total). Replace cannot
/// change Side, SecurityId, Type or TIF — those would be a new order.
/// Priority is preserved iff (PriceMantissa unchanged AND NewQuantity &lt;= current
/// remaining qty); otherwise the engine emits a <see cref="OrderCanceledEvent"/>
/// with <see cref="CancelReason.ReplaceLostPriority"/> followed by an
/// <see cref="OrderAcceptedEvent"/> for the replacement (which may then cross
/// or rest like a brand-new order, including emitting trades).
/// </summary>
public sealed record ReplaceOrderCommand(
    string ClOrdId,
    long SecurityId,
    long OrderId,
    long NewPriceMantissa,
    long NewQuantity,
    ulong EnteredAtNanos)
{
    /// <summary>
    /// New order type for the replacement. <c>null</c> means "preserve
    /// the resting order's type" (always <see cref="OrderType.Limit"/>
    /// for an order that is on the book). Setting <c>OrderType.Market</c>
    /// turns the priority-loss path into a market aggressor that consumes
    /// liquidity and never rests; <see cref="NewTif"/> must then be
    /// IOC or FOK. Issue #204.
    /// </summary>
    public OrderType? NewOrdType { get; init; }

    /// <summary>
    /// New TIF for the replacement. <c>null</c> means "preserve the
    /// resting order's original TIF". Issue #204 (was previously hard-
    /// coded to <see cref="TimeInForce.Day"/>).
    /// </summary>
    public TimeInForce? NewTif { get; init; }
}

/// <summary>
/// Cross order command (NewOrderCross template 106 / spec §4.6.1 / §16.1.5).
/// Submitted as a single inbound frame containing both legs at the same
/// security/qty/price; the engine processes them as a single atomic dispatch
/// turn so the cross cannot be split, dropped half-way, or interleaved with
/// other producers (Self-Trading Prevention is tracked separately as #14;
/// without STP both legs cross naturally on the book).
///
/// <para>Issue #218 (Onda L · L5) extended this with <see cref="CrossType"/>,
/// <see cref="CrossPrioritization"/>, and <see cref="MaxSweepQty"/>. The
/// defaults reproduce the pre-#218 behavior (AllOrNone cross between the
/// two legs, Buy submitted first, no book interaction).</para>
/// </summary>
public sealed record CrossOrderCommand(
    NewOrderCommand Buy,
    NewOrderCommand Sell,
    ulong BuyClOrdIdValue,
    ulong SellClOrdIdValue,
    ulong CrossId)
{
    /// <summary>Spec §16.1.5 / SBE enum <c>CrossType</c>. Default
    /// <see cref="CrossType.AllOrNone"/> reproduces the pre-#218 behavior:
    /// the two legs cross internally with no public-book interaction.
    /// <see cref="CrossType.AgainstBook"/> activates the
    /// <see cref="MaxSweepQty"/> sweep phase. Auction-related variants
    /// (VWAP, ClosingPrice) are deferred and rejected at the decoder.</summary>
    public CrossType CrossType { get; init; } = CrossType.AllOrNone;

    /// <summary>Spec §16.1.5 / SBE enum <c>CrossPrioritization</c>. Controls
    /// which leg submits first into the engine (and therefore which leg
    /// gets to sweep first when <see cref="CrossType"/> is
    /// <see cref="Matching.CrossType.AgainstBook"/>).
    /// <see cref="CrossPrioritization.None"/> (default) treats Buy as the
    /// implicitly prioritized side, matching pre-#218 submission order.</summary>
    public CrossPrioritization CrossPrioritization { get; init; } = CrossPrioritization.None;

    /// <summary>When <see cref="CrossType"/> is
    /// <see cref="Matching.CrossType.AgainstBook"/>, the prioritized leg may
    /// sweep up to this many shares against the opposing public book at the
    /// cross price (or better) before the residual prints internally with
    /// the other leg. Zero (default) means "no sweep" — equivalent to
    /// AllOrNone semantics for the residual phase.</summary>
    public long MaxSweepQty { get; init; } = 0;
}

/// <summary>
/// NewOrderCross <c>CrossType</c> values per the EntryPoint SBE schema
/// (b3-entrypoint-messages-8.4.2.xml). Wire byte values are preserved so
/// the decoder can map directly. Issue #218 (Onda L · L5).
/// </summary>
public enum CrossType : byte
{
    /// <summary>Cross prints atomically between the two parties with no
    /// public-book interaction. Wire value 1 — also the in-engine default
    /// when the inbound message omits CrossType (null on the wire).</summary>
    AllOrNone = 1,

    /// <summary>Prioritized leg sweeps the opposing public book up to
    /// <see cref="CrossOrderCommand.MaxSweepQty"/> shares at the cross
    /// price (or better) before the residual prints internally between
    /// the two cross legs. Wire value 4.</summary>
    AgainstBook = 4,

    /// <summary>VWAP cross — auction-tied, deferred. Wire value 7.</summary>
    VwapCross = 7,

    /// <summary>Closing-price cross — auction-tied, deferred. Wire value 8.</summary>
    ClosingPriceCross = 8,
}

/// <summary>
/// NewOrderCross <c>CrossPrioritization</c> values per the EntryPoint SBE
/// schema. Wire byte values preserved. Issue #218 (Onda L · L5).
/// </summary>
public enum CrossPrioritization : byte
{
    /// <summary>No explicit prioritization. The engine treats this as
    /// Buy-prioritized (Buy leg submits first), matching pre-#218
    /// behavior. Wire value 0 — also the in-engine default when the
    /// inbound message omits CrossPrioritization (null on the wire).</summary>
    None = 0,

    /// <summary>Buy leg submits first; sweeps before the Sell leg under
    /// <see cref="CrossType.AgainstBook"/>. Wire value 1.</summary>
    BuyPrioritized = 1,

    /// <summary>Sell leg submits first; sweeps before the Buy leg under
    /// <see cref="CrossType.AgainstBook"/>. Wire value 2.</summary>
    SellPrioritized = 2,
}

/// <summary>
/// Mass-cancel command (OrderMassActionRequest template 701, spec §4.8 /
/// #GAP-19). Carries the wire-level filter as decoded from the inbound
/// frame; the Gateway router resolves the
/// (session, firm, optional Side, optional SecurityId) tuple against the
/// <c>OrderOwnershipMap</c>, groups the resulting orderIds by channel, and
/// forwards a flat list per channel via
/// <c>ChannelDispatcher.EnqueueResolvedMassCancel</c>. A
/// <c>SecurityId</c> of zero (the schema's null sentinel) means "any
/// instrument".
/// </summary>
public sealed record MassCancelCommand(
    long SecurityId,
    Side? SideFilter,
    ulong EnteredAtNanos);
