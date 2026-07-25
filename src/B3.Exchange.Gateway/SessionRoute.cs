namespace B3.Exchange.Gateway;

/// <summary>
/// Stable logical-session route captured by asynchronous gateway work.
/// A committed session-version takeover transfers the route to the
/// replacement session; a normal close clears it without exposing a
/// later unrelated reuse of the same numeric SessionID.
/// </summary>
internal sealed class SessionRoute
{
    private FixpSession? _current;
    private SessionRoute? _redirect;

    public SessionRoute(FixpSession initial)
    {
        _current = initial;
    }

    public bool TryInvoke(Func<FixpSession, bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var current = Resolve().Current;
        if (current is null) return false;
        if (action(current)) return true;

        // A takeover can linearize between route resolution and enqueue.
        // Retry only when the route moved to a different logical successor;
        // never fall through to an unrelated later session after Clear().
        var replacement = Resolve().Current;
        return replacement is not null
            && !ReferenceEquals(replacement, current)
            && action(replacement);
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

    internal void RedirectTo(SessionRoute route)
        => Volatile.Write(ref _redirect, route);

    public void Clear(FixpSession expected)
    {
        var route = Resolve();
        Interlocked.CompareExchange(ref route._current, null, expected);
    }
}
