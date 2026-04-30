using System.Net;
using B3.Exchange.EntryPoint;
using B3.Exchange.Instruments;
using B3.Exchange.Integration;
using B3.Exchange.Matching;

namespace B3.Exchange.Host;

/// <summary>
/// Wires the host together: per-channel <see cref="ChannelDispatcher"/>
/// (each owning a <see cref="MatchingEngine"/> + multicast publisher),
/// the <see cref="HostRouter"/> that dispatches inbound commands by
/// SecurityId, and the TCP <see cref="EntryPointListener"/>.
///
/// Lifetime: <see cref="StartAsync"/> binds sockets and begins accepting;
/// <see cref="StopAsync"/> cancels accept loop, drains channel dispatchers,
/// and disposes sockets. Designed to be driven from a Program.Main with
/// SIGTERM / Ctrl+C handling.
/// </summary>
public sealed class ExchangeHost : IAsyncDisposable
{
    private readonly HostConfig _config;
    private readonly Action<string>? _log;
    private readonly Func<ChannelConfig, IUmdfPacketSink>? _packetSinkFactory;
    private readonly List<ChannelDispatcher> _dispatchers = new();
    private readonly List<IDisposable> _ownedSinks = new();
    private readonly MetricsRegistry _metrics = new();
    private readonly StartupReadinessProbe _startupProbe = new("startup");
    private readonly List<IReadinessProbe> _probes = new();
    private EntryPointListener? _listener;
    private HostRouter? _router;
    private HttpServer? _http;

    public ExchangeHost(HostConfig config, Action<string>? log = null,
        Func<ChannelConfig, IUmdfPacketSink>? packetSinkFactory = null)
    {
        _config = config;
        _log = log;
        _packetSinkFactory = packetSinkFactory;
        _probes.Add(_startupProbe);
    }

    public IPEndPoint? TcpEndpoint => _listener?.LocalEndpoint;
    public IPEndPoint? HttpEndpoint => _http?.LocalEndpoint;
    public MetricsRegistry Metrics => _metrics;
    internal IReadOnlyList<ChannelDispatcher> Dispatchers => _dispatchers;

    /// <summary>
    /// Register an additional readiness probe. Intended for the snapshot
    /// rotator (#1) and instrument-definition publisher (#2) once they
    /// land — each subsystem registers its own probe so /health/ready
    /// only flips green when every dependency is satisfied.
    /// </summary>
    public void RegisterReadinessProbe(IReadinessProbe probe) => _probes.Add(probe);

    public async Task StartAsync()
    {
        var routing = new Dictionary<long, ChannelDispatcher>();
        foreach (var ch in _config.Channels)
        {
            var instruments = InstrumentLoader.LoadFromFile(ch.InstrumentsFile);
            IUmdfPacketSink sink;
            if (_packetSinkFactory != null)
            {
                sink = _packetSinkFactory(ch);
            }
            else
            {
                var local = ch.LocalInterface != null ? IPAddress.Parse(ch.LocalInterface) : null;
                sink = new MulticastUdpPacketSink(IPAddress.Parse(ch.IncrementalGroup), ch.IncrementalPort, local, ch.Ttl);
            }
            if (sink is IDisposable d) _ownedSinks.Add(d);
            var channelMetrics = _metrics.RegisterChannel(ch.ChannelNumber);
            var disp = new ChannelDispatcher(
                channelNumber: ch.ChannelNumber,
                engineFactory: s => new MatchingEngine(instruments, s),
                packetSink: sink,
                metrics: channelMetrics);
            disp.Start();
            _dispatchers.Add(disp);
            foreach (var inst in instruments)
            {
                if (routing.ContainsKey(inst.SecurityId))
                    throw new InvalidOperationException($"SecurityId {inst.SecurityId} mapped to multiple channels");
                routing.Add(inst.SecurityId, disp);
            }
            _log?.Invoke($"channel {ch.ChannelNumber}: {instruments.Count} instruments → {ch.IncrementalGroup}:{ch.IncrementalPort}");
        }

        _router = new HostRouter(routing);
        var listenEp = ParseEndpoint(_config.Tcp.Listen);
        _listener = new EntryPointListener(listenEp, _router,
            identityFactory: remote =>
            {
                var connectionId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
                return new EntryPointListener.AcceptedConnection(
                    ConnectionId: connectionId,
                    EnteringFirm: _config.Tcp.EnteringFirm,
                    SessionId: (uint)(connectionId & 0xFFFFFFFFu));
            });
        _listener.Start();
        _log?.Invoke($"listening on {_listener.LocalEndpoint}");

        _metrics.SetSessionProvider(new ListenerSessionProvider(_listener));

        if (_config.Http != null)
        {
            _http = new HttpServer(_config.Http, _metrics, _probes, _log);
            await _http.StartAsync().ConfigureAwait(false);
        }

        _startupProbe.MarkReady();
    }

    private sealed class ListenerSessionProvider : ISessionMetricsProvider
    {
        private readonly EntryPointListener _listener;
        public ListenerSessionProvider(EntryPointListener listener) { _listener = listener; }
        public IEnumerable<SessionQueueSample> Sample()
        {
            foreach (var s in _listener.ActiveSessions)
            {
                if (!s.IsOpen) continue;
                yield return new SessionQueueSample(
                    SessionId: "conn-" + s.ConnectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    QueueDepth: s.SendQueueDepth);
            }
        }
    }

    private static IPEndPoint ParseEndpoint(string s)
    {
        var idx = s.LastIndexOf(':');
        if (idx < 0) throw new FormatException($"expected host:port, got '{s}'");
        var host = s.Substring(0, idx);
        var port = int.Parse(s.Substring(idx + 1));
        return new IPEndPoint(IPAddress.Parse(host), port);
    }

    public async ValueTask DisposeAsync()
    {
        if (_http != null) await _http.DisposeAsync().ConfigureAwait(false);
        if (_listener != null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var d in _dispatchers) await d.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _ownedSinks) s.Dispose();
    }
}
