using B3.EntryPoint.Wire;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Contracts;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway;

/// <summary>
/// TCP accept loop. Each accepted socket is wrapped in a
/// <see cref="NetworkStream"/> + <see cref="FixpSession"/>. The caller
/// supplies a factory to assign per-connection identity (ConnectionId,
/// EnteringFirm, SessionId), so the listener doesn't need to know about
/// authentication or firm-mapping policy.
/// </summary>
public sealed class EntryPointListener : IAsyncDisposable
{
    public readonly record struct AcceptedConnection(long ConnectionId, uint EnteringFirm, uint SessionId);

    private readonly IPEndPoint _endpoint;
    private readonly IInboundCommandSink _sink;
    private readonly SessionRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EntryPointListener> _logger;
    private readonly Func<EndPoint?, AcceptedConnection> _identityFactory;
    private readonly FixpSessionOptions _sessionOptions;
    private readonly Action<FixpSession, string>? _onSessionClosed;
    private readonly NegotiationValidator? _negotiationValidator;
    private readonly SessionClaimRegistry? _sessionClaims;
    private readonly EstablishValidator? _establishValidator;
    private readonly B3.Exchange.Contracts.RetransmitMetrics? _retransmitMetrics;
    private readonly B3.Exchange.Gateway.Persistence.IFixpOutboundJournal? _outboundJournal;
    private readonly B3.Exchange.Gateway.Persistence.IFixpSessionStatePersister? _statePersister;
    private readonly IDictionary<uint, B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot>? _persistedSessionStates;
    private readonly Func<uint, int?>? _persistedMaxOrderRateResolver;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _reaperTask;
    // Atomic monotonic id allocator. Today the only writer is the accept
    // loop (single thread), but we keep Interlocked.Increment to preserve
    // the "any-thread safe" contract of DefaultIdentityFactory in case a
    // future custom factory is invoked off the accept loop. Audited as
    // part of issue #138.
    private long _nextConnectionId;
    private readonly List<FixpSession> _sessions = new();
    private readonly object _lock = new();

    public IPEndPoint? LocalEndpoint => (IPEndPoint?)_listener?.LocalEndpoint;

    /// <summary>
    /// Snapshot of currently active sessions for diagnostics / metrics.
    /// Closed sessions remain in the list until <see cref="DisposeAsync"/>
    /// runs; callers should filter on <see cref="FixpSession.IsOpen"/>
    /// if they only want live ones.
    /// </summary>
    public IReadOnlyList<FixpSession> ActiveSessions
    {
        get { lock (_lock) return _sessions.ToArray(); }
    }

    public EntryPointListener(IPEndPoint endpoint, IInboundCommandSink sink,
        SessionRegistry registry,
        ILoggerFactory loggerFactory,
        Func<EndPoint?, AcceptedConnection>? identityFactory = null,
        FixpSessionOptions? sessionOptions = null,
        Action<FixpSession, string>? onSessionClosed = null,
        NegotiationValidator? negotiationValidator = null,
        SessionClaimRegistry? sessionClaims = null,
        EstablishValidator? establishValidator = null,
        B3.Exchange.Contracts.RetransmitMetrics? retransmitMetrics = null,
        B3.Exchange.Gateway.Persistence.IFixpOutboundJournal? outboundJournal = null,
        B3.Exchange.Gateway.Persistence.IFixpSessionStatePersister? statePersister = null,
        IReadOnlyDictionary<uint, B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot>? persistedSessionStates = null,
        Func<uint, int?>? persistedMaxOrderRateResolver = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(registry);
        _endpoint = endpoint;
        _sink = sink;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EntryPointListener>();
        _identityFactory = identityFactory ?? DefaultIdentityFactory;
        _sessionOptions = sessionOptions ?? FixpSessionOptions.Default;
        _sessionOptions.Validate();
        _onSessionClosed = onSessionClosed;
        _negotiationValidator = negotiationValidator;
        _sessionClaims = sessionClaims;
        _establishValidator = establishValidator;
        _retransmitMetrics = retransmitMetrics;
        _outboundJournal = outboundJournal;
        _statePersister = statePersister;
        _persistedSessionStates = persistedSessionStates is null
            ? null
            : new Dictionary<uint, B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot>(persistedSessionStates);
        _persistedMaxOrderRateResolver = persistedMaxOrderRateResolver;
        if ((_negotiationValidator is null) ^ (_sessionClaims is null))
        {
            throw new ArgumentException(
                "negotiationValidator and sessionClaims must be supplied together (both null = legacy passthrough mode).");
        }
    }

