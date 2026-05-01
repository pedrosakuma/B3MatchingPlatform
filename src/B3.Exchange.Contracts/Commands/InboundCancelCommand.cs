namespace B3.Exchange.Contracts;

/// <summary>
/// Gateway → Core: cancel an existing order.
///
/// Either <see cref="OrigClOrdID"/> or <see cref="ExplicitOrderId"/>
/// must be set; if both are present the Core uses the explicit
/// <see cref="ExplicitOrderId"/>.
/// </summary>
public sealed record InboundCancelCommand(
    SessionId Session,
    FirmId Firm,
    string ClOrdID,
    string? OrigClOrdID,
    long? ExplicitOrderId,
    long SecurityId,
    long ReceivedAtNs);
