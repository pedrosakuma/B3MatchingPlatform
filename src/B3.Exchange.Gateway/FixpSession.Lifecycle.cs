using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
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
        // SuspendLocked closes the transport before publishing !IsAttached.
        // Its late recv-loop callback can therefore observe Suspended while
        // teardown still owns the generation. Do not abort the admission gate
        // in that window; the locked generation/attachment checks below will
        // discard the re-entrant callback.
        if (State != FixpState.Established
            && Volatile.Read(ref _suspendInProgress) == 0)
        {
            MarkCloseRequested();
            AbortBusinessAdmission();
        }
        // Decision must run under _attachLock so the generation check and
        // the actual lifecycle transition can't be split by a concurrent
        // TryReattach. Without the lock a stale callback could pass the
        // generation check, get preempted while the new transport is
        // installed, then close the new generation.
        ExecuteLogicalSessionExclusive(() =>
        {
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
                // Transport went away before/after Establish — peer is expected
                // to reconnect, so do not erase persisted state (issue #405).
                CloseLocked(reason, CloseKind.TransportError);
            }
        });
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
        ExecuteLogicalSessionExclusive(() =>
        {
            lock (_attachLock)
            {
                SuspendLocked(reason);
            }
        });
    }

    /// <summary>
    /// Issue #496: demote a still-<see cref="FixpState.Established"/> stale
    /// session for an Establish-path session takeover. Identical to
    /// <see cref="Suspend(string)"/> except it does NOT arm the
    /// cancel-on-disconnect timer: the successor transport has already
    /// arrived (the listener parsed its resuming <c>Establish</c> with a
    /// matching SessionId + SessionVerId), so the old transport's
    /// disconnect is immediately superseded and the firm's resting orders
    /// must be preserved — mirroring the <see cref="CloseKind.SessionTakeOver"/>
    /// semantics on the Negotiate-path takeover (#488). The caller follows
    /// this with <see cref="TryReattach"/> to bind the new transport.
    /// </summary>
    public void SuspendForTakeover(string reason)
    {
        ExecuteLogicalSessionExclusive(() =>
        {
            lock (_outboundLock)
            {
                lock (_attachLock)
                {
                    if (Volatile.Read(ref _isOpen) != 0
                        && Volatile.Read(ref _isAttached) != 0
                        && State == FixpState.Established)
                    {
                        ResetBusinessAdmissionLocked();
                    }
                    SuspendLocked(reason, armCancelOnDisconnect: false);
                }
            }
        });
    }

    /// <summary>
    /// <summary>
    /// Issue #405: writes the current FIXP envelope state to
    /// <see cref="_statePersister"/> if one is wired. Best-effort —
    /// persistence failures are logged but never propagate, so a
    /// transient disk hiccup does not kill the session. Caller must
    /// have set <see cref="SessionId"/>; otherwise the call is a
    /// no-op (no point persisting a zeroed identity).
    ///
    /// Suitable only for non-handshake-acking paths (post-Negotiate
    /// outbound seq advance, lifecycle close, etc.) — the Negotiate
    /// commit MUST use <see cref="TrySaveStateSnapshot"/> so a failed
    /// save aborts the handshake instead of acknowledging a
    /// SessionVerID that never reached disk.
    /// </summary>
    internal void SaveStateSnapshotSafe()
    {
        _ = TrySaveStateSnapshot();
    }

    /// <summary>
    /// Issue #405 (review finding): variant of
    /// <see cref="SaveStateSnapshotSafe"/> that surfaces success / failure
    /// to the caller so the Negotiate commit path can reject the
    /// handshake if the SessionVerID could not be made crash-durable.
    /// Returns <c>true</c> when there is no persister wired (the
    /// simulator is running in ephemeral mode), when SessionId is 0
    /// (nothing to persist yet), or when the underlying
    /// <see cref="IFixpSessionStatePersister.Save(in FixpSessionStateSnapshot)"/>
    /// call completes without throwing.
    /// </summary>
    internal bool TrySaveStateSnapshot()
    {
        var persister = _statePersister;
        if (persister is null) return true;
        if (SessionId == 0) return true;
        try
        {
            long outboundMsgSeqNum = Volatile.Read(ref _msgSeqNum);
            if ((ulong)outboundMsgSeqNum > uint.MaxValue)
            {
                _logger.LogCritical(
                    "fixp session {ConnectionId} sessionId={SessionId} refused to persist invalid outbound MsgSeqNum={OutboundMsgSeqNum}",
                    ConnectionId, SessionId, outboundMsgSeqNum);
                return false;
            }
            var snapshot = new B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot(
                SessionId: SessionId,
                SessionVerId: SessionVerId,
                OutboundMsgSeqNum: (uint)outboundMsgSeqNum,
                LastIncomingSeqNo: LastIncomingSeqNo,
                EnteringFirm: EnteringFirm,
                UpdatedAtNanos: (long)_timeSource.NowNanos());
            persister.Save(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} sessionId={SessionId} failed to persist state snapshot",
                ConnectionId, SessionId);
            return false;
        }
    }

    /// <summary>
    /// Body of <see cref="Suspend(string)"/>; assumes the caller already
    /// holds <see cref="_attachLock"/>. Idempotent — second caller is a
    /// no-op via the <see cref="_isAttached"/> CAS.
    /// </summary>
    private void SuspendLocked(string reason, bool armCancelOnDisconnect = true)
    {
        // Atomically claim the right to suspend; second caller is a no-op.
        // We use a separate CAS guard (not _isAttached) so the public
        // IsAttached flag stays true until after teardown + state transition,
        // preserving the invariant that observers seeing !IsAttached can rely
        // on State == Suspended AND the transport already being down.
        if (Interlocked.Exchange(ref _suspendInProgress, 1) == 1) return;
        if (Volatile.Read(ref _isAttached) == 0)
        {
            // A concurrent Close already cleared attachment; nothing to do.
            Volatile.Write(ref _suspendInProgress, 0);
            return;
        }
        _logger.LogInformation("fixp session {ConnectionId} suspending (reason={Reason})",
            ConnectionId, reason);
        // Best-effort teardown of cancellation + transport before publishing
        // Suspended. Tests and diagnostics use State==Suspended as the contract
        // that the transport is already detached/down; publishing the state
        // first races observers against the close side effect on fast machines.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* concurrent dispose */ }
        try { _transport.Close(reason); } catch (ObjectDisposedException) { /* transport already disposed */ }
        try { _transport.Stream.Dispose(); } catch (ObjectDisposedException) { /* stream already disposed */ }

        var action = ApplyTransition(FixpEvent.Detach);
        if (action != FixpAction.Accept || State != FixpState.Suspended)
        {
            _logger.LogInformation(
                "fixp session {ConnectionId} suspend declined (state={State} action={Action}); closing instead",
                ConnectionId, State, action);
            // State machine refused the demote → fall through to a
            // hard close. Treat as transport loss (peer may reconnect)
            // unless we know otherwise (issue #405).
            MarkCloseRequested();
            CloseLocked(reason, CloseKind.TransportError);
            return;
        }
        Volatile.Write(ref _suspendedSinceMs, NowMs());
        // Issue #496: an Establish-path takeover suspends a still-Established
        // session whose successor transport has already arrived, so CoD must
        // be skipped (the disconnect is immediately superseded).
        if (armCancelOnDisconnect)
            ScheduleCodTimerLocked();
        // Issue #405: persist the envelope on suspend so a host crash
        // while the session is parked still produces an
        // EstablishmentAck.lastIncomingSeqNo matching reality.
        SaveStateSnapshotSafe();
        // Publish !IsAttached only after state transition + teardown so
        // consumers observing IsAttached==false see a fully-suspended session.
        Volatile.Write(ref _isAttached, 0);
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
        ExecuteLogicalSessionExclusive(() =>
        {
            lock (_attachLock)
            {
                if (!ReferenceEquals(_transport, originatingTransport)) return;
                MarkCloseRequested();
                // Triggered exclusively from background loops (watchdog) on
                // an idle-timeout / IO error → peer is expected to reconnect.
                CloseLocked(reason, CloseKind.TransportError);
            }
        });
    }

    /// <summary>
    /// Generation-aware graceful terminate: writes a Terminate frame to the
    /// transport observed by the caller, then closes only if that transport is
    /// still current. Used by the watchdog so stale ticks after re-attach cannot
    /// tear down a fresh transport.
    /// </summary>
    private async Task SendTerminateIfTransportCurrentAndCloseAsync(
        byte terminationCode, string reason, TcpTransport originatingTransport, CloseKind kind)
    {
        uint sessionId;
        ulong sessionVerId;
        lock (_attachLock)
        {
            if (!ReferenceEquals(_transport, originatingTransport)) return;
            if (Volatile.Read(ref _isOpen) == 0) return;
            sessionId = SessionId;
            sessionVerId = SessionVerId;
        }

        var frame = new byte[SessionRejectEncoder.TerminateTotal];
        SessionRejectEncoder.EncodeTerminate(frame, sessionId, sessionVerId, terminationCode);
        try
        {
            await originatingTransport.SendDirectAsync(frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} failed to write Terminate({Code})",
                ConnectionId, terminationCode);
        }

        ExecuteLogicalSessionExclusive(() =>
        {
            lock (_attachLock)
            {
                if (!ReferenceEquals(_transport, originatingTransport)) return;
                MarkCloseRequested();
                CloseLocked(reason, kind);
            }
        });
    }

    /// <summary>
    /// Closes the session with a diagnostic reason. Legacy overload
    /// kept for tests and existing call-sites that have not been
    /// classified yet; defaults to <see cref="CloseKind.LocalTerminate"/>
    /// so generic local teardown remains terminal without being treated
    /// as a peer-Terminate CoD trigger.
    /// </summary>
    public void Close(string reason) => Close(reason, CloseKind.LocalTerminate);

    /// <summary>
    /// Classified close (issue #405). The <paramref name="kind"/>
    /// drives whether persisted FIXP session state and the outbound
    /// journal are erased (terminal kinds) or preserved (kinds where
    /// the peer is expected to reconnect).
    /// </summary>
    public void Close(string reason, CloseKind kind)
    {
        MarkCloseRequested();
        AbortBusinessAdmission();
        ExecuteLogicalSessionExclusive(() =>
        {
            lock (_attachLock)
            {
                CloseLocked(reason, kind);
            }
        });
    }

    /// <summary>
    /// Body of <see cref="Close(string)"/>; assumes the caller already
    /// holds <see cref="_attachLock"/>. Called directly from
    /// <see cref="Suspend"/> when the state-machine refuses to demote
    /// (so we don't try to re-acquire the lock recursively, and so the
    /// transition from "trying to suspend" to "actually closing" is
    /// atomic from the perspective of a concurrent re-attach).
    /// </summary>
    private void CloseLocked(string reason, CloseKind kind)
    {
        MarkCloseRequested();
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return;
        Volatile.Write(ref _isAttached, 0);
        // Clear the suspended-since timestamp so a concurrent reaper poll
        // doesn't try to re-Close a session that's already terminal.
        Volatile.Write(ref _suspendedSinceMs, 0);
        StopCodTimerLocked();
        _lastCloseKind = kind;
        bool ownsLogicalSession = _sessionRegistry?.IsCurrent(this) ?? true;
        _logger.LogInformation(
            "fixp session {ConnectionId} closing (kind={Kind})",
            ConnectionId, kind);
        if (CodCoversDisconnect(kind))
        {
            FireCancelOnDisconnect();
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* concurrent dispose race; benign */ }
        // The transport may have already closed (it's the source of the
        // callback in IO-error paths). Either way, ensure it's down so the
        // send loop wakes up and exits.
        _transport.Close(reason);
        // Release the FIXP session claim (if any) so the same sessionID
        // can be re-negotiated by a future transport. Daily reset also
        // clears the last-seen SessionVerID because B3 daily state starts
        // fresh rather than obeying prior-day monotonicity.
        if (_claimedSessionId != 0 && _claims is not null)
        {
            try { _claims.Release(_claimedSessionId, this, forgetLastVersion: kind == CloseKind.DailyReset); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp session {ConnectionId} sessionId={SessionId} failed to release claim during close",
                    ConnectionId, _claimedSessionId);
            }
            _claimedSessionId = 0;
        }
        // Notify the engine sink so it releases any cached references to this
        // session (see IInboundCommandSink.OnSessionClosed). Without this the
        // ChannelDispatcher's order-owners map keeps the session rooted for
        // the lifetime of every resting order it placed → unbounded memory.
        // Issue #488: skip for SessionTakeOver — the new session has the same
        // Identity and has already taken over the claim; evicting ownership
        // entries here would break routing of passive fills to the new session.
        if (kind != CloseKind.SessionTakeOver && ownsLogicalSession)
        {
            try { _sink.OnSessionClosed(Identity); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp session {ConnectionId} OnSessionClosed callback threw",
                    ConnectionId);
            }
        }
        // Issue #289 / #405 / #416: terminal close ⇒ drop the persisted
        // retransmit ring file AND the unbounded outbound journal AND
        // the state snapshot. Scoped to terminal close kinds
        // so transport-error and host-shutdown closes preserve state for
        // the reconnecting peer to resync against (SBE 5.2 §1.5
        // recoverable serverFlow). Non-removing kinds also save the
        // final state so the resume point on reconnect is accurate.
        bool removePersistence =
            kind == CloseKind.PeerTerminate
            || kind == CloseKind.LocalTerminate
            || kind == CloseKind.KeepaliveLapsed
            || kind == CloseKind.SuspendedTimeout
            || kind == CloseKind.DailyReset;
        if (removePersistence)
        {
            _retxBuffer.Dispose();
            if (ownsLogicalSession && _outboundJournal is not null && SessionId != 0)
            {
                try { _outboundJournal.Remove(SessionId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "fixp session {ConnectionId} sessionId={SessionId} failed to remove outbound journal",
                        ConnectionId, SessionId);
                }
            }
            if (ownsLogicalSession && _statePersister is not null && SessionId != 0)
            {
                try { _statePersister.Remove(SessionId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "fixp session {ConnectionId} sessionId={SessionId} failed to remove state snapshot",
                        ConnectionId, SessionId);
                }
            }
        }
        else if (kind != CloseKind.SessionTakeOver && ownsLogicalSession)
        {
            // Host shutdown / transport error: keep journal + ring;
            // persist final snapshot so the reconnecting peer sees the
            // correct LastIncomingSeqNo / OutboundMsgSeqNum.
            // SessionTakeOver is intentionally excluded: the new session
            // already persisted its snapshot (with the higher SessionVerID)
            // before calling Close on this evicted session. Saving here
            // would overwrite the new snapshot with this session's older
            // SessionVerID, rolling durable state backwards.
            SaveStateSnapshotSafe();
        }
        else if (kind == CloseKind.SessionTakeOver)
        {
            // The successor already deep-copied the retained replay window
            // while both outbound locks were held. Release this retired
            // transport generation's pooled frame references without touching
            // the shared per-SessionID journal.
            _retxBuffer.Dispose();
        }
        try { _onClosed?.Invoke(this, reason); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} onClosed callback threw",
                ConnectionId);
        }
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
    internal AttachmentSnapshot CaptureAttachmentSnapshot()
    {
        lock (_attachLock)
        {
            return new AttachmentSnapshot(
                State,
                Volatile.Read(ref _attachmentGeneration));
        }
    }

    public bool TryReattach(Stream rebindStream)
    {
        ArgumentNullException.ThrowIfNull(rebindStream);
        var expected = CaptureAttachmentSnapshot();
        if (expected.State != FixpState.Suspended)
            return false;
        return TryReattachCore(rebindStream, expected, allowEstablishedTakeover: false);
    }

    internal bool TryReattachForEstablish(
        Stream rebindStream,
        AttachmentSnapshot expected)
    {
        ArgumentNullException.ThrowIfNull(rebindStream);
        if (expected.State is not (FixpState.Suspended or FixpState.Established))
            return false;
        return TryReattachCore(rebindStream, expected, allowEstablishedTakeover: true);
    }

    private bool TryReattachCore(
        Stream rebindStream,
        AttachmentSnapshot expected,
        bool allowEstablishedTakeover)
    {
        Task? oldRecv = null;
        Task? oldWatchdog = null;
        long reservation = 0;

        bool reserved = ExecuteLogicalSessionExclusive(() =>
        {
            lock (_outboundLock)
            {
                lock (_attachLock)
                {
                    if (Volatile.Read(ref _isOpen) == 0
                        || Volatile.Read(ref _attachmentGeneration) != expected.Generation
                        || Volatile.Read(ref _reattachReservation) != 0)
                        return false;

                    if (expected.State == FixpState.Established)
                    {
                        if (!allowEstablishedTakeover
                            || State != FixpState.Established
                            || Volatile.Read(ref _isAttached) == 0)
                            return false;

                        ResetBusinessAdmissionLocked();
                        SuspendLocked(
                            $"establish-takeover:generation={expected.Generation}",
                            armCancelOnDisconnect: false);
                    }
                    else if (State != FixpState.Suspended
                        || Volatile.Read(ref _isAttached) != 0)
                    {
                        return false;
                    }

                    if (Volatile.Read(ref _isOpen) == 0
                        || State != FixpState.Suspended
                        || Volatile.Read(ref _isAttached) != 0)
                        return false;

                    reservation = expected.Generation + 1;
                    Volatile.Write(ref _attachmentGeneration, reservation);
                    Volatile.Write(ref _reattachReservation, reservation);
                    oldRecv = _recvTask;
                    oldWatchdog = _watchdogTask;
                    return true;
                }
            }
        });
        if (!reserved)
            return false;

        if (!DrainPriorAttachment(oldRecv, oldWatchdog))
        {
            ClearReattachReservation(reservation);
            return false;
        }

        bool attached = ExecuteLogicalSessionExclusive(() =>
        {
            lock (_outboundLock)
            {
                lock (_attachLock)
                {
                    if (Volatile.Read(ref _reattachReservation) != reservation
                        || Volatile.Read(ref _isOpen) == 0
                        || Volatile.Read(ref _isAttached) != 0
                        || State != FixpState.Suspended)
                        return false;

                    _logger.LogInformation(
                        "fixp session {ConnectionId} re-attaching generation={Generation} (sessionId={SessionId} sessionVerId={SessionVerId})",
                        ConnectionId, reservation, SessionId, SessionVerId);

                    try { _cts.Dispose(); } catch (ObjectDisposedException) { }
                    _cts = new CancellationTokenSource();
                    ResetBusinessAdmissionLocked();
                    _transport = CreateBoundTransport(rebindStream);
                    StopCodTimerLocked();
                    Volatile.Write(ref _suspendedSinceMs, 0);
                    Volatile.Write(ref _lastInboundMs, NowMs());
                    Volatile.Write(ref _isAttached, 1);
                    Volatile.Write(ref _suspendInProgress, 0);
                    Volatile.Write(ref _reattachReservation, 0);

                    _transport.StartSendLoop(_cts.Token);
                    _recvTask = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
                    _watchdogTask = Task.Run(() => RunWatchdogLoopAsync(_cts.Token));
                    return true;
                }
            }
        });
        if (!attached)
            ClearReattachReservation(reservation);
        return attached;
    }

    private bool DrainPriorAttachment(Task? oldRecv, Task? oldWatchdog)
    {
        if (oldRecv is not null)
        {
            try
            {
                if (!oldRecv.Wait(TimeSpan.FromSeconds(2)))
                    return false;
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex,
                    "fixp session {ConnectionId} old recv task surfaced unexpected exception during re-attach drain",
                    ConnectionId);
            }
        }
        if (oldWatchdog is not null)
        {
            try
            {
                if (!oldWatchdog.Wait(TimeSpan.FromSeconds(2)))
                    return false;
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex,
                    "fixp session {ConnectionId} old watchdog task surfaced unexpected exception during re-attach drain",
                    ConnectionId);
            }
        }
        return true;
    }

    private void ClearReattachReservation(long reservation)
    {
        lock (_attachLock)
        {
            Interlocked.CompareExchange(ref _reattachReservation, 0, reservation);
        }
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
        return ExecuteLogicalSessionExclusive(() =>
        {
            lock (_attachLock)
            {
                if (Volatile.Read(ref _isOpen) == 0) return false;
                if (State != FixpState.Suspended) return false;
                var since = Volatile.Read(ref _suspendedSinceMs);
                if (since == 0) return false;
                if (since > thresholdMs) return false;
                MarkCloseRequested();
                CloseLocked("suspended-timeout", CloseKind.SuspendedTimeout);
                return true;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        Close("dispose");
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected: recv loop cancelled by Close */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} recv task threw during DisposeAsync drain",
                ConnectionId);
        }
        try { if (_watchdogTask != null) await _watchdogTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected: watchdog cancelled by Close */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} watchdog task threw during DisposeAsync drain",
                ConnectionId);
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _retxBuffer.Dispose();
        _cts.Dispose();
    }
}