    // Convenience overload for callers (e.g., tests) that don't need an
    // out-of-process session registry. The listener still creates and owns a
    // private registry instance; sessions register/deregister against it.
    public EntryPointListener(IPEndPoint endpoint, IInboundCommandSink sink,
        ILoggerFactory loggerFactory,
        Func<EndPoint?, AcceptedConnection>? identityFactory = null,
        FixpSessionOptions? sessionOptions = null,
        Action<FixpSession, string>? onSessionClosed = null)
        : this(endpoint, sink, new SessionRegistry(), loggerFactory, identityFactory, sessionOptions, onSessionClosed)
    {
    }

    private AcceptedConnection DefaultIdentityFactory(EndPoint? remote)
    {
        long id = Interlocked.Increment(ref _nextConnectionId);
        return new AcceptedConnection(id, EnteringFirm: 0, SessionId: (uint)id);
    }

    public void Start()
    {
        _listener = new TcpListener(_endpoint);
        _listener.Start();
        _logger.LogInformation("entrypoint listener bound to {Endpoint}", _listener.LocalEndpoint);
        _acceptTask = Task.Run(() => RunAcceptLoopAsync(_cts.Token));
        if (_sessionOptions.SuspendedTimeoutMs > 0)
            _reaperTask = Task.Run(() => RunSuspendedReaperAsync(_cts.Token));
    }

