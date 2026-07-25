using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    private readonly object _routeLock = new();
    private readonly ConditionalWeakTable<FixpSession, SessionRoute> _routes = new();

    public int Count => _sessions.Count;

    /// <summary>Inserts the session under its <see cref="FixpSession.Identity"/>;
    /// if an entry already exists for that identity (e.g. duplicate accept
    /// for the same connection id) it is replaced and the previous
    /// session is closed by the caller.</summary>
    public void Register(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_routeLock)
        {
            var route = _routes
                .GetValue(session, static current => new SessionRoute(current))
                .Resolve();
            var current = route.Current;
            if (current is null
                || ReferenceEquals(current, session)
                || (current.Identity == session.Identity
                    && current.SessionVerId <= session.SessionVerId))
                route.SetCurrent(session);
        }
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
        lock (_routeLock)
        {
            if (_routes.TryGetValue(session, out var route))
                route.Clear(session);
        }
    }

    /// <summary>
    /// Issue #485: atomically re-indexes a session when its Identity changes
    /// (after successful Negotiate). Removes the session under the old identity
    /// and registers it under the new identity. No-op when identities match.
    /// </summary>
    public void UpdateIdentity(FixpSession session, SessionId oldIdentity, SessionId newIdentity)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Avoid unnecessary churn when identity hasn't changed (e.g., rehydrated sessions)
        if (oldIdentity == newIdentity) return;
        // Publish new key BEFORE removing old key to minimize routing miss window
        _sessions[newIdentity] = session;
        // Remove old key only if it still points to this session
        _sessions.TryRemove(new KeyValuePair<SessionId, FixpSession>(oldIdentity, session));
    }

    public bool TryGet(SessionId session, out FixpSession value)
        => _sessions.TryGetValue(session, out value!);

    internal SessionRoute CaptureRoute(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _routes.GetValue(session, static current => new SessionRoute(current));
    }

    internal bool TransferTakeOver(FixpSession previous, FixpSession replacement)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_routeLock)
        {
            if (previous.Identity != replacement.Identity)
                return false;

            var previousRoute = _routes
                .GetValue(previous, static current => new SessionRoute(current))
                .Resolve();
            var replacementRoute = _routes
                .GetValue(replacement, static current => new SessionRoute(current))
                .Resolve();
            var target = replacement;

            if (!TrySelectCurrent(previousRoute.Current, replacement.Identity, ref target)
                || !TrySelectCurrent(replacementRoute.Current, replacement.Identity, ref target))
                return false;

            previousRoute.SetCurrent(target);
            if (!ReferenceEquals(previousRoute, replacementRoute))
                replacementRoute.RedirectTo(previousRoute);
            return true;
        }
    }

    private static bool TrySelectCurrent(
        FixpSession? candidate,
        SessionId identity,
        ref FixpSession target)
    {
        if (candidate is null) return true;
        if (candidate.Identity != identity) return false;
        if (candidate.SessionVerId > target.SessionVerId)
            target = candidate;
        return true;
    }
}
