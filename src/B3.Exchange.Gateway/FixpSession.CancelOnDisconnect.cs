using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;

/// <summary>
/// FixpSession cancel-on-disconnect handling (issue #54 / spec
/// §4.7): persistence of the Establish-time CoD parameters, the
/// armed-timer lifecycle (schedule / stop / fire), and the actual
/// MassCancelCommand emission when the grace window elapses. Split
/// out from FixpSession.cs (issue #139).
/// </summary>
public sealed partial class FixpSession
{

    /// <summary>
    /// Cancel-on-Disconnect criterion claimed by the most recent
    /// successful <c>Establish</c> (issue #54 / GAP-18, spec §4.7).
    /// Defaults to
    /// <c>DO_NOT_CANCEL_ON_DISCONNECT_OR_TERMINATE</c> until Establish
    /// completes. Set on the dispatch thread inside
    /// <see cref="ProcessEstablish"/>; read by
    /// <see cref="SuspendLocked"/> and <see cref="StopCodTimer"/>.
    /// </summary>
    public B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType CancelOnDisconnectType { get; private set; }
        = B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.DO_NOT_CANCEL_ON_DISCONNECT_OR_TERMINATE;

    /// <summary>
    /// Cancel-on-Disconnect grace window (ms) committed at Establish.
    /// Spec range: <c>[0, 60000]</c>; out-of-range values are clamped on
    /// ingest. Zero means "fire CoD as soon as the trigger is detected".
    /// </summary>
    public long CodTimeoutWindowMs { get; private set; }

    /// <summary>FIXP spec §4.7 maximum CoD grace window (ms).</summary>
    internal const long MaxCodTimeoutWindowMs = 60_000L;

    /// <summary>
    /// Test seam: stamp the cancel-on-disconnect parameters that would
    /// normally be set inside <see cref="ProcessEstablish"/>. Mirrors the
    /// pattern used by <c>ApplyTransition</c> in the suspend/reattach
    /// tests so suites can drive CoD scheduling without building a full
    /// Establish frame. Intentionally <c>internal</c>.
    /// </summary>
    internal void SetCancelOnDisconnectForTest(
        B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType type,
        long codTimeoutWindowMs)
    {
        CancelOnDisconnectType = type;
        if (codTimeoutWindowMs < 0) codTimeoutWindowMs = 0;
        if (codTimeoutWindowMs > MaxCodTimeoutWindowMs) codTimeoutWindowMs = MaxCodTimeoutWindowMs;
        CodTimeoutWindowMs = codTimeoutWindowMs;
    }

    /// <summary>
    /// Active CoD timer scheduled in <see cref="SuspendLocked"/> when the
    /// session is configured for cancel-on-disconnect; non-null only
    /// while the session is Suspended and the grace window has not yet
    /// elapsed. Owned by <see cref="_attachLock"/>.
    /// </summary>
    private Timer? _codTimer;

    /// <summary>
    /// Persist the cancel-on-disconnect knobs claimed by the peer's
    /// <c>Establish</c> frame (issue #54 / GAP-18, spec §4.7). Called on
    /// the recv thread inside <see cref="ProcessEstablish"/>; values are
    /// only consumed from <see cref="SuspendLocked"/> on the same path,
    /// so no synchronization is required.
    /// </summary>
    private void ApplyCodEstablishParams(in EstablishRequest req)
    {
        // The SBE field is a 1-byte enum followed by 1 byte of padding;
        // EstablishDecoder reads it as ushort for layout convenience.
        // Narrow to the underlying byte before casting to the enum.
        CancelOnDisconnectType =
            (B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType)(byte)req.CancelOnDisconnectType;
        var window = (long)Math.Min(req.CodTimeoutWindowMillis, (ulong)MaxCodTimeoutWindowMs);
        if (window < 0) window = 0;
        CodTimeoutWindowMs = window;
    }

