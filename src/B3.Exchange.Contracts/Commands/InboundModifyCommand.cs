namespace B3.Exchange.Contracts;

/// <summary>
/// Gateway → Core: amend ("replace") an existing order. Same lookup
/// rules as <see cref="InboundCancelCommand"/> for resolving the live
/// order.
/// </summary>
public sealed record InboundModifyCommand(
    SessionId Session,
    FirmId Firm,
    string ClOrdID,
    string? OrigClOrdID,
    long? ExplicitOrderId,
    long SecurityId,
    long NewPriceMantissa,
    long NewQuantity,
    long ReceivedAtNs);
