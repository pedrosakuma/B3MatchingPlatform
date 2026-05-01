using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
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
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _reaperTask;
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
        EstablishValidator? establishValidator = null)
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
                catch (OperationCanceledException) { return; }
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

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket sock;
                try { sock = await _listener!.AcceptSocketAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                sock.NoDelay = true;
                // Per-connection task: parses the first frame to decide
                // re-attach (#69b-2) vs new session, with a hard timeout
                // (FirstFrameTimeoutMs) so a slowloris client cannot pin
                // an accept slot indefinitely. We deliberately do NOT
                // await this — accept must remain responsive.
                _ = Task.Run(() => HandleAcceptedConnectionAsync(sock, ct));
            }
        }
        catch (OperationCanceledException) { }
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
            ConstructAndStartSession(stream, sock, firstFrame: null);
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
        // held by a Suspended FixpSession with matching SessionVerId.
        if (templateId == EntryPointFrameReader.TidEstablish
            && firstFrame.Length >= EntryPointFrameReader.WireHeaderSize + EstablishDecoder.BlockLength
            && EstablishDecoder.TryDecode(
                firstFrame.AsSpan(EntryPointFrameReader.WireHeaderSize, EstablishDecoder.BlockLength),
                out var req, out _))
        {
            if (_sessionClaims.TryGetActiveClaim(req.SessionId, out var holder, out var lastVerId)
                && holder is FixpSession existing
                && existing.SessionVerId == req.SessionVerId
                && req.SessionVerId == lastVerId
                && existing.State == FixpState.Suspended)
            {
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

        ConstructAndStartSession(stream, sock, firstFrame);
    }

    private void ConstructAndStartSession(NetworkStream stream, Socket sock, byte[]? firstFrame)
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

        Stream sessionStream = firstFrame is null ? stream : new PrependedStream(firstFrame, stream);
        var session = new FixpSession(identity.ConnectionId, identity.EnteringFirm, identity.SessionId,
            sessionStream, _sink, _loggerFactory.CreateLogger<FixpSession>(),
            options: _sessionOptions, onClosed: onClosed,
            negotiationValidator: _negotiationValidator,
            sessionClaims: _sessionClaims,
            establishValidator: _establishValidator);
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
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_acceptTask != null) { try { await _acceptTask.ConfigureAwait(false); } catch { } }
        if (_reaperTask != null) { try { await _reaperTask.ConfigureAwait(false); } catch { } }
        FixpSession[] toClose;
        lock (_lock) { toClose = _sessions.ToArray(); _sessions.Clear(); }
        foreach (var s in toClose) await s.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
