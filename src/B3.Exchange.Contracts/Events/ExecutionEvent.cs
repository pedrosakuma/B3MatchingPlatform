namespace B3.Exchange.Contracts;

/// <summary>
/// Core → Gateway: an execution lifecycle event for an order
/// (acknowledgement, fill, partial fill, cancel, replace).
///
/// <para><see cref="Session"/> is the originator's <see cref="SessionId"/>
/// — the Gateway uses it to find the live <c>FixpSession</c> (or the
/// retransmission ring of a Suspended one). Core does NOT track which
/// transport produced the order; it simply echoes back the
/// <see cref="SessionId"/> stamped on the inbound command.</para>
///
/// <para><see cref="LastPx"/> is in the same /10000 mantissa as
/// <see cref="InboundOrderCommand.PriceMantissa"/>.</para>
/// </summary>
public sealed record ExecutionEvent(
    SessionId Session,
    long OrderId,
    long TradeId,
    string ClOrdID,
    ExecType ExecType,
    long LastQty,
    long LastPx,
    long LeavesQty,
    long CumQty,
    long EmittedAtNs);
