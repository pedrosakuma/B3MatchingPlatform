using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using B3.Exchange.Contracts;

namespace B3.Exchange.Gateway;

/// <summary>
/// In-memory map from <see cref="SessionId"/> to the live
/// <see cref="FixpSession"/> that currently terminates that logical session,
/// plus the per-logical-session coordination routes used by outbound writes,
/// lifecycle closure, and higher-version takeover.
///
/// <para>Each logical session has its own <see cref="SessionRoute"/> gate, so
/// unrelated firms never serialize their execution reports behind a global
/// lock. Route handles merge only after a takeover is fully persisted. A
/// delayed callback captured on the victim therefore follows the committed
/// successor but can never follow an unrelated later reuse of the numeric
/// SessionID.</para>
/// </summary>
public sealed class SessionRegistry
{
    private enum IdentityUpdateAttempt
    {
        Updated,
        Retry,
        Rejected,
    }

    private readonly ConcurrentDictionary<SessionId, FixpSession> _sessions = new();
    private readonly object _mapLock = new();
    private readonly ConditionalWeakTable<FixpSession, SessionRoute> _routes = new();

    public int Count => _sessions.Count;

    /// <summary>Inserts the session under its <see cref="FixpSession.Identity"/>;
    /// if an entry already exists for that identity (e.g. duplicate accept
    /// for the same connection id) it is replaced and the previous
    /// session is closed by the caller.</summary>
    public void Register(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var route = RouteFor(session);
        route.Execute(root =>
        {
            var current = root.Current;
            if (current is null
                || ReferenceEquals(current, session)
                || (current.Identity == session.Identity
                    && current.SessionVerId <= session.SessionVerId))
                root.SetCurrent(session);
            lock (_mapLock)
            {
                _sessions[session.Identity] = session;
            }
            return true;
        });
    }

    /// <summary>Removes the session if it is the currently-registered
    /// instance for its identity (no-op otherwise — protects against
    /// late deregistration from a stale session whose identity has been
    /// reclaimed).</summary>
    public void Deregister(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RouteFor(session).Execute(root =>
        {
            lock (_mapLock)
            {
                _sessions.TryRemove(new KeyValuePair<SessionId, FixpSession>(
                    session.Identity, session));
            }
            root.ClearCurrent(session);
            return true;
        });
    }

    /// <summary>
    /// Issue #485: atomically re-indexes a session when its Identity changes
    /// (after successful Negotiate). Removes the session under the old identity
    /// and registers it under the new identity. No-op when identities match.
    /// </summary>
    public void UpdateIdentity(FixpSession session, SessionId oldIdentity, SessionId newIdentity)
        => _ = TryUpdateIdentity(
            session,
            oldIdentity,
            newIdentity,
            claims: null,
            claimedSessionId: 0,
            replaceRetired: true);

