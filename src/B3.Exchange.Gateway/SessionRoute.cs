namespace B3.Exchange.Gateway;

using B3.Exchange.Contracts;

/// <summary>
/// Stable logical-session route captured by asynchronous gateway work.
/// A committed session-version takeover transfers the route to the
/// replacement session; a normal close clears it without exposing a
/// later unrelated reuse of the same numeric SessionID.
/// </summary>
internal sealed class SessionRoute
{
    private static long _nextId;
    private FixpSession? _current;
    private SessionRoute? _redirect;
    private readonly object _gate = new();
    private readonly long _id = Interlocked.Increment(ref _nextId);

    public SessionRoute(FixpSession initial)
    {
        _current = initial;
    }

    public OrderedStreamWriteResult TryInvoke(
        Func<FixpSession, OrderedStreamWriteResult> action)
        => TryInvokeCore(action, OrderedStreamWriteResult.NotCommitted);

    internal bool TryInvoke(Func<FixpSession, bool> action)
        => TryInvokeCore(action, false);

    private T TryInvokeCore<T>(Func<FixpSession, T> action, T unavailable)
    {
        ArgumentNullException.ThrowIfNull(action);
        while (true)
        {
            var target = Execute(route => route.Current);
            if (target is null)
                return unavailable;

            if (!target.WaitForBusinessAdmission())
            {
                if (Execute(route => !ReferenceEquals(route.Current, target)))
                    continue;
                return unavailable;
            }

            bool routeChanged = false;
            var result = Execute(route =>
            {
                if (!ReferenceEquals(route.Current, target))
                {
                    routeChanged = true;
                    return unavailable;
                }
                return action(target);
            });
            if (!routeChanged)
                return result;
        }
    }

    internal FixpSession? Current => Volatile.Read(ref _current);

    internal SessionRoute Resolve()
    {
        var route = this;
        while (Volatile.Read(ref route._redirect) is { } redirect)
        {
            route = redirect;
        }
        return route;
    }

    internal void SetCurrent(FixpSession current)
        => Volatile.Write(ref _current, current);

    internal void ClearCurrent(FixpSession expected)
        => Interlocked.CompareExchange(ref _current, null, expected);

    internal void RedirectTo(SessionRoute route)
        => Volatile.Write(ref _redirect, route);

    public void Clear(FixpSession expected)
        => Execute(route =>
        {
            route.ClearCurrent(expected);
            return true;
        });

    internal T Execute<T>(Func<SessionRoute, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        while (true)
        {
            var route = Resolve();
            lock (route._gate)
            {
                if (!ReferenceEquals(route, Resolve()))
                    continue;
                return action(route);
            }
        }
    }

    internal static T ExecutePair<T>(
        SessionRoute first,
        SessionRoute second,
        Func<SessionRoute, SessionRoute, T> action)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(action);

        while (true)
        {
            var firstRoot = first.Resolve();
            var secondRoot = second.Resolve();
            if (ReferenceEquals(firstRoot, secondRoot))
                return firstRoot.Execute(root => action(root, root));

            var lower = firstRoot._id < secondRoot._id ? firstRoot : secondRoot;
            var upper = ReferenceEquals(lower, firstRoot) ? secondRoot : firstRoot;
            lock (lower._gate)
            {
                lock (upper._gate)
                {
                    if (!ReferenceEquals(firstRoot, first.Resolve())
                        || !ReferenceEquals(secondRoot, second.Resolve()))
                        continue;
                    return action(firstRoot, secondRoot);
                }
            }
        }
    }
}
