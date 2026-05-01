namespace B3.Exchange.Contracts;

/// <summary>
/// Stable, transport-neutral identifier of a logical FIXP session.
///
/// Issued by the Gateway after authentication and used as the only routing
/// key Core knows about: outbound events are stamped with
/// <see cref="SessionId"/>, and the Gateway resolves the live transport
/// (or buffers into a <c>Suspended</c> session's retx ring) at delivery
/// time. Core never holds a reference to a transport, socket, or
/// <c>FixpSession</c> instance.
///
/// Stored as <see cref="string"/> so the assembly stays serialisable
/// without taking dependencies on numeric session-id schemes.
/// </summary>
public readonly record struct SessionId(string Value)
{
    public override string ToString() => Value;
}
