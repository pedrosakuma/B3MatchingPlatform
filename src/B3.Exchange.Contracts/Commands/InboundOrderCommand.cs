namespace B3.Exchange.Contracts;

/// <summary>
/// Gateway → Core: a new-order request decoded from the wire and
/// authenticated against a session.
///
/// <para><see cref="Session"/> is the routing key Core stamps onto every
/// outbound event for this order. <see cref="Firm"/> is carried for
/// audit / risk and is set by the Gateway from the session's
/// authenticated firm registration — the wire payload alone does NOT
/// determine the firm.</para>
///
/// <para><see cref="PriceMantissa"/> is the implicit /10000 mantissa
/// used throughout the simulator (a price of 100.05 is encoded as
/// <c>1_000_500</c>).</para>
/// </summary>
public sealed record InboundOrderCommand(
    SessionId Session,
    FirmId Firm,
    string ClOrdID,
    long SecurityId,
    Side Side,
    OrderType Type,
    long PriceMantissa,
    long Quantity,
    TimeInForce Tif,
    long ReceivedAtNs);
