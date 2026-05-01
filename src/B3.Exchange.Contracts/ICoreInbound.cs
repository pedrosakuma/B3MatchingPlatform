namespace B3.Exchange.Contracts;

/// <summary>
/// Inbound surface exposed by Core to the Gateway. Implementations are
/// expected to be <b>thread-safe at the <see cref="SessionId"/> grain</b>
/// (today: routed to a single dispatch thread per channel) and to
/// return immediately after enqueueing — the heavy work happens on the
/// Core's dispatch thread.
///
/// <para>Designed so Core and Gateway can be split into separate
/// processes later: every parameter is a value object on
/// <c>B3.Exchange.Contracts</c>.</para>
/// </summary>
public interface ICoreInbound
{
    void Submit(InboundOrderCommand command);

    void Submit(InboundCancelCommand command);

    void Submit(InboundModifyCommand command);
}
