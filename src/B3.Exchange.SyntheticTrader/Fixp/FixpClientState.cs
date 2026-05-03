namespace B3.Exchange.SyntheticTrader.Fixp;

/// <summary>
/// FIXP client-side state machine, mirroring the server-side
/// <c>FixpStateMachine</c> in <c>B3.Exchange.Gateway</c>.
///
/// Transitions (happy path):
///   <see cref="Init"/> → <see cref="NegotiateSent"/> → <see cref="Negotiated"/>
///     → <see cref="EstablishSent"/> → <see cref="Established"/>
///     → <see cref="Terminating"/> → <see cref="Closed"/>.
/// Any reject moves the session to <see cref="Failed"/>.
/// </summary>
public enum FixpClientState
{
    Init = 0,
    NegotiateSent,
    Negotiated,
    EstablishSent,
    Established,
    Terminating,
    Closed,
    Failed,
}
