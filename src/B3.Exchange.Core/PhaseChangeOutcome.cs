using B3.Exchange.Matching;

namespace B3.Exchange.Core;

/// <summary>
/// Issue #321: structured outcome of an operator-driven phase transition
/// (plain <see cref="MatchingEngine.SetTradingPhase"/> or
/// <see cref="MatchingEngine.UncrossAuction"/>) returned to the HTTP
/// admin endpoint via a <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>.
/// </summary>
public readonly record struct PhaseChangeOutcome(
    bool TransitionApplied,
    TradingPhase PreviousPhase,
    TradingPhase CurrentPhase,
    AuctionPrintInfo? UncrossPrint);

/// <summary>
/// Issue #321: snapshot of the auction print produced by an
/// <see cref="MatchingEngine.UncrossAuction"/> call. Mirrors the values
/// captured from <c>AuctionPrintEvent</c> by the dispatcher's
/// <c>OnAuctionPrint</c> sink.
/// </summary>
public readonly record struct AuctionPrintInfo(
    AuctionPrintKind Kind,
    long PriceMantissa,
    long ClearedQuantity);
