using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.EntryPoint;

/// <summary>
/// TCP accept loop. Each accepted socket is wrapped in a
/// <see cref="NetworkStream"/> + <see cref="EntryPointSession"/>. The caller
/// supplies a factory to assign per-connection identity (ConnectionId,
/// EnteringFirm, SessionId), so the listener doesn't need to know about
/// authentication or firm-mapping policy.
/// </summary>
public sealed class EntryPointListener : IAsyncDisposable
{
    public readonly record struct AcceptedConnection(long ConnectionId, uint EnteringFirm, uint SessionId);

    private readonly IPEndPoint _endpoint;
    private readonly IInboundCommandSink _sink;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EntryPointListener> _logger;
    private readonly Func<EndPoint?, AcceptedConnection> _identityFactory;
    private readonly EntryPointSessionOptions _sessionOptions;
    private readonly Action<EntryPointSession, string>? _onSessionClosed;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private long _nextConnectionId;
    private readonly List<EntryPointSession> _sessions = new();
    private readonly object _lock = new();

    public IPEndPoint? LocalEndpoint => (IPEndPoint?)_listener?.LocalEndpoint;

    /// <summary>
    /// Snapshot of currently active sessions for diagnostics / metrics.
    /// Closed sessions remain in the list until <see cref="DisposeAsync"/>
    /// runs; callers should filter on <see cref="EntryPointSession.IsOpen"/>
    /// if they only want live ones.
    /// </summary>
    public IReadOnlyList<EntryPointSession> ActiveSessions
    {
        get { lock (_lock) return _sessions.ToArray(); }
    }

    public EntryPointListener(IPEndPoint endpoint, IInboundCommandSink sink,
        ILoggerFactory loggerFactory,
        Func<EndPoint?, AcceptedConnection>? identityFactory = null,
        EntryPointSessionOptions? sessionOptions = null,
        Action<EntryPointSession, string>? onSessionClosed = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _endpoint = endpoint;
        _sink = sink;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EntryPointListener>();
        _identityFactory = identityFactory ?? DefaultIdentityFactory;
        _sessionOptions = sessionOptions ?? EntryPointSessionOptions.Default;
        _sessionOptions.Validate();
        _onSessionClosed = onSessionClosed;
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
                var identity = _identityFactory(sock.RemoteEndPoint);
                _logger.LogInformation("accepted connection {ConnectionId} from {Remote} sessionId={SessionId}",
                    identity.ConnectionId, sock.RemoteEndPoint, identity.SessionId);
                var stream = new NetworkStream(sock, ownsSocket: true);

                // Wrap onClosed to remove session from _sessions (so the
                // disconnected EntryPointSession + its NetworkStream/Socket
                // become GC-eligible) before invoking the external callback.
                Action<EntryPointSession, string> onClosed = (s, reason) =>
                {
                    lock (_lock) _sessions.Remove(s);
                    _onSessionClosed?.Invoke(s, reason);
                };

                var session = new EntryPointSession(identity.ConnectionId, identity.EnteringFirm, identity.SessionId,
                    stream, _sink, _loggerFactory.CreateLogger<EntryPointSession>(),
                    options: _sessionOptions, onClosed: onClosed);
                lock (_lock) _sessions.Add(session);
                session.Start();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "entrypoint accept loop terminated unexpectedly");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("entrypoint listener stopping");
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_acceptTask != null) { try { await _acceptTask.ConfigureAwait(false); } catch { } }
        EntryPointSession[] toClose;
        lock (_lock) { toClose = _sessions.ToArray(); _sessions.Clear(); }
        foreach (var s in toClose) await s.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
