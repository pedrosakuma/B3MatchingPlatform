namespace B3.Exchange.Gateway;

using B3.Exchange.Contracts;

/// <summary>
/// Stable logical-session route captured by asynchronous gateway work.
/// A committed session-version takeover transfers the route to the
/// replacement session; a normal close clears it without exposing a
/// later unrelated reuse of the same numeric SessionID.
///
/// <para>Business writes that arrive while Establish admission is closed are
/// accepted into a bounded per-route FIFO. A background continuation drains
/// that FIFO only after EstablishAck opens admission, so channel dispatcher
/// threads never wait on peer handshake progress.</para>
/// </summary>
internal sealed class SessionRoute
{
    private abstract class DeferredWrite
    {
        public abstract bool TryExecute(FixpSession target);
        public abstract void Fail();
    }

    private sealed class DeferredBooleanWrite(
        Func<FixpSession, bool> action) : DeferredWrite
    {
        public override bool TryExecute(FixpSession target)
        {
            bool result = action(target);
            return result || target.BusinessAdmissionState != 0;
        }

        public override void Fail() { }
    }

    private sealed class DeferredOrderedWrite(
        Func<FixpSession, OrderedStreamWriteResult> action,
        TaskCompletionSource<OrderedStreamWriteResult> completion) : DeferredWrite
    {
        public Task<OrderedStreamWriteResult> Completion => completion.Task;

        public override bool TryExecute(FixpSession target)
        {
            OrderedStreamWriteResult result;
            try
            {
                result = action(target);
            }
            catch
            {
                completion.TrySetResult(OrderedStreamWriteResult.NotCommitted);
                return true;
            }

            if (!result.IsAccepted && target.BusinessAdmissionState == 0)
                return false;

            completion.TrySetResult(result);
            return true;
        }

        public override void Fail() =>
            completion.TrySetResult(OrderedStreamWriteResult.NotCommitted);
    }

    private static long _nextId;
    private FixpSession? _current;
    private SessionRoute? _redirect;
    private readonly object _gate = new();
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly LinkedList<DeferredWrite> _pending = new();
    private Task<bool>? _observedAdmission;
    private int _drainScheduled;
    internal Action? AfterDeferredWriteForTesting { get; set; }

    public SessionRoute(FixpSession initial)
    {
        _current = initial;
    }

    public OrderedStreamWriteResult TryInvoke(
        Func<FixpSession, OrderedStreamWriteResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Execute(route => route.TryInvokeOrderedLocked(action));
    }

    internal bool TryInvoke(Func<FixpSession, bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Execute(route => route.TryInvokeBooleanLocked(action));
    }

    private OrderedStreamWriteResult TryInvokeOrderedLocked(
        Func<FixpSession, OrderedStreamWriteResult> action)
    {
        var target = Current;
        if (target is null || target.BusinessAdmissionState == 2)
            return OrderedStreamWriteResult.NotCommitted;

        if (target.BusinessAdmissionState == 0
            || _pending.Count != 0
            || _drainScheduled != 0)
            return EnqueueOrderedLocked(target, action);

        var result = action(target);
        if (!result.IsAccepted && target.BusinessAdmissionState == 0)
            return EnqueueOrderedLocked(target, action);
        return result;
    }

    private bool TryInvokeBooleanLocked(Func<FixpSession, bool> action)
    {
        var target = Current;
        if (target is null || target.BusinessAdmissionState == 2)
            return false;

        if (target.BusinessAdmissionState == 0
            || _pending.Count != 0
            || _drainScheduled != 0)
            return EnqueueBooleanLocked(target, action);

        bool result = action(target);
        if (!result && target.BusinessAdmissionState == 0)
            return EnqueueBooleanLocked(target, action);
        return result;
    }

    private OrderedStreamWriteResult EnqueueOrderedLocked(
        FixpSession target,
        Func<FixpSession, OrderedStreamWriteResult> action)
    {
        if (_pending.Count >= target.DeferredBusinessWriteCapacity)
            return OrderedStreamWriteResult.NotCommitted;

        var completion = new TaskCompletionSource<OrderedStreamWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new DeferredOrderedWrite(action, completion);
        _pending.AddLast(pending);
        ObserveAdmissionLocked(target);
        return OrderedStreamWriteResult.Deferred(pending.Completion);
    }

    private bool EnqueueBooleanLocked(
        FixpSession target,
        Func<FixpSession, bool> action)
    {
        if (_pending.Count >= target.DeferredBusinessWriteCapacity)
            return false;

        _pending.AddLast(new DeferredBooleanWrite(action));
        ObserveAdmissionLocked(target);
        return true;
    }

    private void ObserveAdmissionLocked(FixpSession target)
    {
        var admission = target.BusinessAdmissionCompletion;
        if (ReferenceEquals(_observedAdmission, admission))
            return;

        _observedAdmission = admission;
        _ = admission.ContinueWith(
            static (_, state) => ((SessionRoute)state!).ScheduleDrain(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ScheduleDrain()
    {
        Execute(route =>
        {
            route.ScheduleDrainLocked();
            return true;
        });
    }

    private void ScheduleDrainLocked()
    {
        if (_pending.Count == 0 || _drainScheduled != 0)
            return;

        var target = Current;
        if (target is null || target.BusinessAdmissionState == 2)
        {
            FailPendingLocked();
            return;
        }
        if (target.BusinessAdmissionState == 0)
        {
            ObserveAdmissionLocked(target);
            return;
        }

        _drainScheduled = 1;
        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            bool more = Execute(route => route.DrainOneLocked());
            if (!more)
                return;
            Resolve().AfterDeferredWriteForTesting?.Invoke();
        }
    }

    private bool DrainOneLocked()
    {
        if (_pending.First is not { } node)
        {
            _drainScheduled = 0;
            return false;
        }

        var target = Current;
        if (target is null || target.BusinessAdmissionState == 2)
        {
            FailPendingLocked();
            _drainScheduled = 0;
            return false;
        }
        if (target.BusinessAdmissionState == 0)
        {
            _drainScheduled = 0;
            ObserveAdmissionLocked(target);
            return false;
        }

        _pending.RemoveFirst();
        bool completed;
        try
        {
            completed = node.Value.TryExecute(target);
        }
        catch
        {
            node.Value.Fail();
            completed = true;
        }

        if (!completed)
        {
            _pending.AddFirst(node);
            _drainScheduled = 0;
            ObserveAdmissionLocked(target);
            return false;
        }
        return true;
    }

    internal FixpSession? Current => Volatile.Read(ref _current);

    internal int PendingWriteCount => _pending.Count;

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
    {
        Volatile.Write(ref _current, current);
        ScheduleDrainLocked();
    }

    internal void ClearCurrent(FixpSession expected)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(
                ref _current, null, expected), expected))
        {
            FailPendingLocked();
        }
    }

    internal void RedirectTo(SessionRoute route)
    {
        while (_pending.First is { } node)
        {
            _pending.RemoveFirst();
            if (route.Current is { } target
                && route._pending.Count < target.DeferredBusinessWriteCapacity)
            {
                route._pending.AddLast(node);
            }
            else
            {
                node.Value.Fail();
            }
        }
        _drainScheduled = 0;
        Volatile.Write(ref _redirect, route);
        route.ScheduleDrainLocked();
    }

    public void Clear(FixpSession expected)
        => Execute(route =>
        {
            route.ClearCurrent(expected);
            return true;
        });

    private void FailPendingLocked()
    {
        while (_pending.First is { } node)
        {
            _pending.RemoveFirst();
            node.Value.Fail();
        }
    }

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
