using System.Collections.Concurrent;
using B3.Exchange.Contracts;

namespace B3.Exchange.Gateway;

/// <summary>
/// In-memory map from <see cref="SessionId"/> to the live
/// <see cref="FixpSession"/> that currently terminates that session.
///
/// <para>Phase 1 stub: today there is exactly one TCP transport per
/// session (entries appear on accept, vanish on disconnect). Phase 2
/// (FIXP Establish + reattach) and Phase 3 (Suspended sessions backed by
/// retransmission rings) will replace the value type with a richer
/// <c>FixpSessionHandle</c> that survives transport churn.</para>
///
/// <para>Thread-safety: register / deregister can race with
/// <see cref="TryGet"/> calls coming from any
/// <see cref="ChannelDispatcher"/> dispatch thread. Backed by
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> for that reason.</para>
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<SessionId, FixpSession> _sessions = new();

    public int Count => _sessions.Count;

    /// <summary>Inserts the session under its <see cref="FixpSession.Identity"/>;
    /// if an entry already exists for that identity (e.g. duplicate accept
    /// for the same connection id) it is replaced and the previous
    /// session is closed by the caller.</summary>
    public void Register(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.Identity] = session;
    }

    /// <summary>Removes the session if it is the currently-registered
    /// instance for its identity (no-op otherwise — protects against
    /// late deregistration from a stale session whose identity has been
    /// reclaimed).</summary>
    public void Deregister(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions.TryRemove(new KeyValuePair<SessionId, FixpSession>(session.Identity, session));
    }

    public bool TryGet(SessionId session, out FixpSession value)
        => _sessions.TryGetValue(session, out value!);
}
