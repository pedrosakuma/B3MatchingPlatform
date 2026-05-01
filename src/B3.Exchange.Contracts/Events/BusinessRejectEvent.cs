namespace B3.Exchange.Contracts;

/// <summary>
/// Core → Gateway: business-level reject (BMR / FIX 35=j). Used when the
/// inbound message is parseable but cannot be processed by Core (e.g.
/// session not authorised to trade an instrument). Distinct from
/// <see cref="RejectEvent"/>, which is order-flow specific.
///
/// <para><b>Stub</b>: the spec field set is tracked under issue #50
/// (GAP-14). Today only the minimum needed to route the message back to
/// the originating session is captured; <see cref="RefSeqNum"/> /
/// <see cref="RefMsgType"/> / business reject reason will be added when
/// #50 lands.</para>
/// </summary>
public sealed record BusinessRejectEvent(
    SessionId Session,
    string? RefBusinessRejectRefId,
    string Text,
    long EmittedAtNs);
