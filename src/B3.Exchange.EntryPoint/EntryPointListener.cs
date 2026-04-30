using System.Net;
using System.Net.Sockets;

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
    private readonly IEntryPointEngineSink _sink;
    private readonly Func<EndPoint?, AcceptedConnection> _identityFactory;
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

    public EntryPointListener(IPEndPoint endpoint, IEntryPointEngineSink sink,
        Func<EndPoint?, AcceptedConnection>? identityFactory = null)
    {
        _endpoint = endpoint;
        _sink = sink;
        _identityFactory = identityFactory ?? DefaultIdentityFactory;
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
                var stream = new NetworkStream(sock, ownsSocket: true);
                var session = new EntryPointSession(identity.ConnectionId, identity.EnteringFirm, identity.SessionId, stream, _sink);
                lock (_lock) _sessions.Add(session);
                session.Start();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_acceptTask != null) { try { await _acceptTask.ConfigureAwait(false); } catch { } }
        EntryPointSession[] toClose;
        lock (_lock) { toClose = _sessions.ToArray(); _sessions.Clear(); }
        foreach (var s in toClose) await s.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
