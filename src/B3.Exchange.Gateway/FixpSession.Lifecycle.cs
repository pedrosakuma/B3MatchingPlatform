using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;

/// <summary>
/// FixpSession lifecycle / attachment management: Close / Suspend /
/// TryReattach / suspended-session reap / DisposeAsync. Split out
/// from FixpSession.cs (issue #139). All lifecycle transitions hold
/// the _attachLock declared in FixpSession.cs and route state
/// changes through ApplyTransition; this file has no fields of its
/// own.
/// </summary>
public sealed partial class FixpSession
{
    public void Close() => Close("close");

    private void OnTransportClosed(string reason) => OnTransportClosed(reason, originatingTransport: null);

    /// <summary>
    /// Routes a transport-side teardown into either a session-preserving
    /// <see cref="Suspend"/> (when we are <see cref="FixpState.Established"/>
    /// per spec §4.5 / issue #69) or a full <see cref="Close"/>. Idempotent
    /// — multiple callers (transport callback, receive-loop finally, watchdog)
    /// converge here without repeating side effects.
    ///
    /// <para><paramref name="originatingTransport"/> identifies the transport
    /// instance whose teardown triggered this call. After a re-attach
    /// (#69b-2) the OLD transport's recv-loop / send-loop / onClose
    /// callbacks may still be in flight; if those fire after the new
    /// transport is installed, they would otherwise erroneously close or
    /// suspend the freshly attached session. We compare against the
    /// session's current <c>_transport</c> snapshot and drop the stale
    /// callback. <c>null</c> means "from an unknown source"; treated as
    /// authoritative for backward compatibility.</para>
    /// </summary>
    private void OnTransportClosed(string reason, TcpTransport? originatingTransport)
    {
        if (Volatile.Read(ref _isOpen) == 0) return;
        // Decision must run under _attachLock so the generation check and
        // the actual lifecycle transition can't be split by a concurrent
        // TryReattach. Without the lock a stale callback could pass the
        // generation check, get preempted while the new transport is
        // installed, then close the new generation.
        lock (_attachLock)
        {
            if (Volatile.Read(ref _isOpen) == 0) return;
            // Stale callback from a prior transport generation — ignore.
            if (originatingTransport is not null && !ReferenceEquals(originatingTransport, _transport))
                return;
            // Re-entrancy guard: Suspend() itself calls _transport.Close(...),
            // which will fire this callback a second time. Once we have detached
            // (or are about to detach), defer to whichever branch already took
            // ownership rather than escalating to a full Close.
            if (Volatile.Read(ref _isAttached) == 0) return;
            if (State == FixpState.Established)
            {
                SuspendLocked(reason);
                return;
            }
            CloseLocked(reason);
        }
    }

    /// <summary>
    /// Demote an <see cref="FixpState.Established"/> session to
    /// <see cref="FixpState.Suspended"/> after the underlying TCP transport
    /// has dropped (spec §4.5, issue #69). The session remains registered in
    /// the <see cref="SessionRegistry"/> and retains its claim so that a
    /// subsequent <c>Establish</c> on a brand-new transport (issue #69b) can
    /// re-attach without re-Negotiate. The dispatch / watchdog loops are
    /// stopped via <c>_cts.Cancel()</c>; a future re-attach will start fresh
    /// loops bound to the new transport.
    /// </summary>
    public void Suspend(string reason)
    {
        lock (_attachLock)
        {
            SuspendLocked(reason);
        }
    }

    /// <summary>
    /// Body of <see cref="Suspend(string)"/>; assumes the caller already
    /// holds <see cref="_attachLock"/>. Idempotent — second caller is a
    /// no-op via the <see cref="_isAttached"/> CAS.
    /// </summary>
    private void SuspendLocked(string reason)
    {
        // Atomically flip the attachment flag; second caller is a no-op.
        if (Interlocked.Exchange(ref _isAttached, 0) == 0) return;
        _logger.LogInformation("fixp session {ConnectionId} suspending (reason={Reason})",
            ConnectionId, reason);
        var action = ApplyTransition(FixpEvent.Detach);
        if (action != FixpAction.Accept || State != FixpState.Suspended)
        {
            _logger.LogInformation(
                "fixp session {ConnectionId} suspend declined (state={State} action={Action}); closing instead",
                ConnectionId, State, action);
            CloseLocked(reason);
            return;
        }
        try { _cts.Cancel(); } catch { }
        try { _transport.Close(reason); } catch { }
        try { _transport.Stream.Dispose(); } catch { }
        Volatile.Write(ref _suspendedSinceMs, NowMs());
        ScheduleCodTimerLocked();
    }