    /// <summary>
    /// Returns true when the session's Establish-time
    /// <see cref="CancelOnDisconnectType"/> covers the
    /// transport-disconnect trigger (modes <c>1</c> and <c>3</c>). The
    /// explicit-Terminate trigger (modes <c>2</c> and <c>3</c>) is
    /// deferred until inbound peer-Terminate framing is wired.
    /// </summary>
    private bool CodCoversDisconnect()
    {
        var t = CancelOnDisconnectType;
        return t == B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_ONLY
            || t == B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_OR_TERMINATE;
    }

    /// <summary>
    /// Schedules the one-shot cancel-on-disconnect timer for this
    /// session. Caller MUST hold <see cref="_attachLock"/>. No-op when
    /// CoD is disabled or already armed for this Suspend cycle.
    /// </summary>
    private void ScheduleCodTimerLocked()
    {
        if (_codTimer is not null) return;
        if (!CodCoversDisconnect()) return;
        var window = CodTimeoutWindowMs;
        if (window < 0) window = 0;
        _logger.LogInformation(
            "fixp session {ConnectionId} arming cancel-on-disconnect (mode={Mode} windowMs={WindowMs})",
            ConnectionId, CancelOnDisconnectType, window);
        // Capture the current generation indirectly: the callback will
        // re-acquire _attachLock and re-check that the timer it sees in
        // the field is still the same instance and that we are still in
        // Suspended. That handshake is sufficient to avoid firing on a
        // session that has since reattached or closed.
        Timer? created = null;
        created = new Timer(_ => OnCodTimerFired(created!), null,
            dueTime: window, period: Timeout.Infinite);
        _codTimer = created;
    }

    /// <summary>
    /// Cancels and disposes any armed cancel-on-disconnect timer. Caller
    /// MUST hold <see cref="_attachLock"/>. Idempotent.
    /// </summary>
    private void StopCodTimerLocked()
    {
        var t = _codTimer;
        if (t is null) return;
        _codTimer = null;
        try { t.Dispose(); } catch { }
    }

    /// <summary>
    /// One-shot CoD timer callback. Runs on a thread-pool thread, so it
    /// re-acquires <see cref="_attachLock"/> to verify the session is
    /// still Suspended and that the firing timer is still the one we
    /// armed (i.e. no concurrent reattach/close). On commit, enqueues a
    /// session-scoped mass-cancel through the gateway-side ownership map
    /// (see <see cref="IInboundCommandSink.EnqueueMassCancel"/>) and
    /// bumps the lifecycle counter.
    /// </summary>
    private void OnCodTimerFired(Timer originating)
    {
        bool fire;
        lock (_attachLock)
        {
            if (!ReferenceEquals(_codTimer, originating)) return;
            _codTimer = null;
            // Only fire if still Suspended and not Closed. Reattach
            // flips to Established before this callback can re-enter the
            // lock, so the State check covers the rebind race.
            fire = Volatile.Read(ref _isOpen) == 1 && State == FixpState.Suspended;
        }
        try { originating.Dispose(); } catch { }
        if (!fire) return;

        // SecurityId=0 + SideFilter=null ⇒ "every resting order owned by
        // this session" — HostRouter resolves the actual order list via
        // OrderOwnershipMap.FilterMassCancel(session, firm, null, 0).
        var cmd = new MassCancelCommand(
            SecurityId: 0,
            SideFilter: null,
            EnteredAtNanos: _nowNanos());
        _logger.LogInformation(
            "fixp session {ConnectionId} cancel-on-disconnect firing (mode={Mode} windowMs={WindowMs})",
            ConnectionId, CancelOnDisconnectType, CodTimeoutWindowMs);
        try { _sink.EnqueueMassCancel(cmd, Identity, EnteringFirm); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} cancel-on-disconnect: EnqueueMassCancel threw",
                ConnectionId);
            return;
        }
        try { _options.LifecycleMetrics?.IncCancelOnDisconnectFired(); } catch { }
    }
}
