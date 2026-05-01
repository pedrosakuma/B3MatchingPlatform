namespace B3.Exchange.Contracts;

/// <summary>
/// Core → Gateway: order-level reject (e.g. unknown instrument,
/// invalid price). The Gateway translates this into an
/// <c>ExecutionReport(8)</c> with <c>ExecType=Rejected</c> on the wire.
/// </summary>
public sealed record RejectEvent(
    SessionId Session,
    string ClOrdID,
    OrdRejReason Reason,
    string? Text,
    long EmittedAtNs);