    /// <summary>
    /// Closes the session with a diagnostic reason. The reason is forwarded to
    /// the optional <c>onClosed</c> callback supplied at construction so the
    /// listener (or tests) can log it. <see cref="Close()"/> is the
    /// no-reason convenience overload.
    /// </summary>
    /// <summary>
    /// Generation-aware close: closes the session only if
    /// <paramref name="originatingTransport"/> is still the current
    /// transport. Used by background loops (watchdog) that may have
    /// raced past their cancellation token after a re-attach (#69b-2).
    /// </summary>
    private void CloseIfTransportCurrent(string reason, TcpTransport originatingTransport)
    {
        lock (_attachLock)
        {
            if (!ReferenceEquals(_transport, originatingTransport)) return;
            CloseLocked(reason);
        }
    }

    public void Close(string reason)
    {
        lock (_attachLock)
        {
            CloseLocked(reason);
        }
    }

    /// <summary>
    /// Body of <see cref="Close(string)"/>; assumes the caller already
    /// holds <see cref="_attachLock"/>. Called directly from
    /// <see cref="Suspend"/> when the state-machine refuses to demote
    /// (so we don't try to re-acquire the lock recursively, and so the
    /// transition from "trying to suspend" to "actually closing" is
    /// atomic from the perspective of a concurrent re-attach).
    /// </summary>
    private void CloseLocked(string reason)
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return;
        Volatile.Write(ref _isAttached, 0);
        // Clear the suspended-since timestamp so a concurrent reaper poll
        // doesn't try to re-Close a session that's already terminal.
        Volatile.Write(ref _suspendedSinceMs, 0);
        StopCodTimerLocked();
        _logger.LogInformation("fixp session {ConnectionId} closing", ConnectionId);
        try { _cts.Cancel(); } catch { }
        // The transport may have already closed (it's the source of the
        // callback in IO-error paths). Either way, ensure it's down so the
        // send loop wakes up and exits.
        _transport.Close(reason);
        // Release the FIXP session claim (if any) so the same sessionID
        // can be re-negotiated by a future transport.
        if (_claimedSessionId != 0 && _claims is not null)
        {
            try { _claims.Release(_claimedSessionId, this); } catch { }
            _claimedSessionId = 0;
        }
        // Notify the engine sink so it releases any cached references to this
        // session (see IInboundCommandSink.OnSessionClosed). Without this the
        // ChannelDispatcher's order-owners map keeps the session rooted for
        // the lifetime of every resting order it placed → unbounded memory.
        try { _sink.OnSessionClosed(Identity); } catch { }
        try { _onClosed?.Invoke(this, reason); } catch { }
    }

    /// <summary>
    /// Re-attach a freshly accepted transport to this Suspended FIXP
    /// session (issue #69b-2). Called by <see cref="EntryPointListener"/>
    /// after it has parsed the new connection's first frame and
    /// determined that it is an <c>Establish</c> targeting our
    /// <see cref="SessionId"/> with a matching <see cref="SessionVerId"/>.
    /// The buffered first frame must be replayable through
    /// <paramref name="rebindStream"/> (e.g. a <see cref="PrependedStream"/>);
    /// the new receive loop will read it, drive
    /// <see cref="ProcessEstablish"/>, send the <c>EstablishAck</c> and
    /// transition the state machine Suspended → Established.
    ///
    /// <para>Returns <c>false</c> (without disposing
    /// <paramref name="rebindStream"/>) when the session is no longer
    /// re-attachable (closed, never suspended, or another re-attach won
    /// the race). The caller is responsible for closing the new socket
    /// in that case.</para>
    /// </summary>
    public bool TryReattach(Stream rebindStream)
    {
        ArgumentNullException.ThrowIfNull(rebindStream);
        // Snapshot the previous loop tasks so we can JOIN with them
        // before installing the new transport. This guarantees that any
        // in-flight dispatch from the old receive loop (which still
        // reads `_transport`) has fully run to completion before a new
        // generation can be observed — eliminating the race in which an
        // old dispatch path's continuation would close or write to the
        // freshly attached transport.
        Task? oldRecv;
        Task? oldWatchdog;
        lock (_attachLock)
        {
            if (Volatile.Read(ref _isOpen) == 0) return false;
            if (Volatile.Read(ref _isAttached) == 1) return false;
            if (State != FixpState.Suspended) return false;
            oldRecv = _recvTask;
            oldWatchdog = _watchdogTask;
        }
        // Drain old loops outside the lock to avoid holding _attachLock
        // across an await of arbitrary duration. Loops were already
        // cancelled (and the old stream disposed) in Suspend, so this
        // join should complete promptly. Bound by a short timeout as a
        // belt-and-suspenders against runaway tasks; on timeout we skip
        // the attempt and let the caller close the new socket — the
        // suspended-session reaper will eventually clean up.
        if (oldRecv is not null)
        {
            try { if (!oldRecv.Wait(TimeSpan.FromSeconds(2))) return false; } catch { }
        }
        if (oldWatchdog is not null)
        {
            try { if (!oldWatchdog.Wait(TimeSpan.FromSeconds(2))) return false; } catch { }
        }
        lock (_attachLock)
        {
            // Re-validate after the unlock-await-relock window: a
            // concurrent Close (e.g. reaper) may have terminated the
            // session while we waited.
            if (Volatile.Read(ref _isOpen) == 0) return false;
            if (Volatile.Read(ref _isAttached) == 1) return false;
            if (State != FixpState.Suspended) return false;

            _logger.LogInformation(
                "fixp session {ConnectionId} re-attaching transport (sessionId={SessionId} sessionVerId={SessionVerId})",
                ConnectionId, SessionId, SessionVerId);

            try { _cts.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
            _transport = CreateBoundTransport(rebindStream);
            // The new transport is back; cancel-on-disconnect grace is
            // satisfied (issue #54 spec §4.7: reconnect inside the
            // window cancels the pending CoD trigger).
            StopCodTimerLocked();
            Volatile.Write(ref _suspendedSinceMs, 0);
            Volatile.Write(ref _lastInboundMs, NowMs());
            Volatile.Write(ref _isAttached, 1);

            // Spin up new send / recv / watchdog loops bound to the fresh
            // transport + cancellation source. The buffered Establish frame
            // already inside `rebindStream` will flow through the recv loop
            // and drive ProcessEstablish → state machine Suspended → Established.
            _transport.StartSendLoop(_cts.Token);
            _recvTask = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
            _watchdogTask = Task.Run(() => RunWatchdogLoopAsync(_cts.Token));
        }
        return true;
    }

    /// <summary>
    /// Atomic suspended-session reap (issue #69b-2): if this session is
    /// still in <see cref="FixpState.Suspended"/> and its
    /// <see cref="SuspendedSinceMs"/> is at or before
    /// <paramref name="thresholdMs"/>, fully closes the session and
    /// returns <c>true</c>. Otherwise leaves the session untouched and
    /// returns <c>false</c>. Holds <see cref="_attachLock"/> for the
    /// duration so a concurrent <see cref="TryReattach"/> cannot race
    /// in between the snapshot the reaper made and the close decision.
    /// </summary>
    internal bool TryReapIfSuspended(long thresholdMs)
    {
        lock (_attachLock)
        {
            if (Volatile.Read(ref _isOpen) == 0) return false;
            if (State != FixpState.Suspended) return false;
            var since = Volatile.Read(ref _suspendedSinceMs);
            if (since == 0) return false;
            if (since > thresholdMs) return false;
            CloseLocked("suspended-timeout");
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close("dispose");
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); } catch { }
        try { if (_watchdogTask != null) await _watchdogTask.ConfigureAwait(false); } catch { }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