    /// <summary>
    /// Periodically scans <see cref="_sessions"/> for FIXP sessions that
    /// have been in <see cref="FixpState.Suspended"/> longer than
    /// <see cref="FixpSessionOptions.SuspendedTimeoutMs"/> and fully closes
    /// them. Without this, every transport drop while Established (issue
    /// #69a) leaves the session, its claim, and the engine's order-owner
    /// reference rooted in memory until process exit. The full re-attach
    /// machinery (#69b-2) will move long-lived suspended sessions back to
    /// Established before the reaper fires.
    /// </summary>
    private async Task RunSuspendedReaperAsync(CancellationToken ct)
    {
        // Poll often enough to honor the timeout within ~10% latency, but
        // never busy-loop and never sleep longer than 30 s in production
        // configurations. Floor 50 ms keeps the test suite fast when
        // tests set SuspendedTimeoutMs to e.g. 200 ms.
        var timeoutMs = _sessionOptions.SuspendedTimeoutMs;
        var pollMs = Math.Max(50, Math.Min(timeoutMs / 4, 30_000));
        var poll = TimeSpan.FromMilliseconds(pollMs);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(poll, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; /* expected: reaper cancelled during shutdown */ }
                ReapSuspendedOnce(Environment.TickCount64);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "suspended-session reaper terminated unexpectedly");
        }
    }

    /// <summary>
    /// Single pass of the suspended-session reaper: fully closes any
    /// session whose <see cref="FixpSession.SuspendedSinceMs"/> is older
    /// than <see cref="FixpSessionOptions.SuspendedTimeoutMs"/>. Exposed
    /// internally so tests can drive the reaper deterministically without
    /// waiting for the Timer-based loop.
    /// </summary>
    internal void ReapSuspendedOnce(long nowMs)
    {
        var timeoutMs = _sessionOptions.SuspendedTimeoutMs;
        if (timeoutMs <= 0) return;
        var thresholdMs = nowMs - timeoutMs;
        FixpSession[] snapshot;
        lock (_lock) snapshot = _sessions.ToArray();
        foreach (var s in snapshot)
        {
            // Per-session atomic reap: the session re-validates state
            // and SuspendedSinceMs under its own _attachLock so a
            // concurrent re-attach (#69b-2) racing the reap cannot lose
            // the connection.
            try
            {
                if (s.TryReapIfSuspended(thresholdMs))
                {
                    _sessionOptions.LifecycleMetrics?.IncReaped();
                    _logger.LogInformation(
                        "reaped suspended session {ConnectionId} sessionId={SessionId} (timeout={TimeoutMs}ms)",
                        s.ConnectionId, s.SessionId, timeoutMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "reaper failed to close session {ConnectionId}", s.ConnectionId);
            }
        }
    }

    /// <summary>
    /// Spec §4.5.1 (#GAP-09 / issue #47): at start of each trading day the
    /// gateway resets inbound + outbound MsgSeqNum counters to 1 and
    /// expects every client to reconnect with a fresh
    /// <c>Negotiate</c>+<c>Establish(nextSeqNo=1)</c>. We implement this
    /// the conservative way — terminate every live session — because
    /// per-session in-place reset would have to coordinate the dispatch
    /// thread, the retransmission ring, the claim ledger, and any
    /// in-flight Establish handshake atomically. Forcing reconnect makes
    /// the whole transition trivially correct and is conformant with the
    /// spec (clients are required to be able to handle this anyway).
    ///
    /// <para>Idempotent: calls <see cref="FixpSession.Close(string)"/> on
    /// each currently-active session; a session that is already closed
    /// is a no-op via the existing CAS guard. Returns the number of
    /// sessions that were live at the moment of the snapshot.</para>
    ///
    /// <para>Thread-safety: snapshots <see cref="_sessions"/> under
    /// <see cref="_lock"/>, then iterates outside the lock so
    /// <c>Close</c> (which takes <c>_attachLock</c> on the session) does
    /// not nest the listener's mutex. Safe to call from the HTTP thread
    /// or from the daily-rollover scheduler timer.</para>
    /// </summary>
    public int TerminateAllSessions(string reason, CloseKind closeKind = CloseKind.HostShutdown)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        FixpSession[] snapshot;
        lock (_lock) snapshot = _sessions.ToArray();
        int closed = 0;
        foreach (var s in snapshot)
        {
            if (!s.IsOpen) continue;
            try
            {
                s.Close(reason, closeKind);
                if (closeKind == CloseKind.DailyReset)
                    _persistedSessionStates?.Remove(s.SessionId);
                closed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "terminate-all-sessions: failed to close session {ConnectionId}",
                    s.ConnectionId);
            }
        }
        _logger.LogInformation(
            "terminated {Count} session(s) (reason={Reason})", closed, reason);
        return closed;
    }

    /// <summary>
    /// Issue #487: waits for all live sessions' outbound queues to drain
    /// before closing. Prevents frames (e.g., ER_Cancel from option expiry
    /// sweep) from being lost when <see cref="TerminateAllSessions"/> closes
    /// sockets. Per-session failures are logged and swallowed so one stuck
    /// session never blocks the rest.
    /// </summary>
    public async Task DrainAllSessionsOutboundAsync(TimeSpan timeout)
    {
        FixpSession[] snapshot;
        lock (_lock) snapshot = _sessions.ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int drained = 0, pending = 0, closed = 0;
        var tasks = new List<Task<(FixpSession s, bool ok)>>(snapshot.Length);
        foreach (var s in snapshot)
        {
            if (!s.IsOpen) { closed++; continue; }
            if (s.SendQueueDepth == 0) { drained++; continue; }
            pending++;
            tasks.Add(DrainOneAsync(s, timeout));
        }
        if (tasks.Count > 0)
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var (s, ok) in results)
            {
                if (ok) drained++;
                else _logger.LogWarning(
                    "drain-outbound: session {ConnectionId} timed out with {Depth} frames pending",
                    s.ConnectionId, s.SendQueueDepth);
            }
        }
        _logger.LogInformation(
            "drain-outbound: drained={Drained} timedOut={TimedOut} alreadyClosed={Closed} duration={DurationMs}ms",
            drained, pending - (drained - (snapshot.Length - pending - closed)), closed, sw.ElapsedMilliseconds);
    }

    private static async Task<(FixpSession s, bool ok)> DrainOneAsync(FixpSession s, TimeSpan timeout)
    {
        try
        {
            var ok = await s.WaitForSendQueueDrainAsync(timeout).ConfigureAwait(false);
            return (s, ok);
        }
        catch
        {
            return (s, false);
        }
    }

    /// <summary>
    /// Graceful-shutdown phase 1 (issue #171 / A7): stop accepting new TCP
    /// connections without closing the existing sessions or unbinding the
    /// listening socket. The accept loop and the suspended-session reaper
    /// are cancelled and awaited; existing sessions keep flowing inbound +
    /// outbound traffic so the host can continue to drain in-flight work
    /// before the subsequent <see cref="TerminateAllSessionsAsync"/> step.
    ///
    /// <para>Idempotent: safe to call multiple times. Subsequent calls
    /// short-circuit because <see cref="_acceptTask"/> is already torn
    /// down. After this returns, <see cref="DisposeAsync"/> is still
    /// required to release sockets and dispose remaining sessions.</para>
    /// </summary>
    public async Task StopAcceptingAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_acceptTask != null) { try { await _acceptTask.ConfigureAwait(false); } catch { } _acceptTask = null; }
        if (_reaperTask != null) { try { await _reaperTask.ConfigureAwait(false); } catch { } _reaperTask = null; }
        _logger.LogInformation("entrypoint listener stopped accepting new connections");
    }

    /// <summary>
    /// Graceful-shutdown phase 2 (issue #171 / A7): broadcast
    /// <c>Terminate(<paramref name="terminationCode"/>)</c> to every live
    /// FIXP session, then close them. Awaits each direct-send to give the
    /// peer a chance to read the frame before TCP RST/FIN; failures are
    /// logged and swallowed so one stuck session can't block the rest.
    /// Returns the number of sessions that were live at snapshot time.
    /// </summary>
    public async Task<int> TerminateAllSessionsAsync(byte terminationCode, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        FixpSession[] snapshot;
        lock (_lock) snapshot = _sessions.ToArray();
        int closed = 0;
        foreach (var s in snapshot)
        {
            if (!s.IsOpen) continue;
            try
            {
                await s.SendTerminateAndCloseAsync(terminationCode, reason, CloseKind.HostShutdown).ConfigureAwait(false);
                closed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "graceful terminate failed for session {ConnectionId}",
                    s.ConnectionId);
            }
        }
        _logger.LogInformation(
            "broadcast Terminate({Code}) to {Count} session(s) (reason={Reason})",
            terminationCode, closed, reason);
        return closed;
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket sock;
                try { sock = await _listener!.AcceptSocketAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; /* expected: listener cancelled during shutdown */ }
                catch (ObjectDisposedException) { return; /* expected: listener disposed before accept completed */ }

                sock.NoDelay = true;
                // Per-connection task: parses the first frame to decide
                // re-attach (#69b-2) vs new session, with a hard timeout
                // (FirstFrameTimeoutMs) so a slowloris client cannot pin
                // an accept slot indefinitely. We deliberately do NOT
                // await this — accept must remain responsive.
                _ = Task.Run(() => HandleAcceptedConnectionAsync(sock, ct));
            }
        }
        catch (OperationCanceledException) { /* expected: accept loop cancelled during shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "entrypoint accept loop terminated unexpectedly");
        }
    }

    /// <summary>
    /// Per-connection handler: reads exactly one FIXP frame from the
    /// new socket (within <see cref="FixpSessionOptions.FirstFrameTimeoutMs"/>),
    /// inspects its templateId, and either re-attaches to an existing
    /// <see cref="FixpSession"/> or constructs a new one with the
    /// already-consumed bytes prepended to the stream.
    /// </summary>
    private async Task HandleAcceptedConnectionAsync(Socket sock, CancellationToken ct)
    {
        NetworkStream stream;
        try { stream = new NetworkStream(sock, ownsSocket: true); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to wrap accepted socket; closing");
            try { sock.Dispose(); } catch { }
            return;
        }

        // Legacy / no-rebind mode: there are no session claims to consult,
        // so the first-frame router has nothing to do. Construct the
        // FixpSession eagerly around the raw stream as in the pre-#69b
        // listener — this preserves test ergonomics that assert
        // ActiveSessions.Count grows immediately on TCP accept (the
        // ApplyTransition seam in EntryPointListenerReaperTests / the
        // wire-driven heartbeat tests rely on this).
        if (_sessionClaims is null)
        {
            ConstructAndStartSession(stream, sock, firstFrame: null, persistedState: null);
            return;
        }

        // Read the first frame under a strict per-connection timeout.
        byte[] firstFrame;
        ushort templateId;
        try
        {
            using var firstFrameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstFrameCts.CancelAfter(_sessionOptions.FirstFrameTimeoutMs);
            (firstFrame, templateId) = await ReadFirstFrameAsync(stream, firstFrameCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "first-frame timeout from {Remote}; closing socket",
                SafeRemote(sock));
            try { stream.Dispose(); } catch { }
            return;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "failed to read first frame from {Remote}; closing socket",
                SafeRemote(sock));
            try { stream.Dispose(); } catch { }
            return;
        }

        // Re-attach decision: only meaningful when the first message is
        // an Establish targeting an already-claimed sessionId currently
        // held by a FixpSession (Suspended, or still-Established and taken
        // over per #496) with matching SessionVerId.
        if (templateId == EntryPointFrameReader.TidEstablish
            && firstFrame.Length >= EntryPointFrameReader.WireHeaderSize + EstablishDecoder.BlockLength
            && EstablishDecoder.TryDecode(
                firstFrame.AsSpan(EntryPointFrameReader.WireHeaderSize, EstablishDecoder.BlockLength),
                out var req, out _))
        {
            if (_sessionClaims.TryGetActiveClaim(req.SessionId, out var holder, out var lastVerId)
                && holder is FixpSession existing
                && existing.SessionVerId == req.SessionVerId
                && req.SessionVerId == lastVerId)
            {
                // Snapshot State ONCE so the takeover decision is consistent: a
                // connection that matched a Suspended session must never later
                // re-read State and escalate to SuspendForTakeover — by then a
                // concurrent reconnect may have legitimately reattached and
                // moved the session back to Established, and tearing that fresh
                // transport down would steal a live successor. Only a connection
                // that matched an Established session performs the takeover.
                var matchedState = existing.State;
                if (matchedState == FixpState.Suspended
                    || matchedState == FixpState.Established)
                {
                    // Issue #496: a still-Established stale claim means the
                    // prior TCP death has not been reaped yet (hard kill / no
                    // FIN, before the idle-timeout watchdog fired). A resuming
                    // Establish with a matching SessionId + SessionVerId is a
                    // cold-resume reconnect (spec §5.3), not a fresh session —
                    // the peer that holds the session demonstrably reconnected.
                    // Force-suspend the stale session (preserving order
                    // ownership + persistence, skipping Cancel-on-Disconnect) so
                    // the proven re-attach path can take over deterministically,
                    // instead of falling through to a fresh Idle session that
                    // rejects UNNEGOTIATED. Mirrors the Negotiate-path takeover
                    // added in #488.
                    if (matchedState == FixpState.Established)
                    {
                        _logger.LogInformation(
                            "establish-takeover: force-suspending still-Established session {ConnectionId} for resuming Establish (sessionId={SessionId} sessionVerId={SessionVerId} from {Remote})",
                            existing.ConnectionId, req.SessionId, req.SessionVerId, SafeRemote(sock));
                        existing.SuspendForTakeover(
                            $"establish-takeover:sessionId={req.SessionId}");
                    }

                    var prepended = new PrependedStream(firstFrame, stream);
                    if (existing.TryReattach(prepended))
                    {
                        _logger.LogInformation(
                            "re-attached transport to existing session {ConnectionId} (sessionId={SessionId} from {Remote})",
                            existing.ConnectionId, existing.SessionId, SafeRemote(sock));
                        return;
                    }
                    _logger.LogInformation(
                        "re-attach raced and lost for sessionId={SessionId} from {Remote}; closing new socket",
                        req.SessionId, SafeRemote(sock));
                    try { prepended.Dispose(); } catch { }
                    return;
                }
            }
        }

        // Issue #405 boot rehydration: if the first frame targets a
        // SessionId we loaded from the state persister at boot, hand
        // the snapshot to the new FixpSession so its identity
        // (SessionVerId / LastIncomingSeqNo / outbound seq) resumes
        // where the previous incarnation left off.
        //
        // Two distinct entry shapes need rehydration after a host crash:
        //   (a) Negotiate with a STRICTLY-GREATER SessionVerId — peer
        //       abandons the old session and starts fresh; SeedLastVersion
        //       on the claim registry enforces monotonicity vs the
        //       persisted version (spec §1.4).
        //   (b) Establish with the SAME SessionVerId — peer resumes the
        //       prior session per the EstablishmentAck.serverFlow=RECOVERABLE
        //       contract (spec §1.5). The rehydrated session must come
        //       up in FixpState.Negotiated (not Idle) so the Establish
        //       lands as (Negotiated, Establish) → Established instead
        //       of being rejected as UNNEGOTIATED.
        // The active-claim re-attach branch above already handles the
        // hot path (same-process Suspended session); this block handles
        // the cold path where the prior incarnation died.
        B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot? rehydrateState = null;
        int? persistedMaxOrderRatePerSecond = null;
        bool resumeAsNegotiated = false;
        if (_persistedSessionStates is not null
            && firstFrame.Length >= EntryPointFrameReader.WireHeaderSize)
        {
            uint sessionIdFromFrame = 0;
            ulong sessionVerIdFromFrame = 0;
            bool isEstablish = false;
            bool decoded = false;
            if (templateId == EntryPointFrameReader.TidNegotiate
                && firstFrame.Length >= EntryPointFrameReader.WireHeaderSize + NegotiateDecoder.BlockLength)
            {
                var fixedBlock = firstFrame.AsSpan(EntryPointFrameReader.WireHeaderSize, NegotiateDecoder.BlockLength);
                var varData = firstFrame.AsSpan(EntryPointFrameReader.WireHeaderSize + NegotiateDecoder.BlockLength);
                if (NegotiateDecoder.TryDecode(fixedBlock, varData, out var negReq, out _, out _))
                {
                    sessionIdFromFrame = negReq.SessionId;
                    sessionVerIdFromFrame = negReq.SessionVerId;
                    decoded = true;
                }
            }
            else if (templateId == EntryPointFrameReader.TidEstablish
                && firstFrame.Length >= EntryPointFrameReader.WireHeaderSize + EstablishDecoder.BlockLength)
            {
                if (EstablishDecoder.TryDecode(
                    firstFrame.AsSpan(EntryPointFrameReader.WireHeaderSize, EstablishDecoder.BlockLength),
                    out var estReq, out _))
                {
                    sessionIdFromFrame = estReq.SessionId;
                    sessionVerIdFromFrame = estReq.SessionVerId;
                    isEstablish = true;
                    decoded = true;
                }
            }
            if (decoded && _persistedSessionStates.TryGetValue(sessionIdFromFrame, out var snap))
            {
                rehydrateState = snap;
                persistedMaxOrderRatePerSecond = _persistedMaxOrderRateResolver?.Invoke(snap.SessionId);
                // Only the Establish-with-matching-SessionVerId shape
                // should resume in Negotiated state. A Negotiate frame
                // (any SessionVerId) and an Establish with a different
                // SessionVerId both go through the normal handshake
                // gate — the latter will be rejected by EstablishValidator
                // as INVALID_SESSIONVERID, which is the correct
                // spec-defined response.
                resumeAsNegotiated = isEstablish && sessionVerIdFromFrame == snap.SessionVerId;
                _logger.LogInformation(
                    "rehydrating persisted session {SessionId} (verId={VerId} → peer verId={PeerVerId} firstFrame={Frame} resumeAsNegotiated={Resume}) from {Remote}",
                    snap.SessionId, snap.SessionVerId, sessionVerIdFromFrame,
                    isEstablish ? "Establish" : "Negotiate", resumeAsNegotiated, SafeRemote(sock));
            }
        }

        ConstructAndStartSession(stream, sock, firstFrame, persistedState: rehydrateState,
            resumeAsNegotiated: resumeAsNegotiated,
            persistedMaxOrderRatePerSecond: persistedMaxOrderRatePerSecond);
    }

    private void ConstructAndStartSession(NetworkStream stream, Socket sock, byte[]? firstFrame,
        B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot? persistedState,
        bool resumeAsNegotiated = false,
        int? persistedMaxOrderRatePerSecond = null)
    {
        var identity = _identityFactory(SafeRemote(sock));
        _logger.LogInformation("accepted connection {ConnectionId} from {Remote} sessionId={SessionId}",
            identity.ConnectionId, SafeRemote(sock), identity.SessionId);

        Action<FixpSession, string> onClosed = (s, reason) =>
        {
            _registry.Deregister(s);
            lock (_lock) _sessions.Remove(s);
            _onSessionClosed?.Invoke(s, reason);
        };

        // Issue #485: callback to re-index the session in the registry when
        // Identity changes from pending-{connId} to the stable FIXP SessionId.
        Action<FixpSession, B3.Exchange.Contracts.SessionId, B3.Exchange.Contracts.SessionId> onIdentityChanged =
            (s, oldId, newId) => _registry.UpdateIdentity(s, oldId, newId);

        Stream sessionStream = firstFrame is null ? stream : new PrependedStream(firstFrame, stream);
        var session = new FixpSession(identity.ConnectionId, identity.EnteringFirm, identity.SessionId,
            sessionStream, _sink, _loggerFactory.CreateLogger<FixpSession>(),
            options: _sessionOptions, onClosed: onClosed,
            negotiationValidator: _negotiationValidator,
            sessionClaims: _sessionClaims,
            establishValidator: _establishValidator,
            retransmitMetrics: _retransmitMetrics,
            outboundJournal: _outboundJournal,
            statePersister: _statePersister,
            persistedState: persistedState,
            resumeAsNegotiated: resumeAsNegotiated,
            persistedMaxOrderRatePerSecond: persistedMaxOrderRatePerSecond,
            onIdentityChanged: onIdentityChanged,
            onTakeOverRollback: s => _registry.Register(s));
        _registry.Register(session);
        lock (_lock) _sessions.Add(session);
        session.Start();
    }

    /// <summary>
    /// Reads exactly one FIXP frame (SOFH + SBE header + body) from
    /// <paramref name="stream"/>, returning the raw bytes (so they can be
    /// replayed via <see cref="PrependedStream"/>) and the parsed
    /// <c>templateId</c>. Throws <see cref="OperationCanceledException"/>
    /// on <paramref name="ct"/>.
    /// </summary>
    private static async Task<(byte[] frame, ushort templateId)> ReadFirstFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (!EntryPointFrameReader.TryParseInboundHeader(header, out var info, out _, out _))
        {
            // Either malformed or unsupported. Return the header anyway
            // so the downstream FixpSession can run its own decoder and
            // emit the proper FIXP error response (Terminate / etc.).
            return (header, info.TemplateId);
        }
        if (info.MessageLength <= EntryPointFrameReader.WireHeaderSize)
            return (header, info.TemplateId);

        var full = new byte[info.MessageLength];
        Buffer.BlockCopy(header, 0, full, 0, header.Length);
        await ReadExactAsync(stream, full.AsMemory(header.Length), ct).ConfigureAwait(false);
        return (full, info.TemplateId);
    }

    private static Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        => ReadExactAsync(stream, buffer.AsMemory(), ct);

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("peer closed before first frame completed");
            read += n;
        }
    }

    private static EndPoint? SafeRemote(Socket sock)
    {
        try { return sock.RemoteEndPoint; }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("entrypoint listener stopping");
        await StopAcceptingAsync().ConfigureAwait(false);
        FixpSession[] toClose;
        lock (_lock) { toClose = _sessions.ToArray(); _sessions.Clear(); }
        foreach (var s in toClose) await s.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
