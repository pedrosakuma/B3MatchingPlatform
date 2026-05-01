namespace B3.Exchange.Contracts;

/// <summary>
/// Inbound surface exposed by the Gateway to Core. Core calls these
/// methods on its own dispatch thread when an order lifecycle event is
/// emitted; the Gateway is responsible for resolving
/// <see cref="ExecutionEvent.Session"/> to the live transport (or to a
/// retransmission ring for a Suspended session) and for translating the
/// event into the FIXP wire form.
///
/// <para>Implementations must not block the calling thread on I/O;
/// long-running work should be enqueued on a per-session writer.</para>
/// </summary>
public interface IGatewayInbound
{
    void Deliver(ExecutionEvent evt);

    void Deliver(RejectEvent evt);

    void Deliver(BusinessRejectEvent evt);
}