    /// <summary>
    /// Re-indexes a newly claimed session while proving that it still owns the
    /// FIXP claim. A terminal predecessor is conditionally replaced and its
    /// stable route is transferred so delayed work follows the successor. A
    /// live predecessor remains mapped until the takeover commit path performs
    /// its persisted state transfer.
    /// </summary>
    internal bool TryUpdateIdentity(
        FixpSession session,
        SessionId oldIdentity,
        SessionId newIdentity,
        SessionClaimRegistry? claims,
        uint claimedSessionId,
        bool replaceRetired)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Identity != newIdentity)
            throw new ArgumentException("session identity must match new identity", nameof(newIdentity));

        if (oldIdentity == newIdentity)
        {
            if (claims is null) return true;
            return claims.TryExecuteIfOwned(claimedSessionId, session, static () => { });
        }

        while (true)
        {
            FixpSession? previous;
            lock (_mapLock)
            {
                _sessions.TryGetValue(newIdentity, out previous);
            }

            IdentityUpdateAttempt attempt;
            if (previous is null
                || ReferenceEquals(previous, session)
                || !replaceRetired)
            {
                attempt = RouteFor(session).Execute(replacementRoute =>
                {
                    if (!ReferenceEquals(replacementRoute.Current, session))
                        return IdentityUpdateAttempt.Rejected;

                    var result = IdentityUpdateAttempt.Rejected;
                    bool owned = ExecuteWithClaim(claims, claimedSessionId, session, () =>
                    {
                        lock (_mapLock)
                        {
                            if (_sessions.TryGetValue(newIdentity, out var current))
                            {
                                if (!ReferenceEquals(current, session))
                                {
                                    result = replaceRetired
                                        ? IdentityUpdateAttempt.Retry
                                        : IdentityUpdateAttempt.Updated;
                                    if (!replaceRetired)
                                    {
                                        _sessions.TryRemove(
                                            new KeyValuePair<SessionId, FixpSession>(
                                                oldIdentity, session));
                                    }
                                    return;
                                }
                            }
                            else if (!_sessions.TryAdd(newIdentity, session))
                            {
                                result = IdentityUpdateAttempt.Retry;
                                return;
                            }

                            _sessions.TryRemove(new KeyValuePair<SessionId, FixpSession>(
                                oldIdentity, session));
                            result = IdentityUpdateAttempt.Updated;
                        }
                    });
                    return owned ? result : IdentityUpdateAttempt.Rejected;
                });
            }
            else
            {
                var previousHandle = RouteFor(previous);
                var replacementHandle = RouteFor(session);
                attempt = SessionRoute.ExecutePair(previousHandle, replacementHandle,
                    (previousRoute, replacementRoute) =>
                {
                    if (!ReferenceEquals(replacementRoute.Current, session))
                        return IdentityUpdateAttempt.Rejected;

                    var previousCurrent = previousRoute.Current;
                    if (previousCurrent is not null
                        && !ReferenceEquals(previousCurrent, previous)
                        && !ReferenceEquals(previousCurrent, session))
                        return IdentityUpdateAttempt.Retry;

                    var result = IdentityUpdateAttempt.Rejected;
                    bool transferRoute = false;
                    bool owned = ExecuteWithClaim(claims, claimedSessionId, session, () =>
                    {
                        lock (_mapLock)
                        {
                            if (_sessions.TryGetValue(newIdentity, out var current))
                            {
                                if (ReferenceEquals(current, previous))
                                {
                                    if (!CanReplace(previous))
                                    {
                                        result = IdentityUpdateAttempt.Rejected;
                                        return;
                                    }
                                    if (!session.TryResetOutboundStateForRetiredReplacement(
                                            previous, claimedSessionId))
                                    {
                                        result = IdentityUpdateAttempt.Rejected;
                                        return;
                                    }
                                    if (!_sessions.TryUpdate(newIdentity, session, previous))
                                    {
                                        result = IdentityUpdateAttempt.Retry;
                                        return;
                                    }
                                    transferRoute = true;
                                }
                                else if (ReferenceEquals(current, session))
                                {
                                    transferRoute = CanReplace(previous);
                                }
                                else
                                {
                                    result = IdentityUpdateAttempt.Retry;
                                    return;
                                }
                            }
                            else
                            {
                                if (!CanReplace(previous))
                                {
                                    result = IdentityUpdateAttempt.Retry;
                                    return;
                                }
                                if (!session.TryResetOutboundStateForRetiredReplacement(
                                        previous, claimedSessionId))
                                {
                                    result = IdentityUpdateAttempt.Rejected;
                                    return;
                                }
                                if (!_sessions.TryAdd(newIdentity, session))
                                {
                                    result = IdentityUpdateAttempt.Retry;
                                    return;
                                }
                                transferRoute = true;
                            }

                            _sessions.TryRemove(new KeyValuePair<SessionId, FixpSession>(
                                oldIdentity, session));
                            result = IdentityUpdateAttempt.Updated;
                        }

                        if (transferRoute)
                        {
                            previousRoute.SetCurrent(session);
                            if (!ReferenceEquals(previousRoute, replacementRoute))
                                replacementRoute.RedirectTo(previousRoute);
                        }
                    });
                    return owned ? result : IdentityUpdateAttempt.Rejected;
                });
            }

            if (attempt == IdentityUpdateAttempt.Retry)
                continue;
            return attempt == IdentityUpdateAttempt.Updated;
        }
    }

    public bool TryGet(SessionId session, out FixpSession value)
        => _sessions.TryGetValue(session, out value!);

    internal bool IsCurrent(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_mapLock)
        {
            return _sessions.TryGetValue(session.Identity, out var current)
                && ReferenceEquals(current, session);
        }
    }

    internal SessionRoute CaptureRoute(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return RouteFor(session);
    }

    internal int PendingWriteCount(FixpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return RouteFor(session).Execute(route => route.PendingWriteCount);
    }

    internal bool TryInvoke(SessionId session, Func<FixpSession, bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        FixpSession current;
        lock (_mapLock)
        {
            if (!_sessions.TryGetValue(session, out current!))
                return false;
        }
        return RouteFor(current).TryInvoke(action);
    }

    internal OrderedStreamWriteResult TryInvokeOrdered(
        SessionId session,
        Func<FixpSession, OrderedStreamWriteResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        FixpSession current;
        lock (_mapLock)
        {
            if (!_sessions.TryGetValue(session, out current!))
                return OrderedStreamWriteResult.NotCommitted;
        }
        return RouteFor(current).TryInvoke(action);
    }

    internal void ExecuteExclusive(FixpSession session, Action action)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(action);
        RouteFor(session).Execute(_ =>
        {
            action();
            return true;
        });
    }

    internal T ExecuteExclusive<T>(FixpSession session, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(action);
        return RouteFor(session).Execute(_ => action());
    }

    internal SessionClaimRegistry.TakeOverFinalizeResult TryCommitTakeOver(
        FixpSession previous,
        FixpSession replacement,
        SessionClaimRegistry claims,
        SessionClaimRegistry.TakeOverRollback rollback)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(claims);

        var previousHandle = RouteFor(previous);
        var replacementHandle = RouteFor(replacement);
        return SessionRoute.ExecutePair(previousHandle, replacementHandle,
            (previousRoute, replacementRoute) =>
        {
            var identity = replacement.Identity;
            var result = claims.TryFinalizeTakeOver(
                replacement.SessionId,
                replacement.SessionVerId,
                replacement,
                previous,
                rollback,
                commit: () =>
                {
                    lock (_mapLock)
                    {
                        if (previous.Identity != replacement.Identity
                            || !_sessions.TryGetValue(identity, out var registered)
                            || !ReferenceEquals(registered, previous)
                            || !replacement.IsLiveTakeOverCandidate)
                            return SessionClaimRegistry.TakeOverCommitDecision.RollBack;
                    }

                    bool freshGeneration = replacement.SessionVerId > previous.SessionVerId;
                    var decision = replacement.TryFinalizeOutboundStateForTakeOver(previous,
                        freshGeneration, () =>
                    {
                        if (!ReferenceEquals(previousRoute.Current, previous)
                            || !ReferenceEquals(replacementRoute.Current, replacement))
                            return SessionClaimRegistry.TakeOverCommitDecision.RollBack;
                        if (!replacement.TrySealTakeOverCandidate())
                            return SessionClaimRegistry.TakeOverCommitDecision.RollBack;

                        if (freshGeneration
                            && !replacement.TryRollOutboundJournalGenerationForFreshTakeOver(
                                replacement.SessionId))
                        {
                            bool rollbackPersisted = previous.TrySaveStateSnapshot();
                            replacement.RollbackTakeOverSeal();
                            return rollbackPersisted
                                ? SessionClaimRegistry.TakeOverCommitDecision.RollBack
                                : SessionClaimRegistry.TakeOverCommitDecision.FailClosed;
                        }

                        if (!replacement.TrySaveStateSnapshot())
                        {
                            bool journalRestored =
                                !freshGeneration
                                || replacement.TryRestoreRolledOutboundJournalGenerationForTakeOver(
                                    replacement.SessionId);
                            bool rollbackPersisted = journalRestored && previous.TrySaveStateSnapshot();
                            replacement.RollbackTakeOverSeal();
                            return rollbackPersisted
                                ? SessionClaimRegistry.TakeOverCommitDecision.RollBack
                                : SessionClaimRegistry.TakeOverCommitDecision.FailClosed;
                        }

                        previousRoute.SetCurrent(replacement);
                        if (!ReferenceEquals(previousRoute, replacementRoute))
                            replacementRoute.RedirectTo(previousRoute);
                        lock (_mapLock)
                        {
                            _sessions[identity] = replacement;
                        }
                        return SessionClaimRegistry.TakeOverCommitDecision.Commit;
                    });
                    return decision;
                });

            if (result == SessionClaimRegistry.TakeOverFinalizeResult.Committed)
            {
                previous.Close(
                    "session-takeover:evicted-by-newer-verId",
                    CloseKind.SessionTakeOver);
            }
            else if (result == SessionClaimRegistry.TakeOverFinalizeResult.FailedClosed)
            {
                previousRoute.ClearCurrent(previous);
                previousRoute.ClearCurrent(replacement);
                replacementRoute.ClearCurrent(previous);
                replacementRoute.ClearCurrent(replacement);
                lock (_mapLock)
                {
                    _sessions.TryRemove(
                        new KeyValuePair<SessionId, FixpSession>(identity, previous));
                    _sessions.TryRemove(
                        new KeyValuePair<SessionId, FixpSession>(identity, replacement));
                }
                previous.Close(
                    "session-takeover:durable-rollback-failed",
                    CloseKind.SessionTakeOver);
            }
            return result;
        });
    }

    private SessionRoute RouteFor(FixpSession session)
        => _routes.GetValue(session, static current => new SessionRoute(current));

    private static bool CanReplace(FixpSession session)
        => session.State == FixpState.Terminated || !session.IsRegistered;

    private static bool ExecuteWithClaim(
        SessionClaimRegistry? claims,
        uint claimedSessionId,
        FixpSession session,
        Action action)
    {
        if (claims is null)
        {
            action();
            return true;
        }
        return claims.TryExecuteIfOwned(claimedSessionId, session, action);
    }
}
