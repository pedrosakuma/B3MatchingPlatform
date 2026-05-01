using System.Net;
using B3.Exchange.EntryPoint;
using B3.Exchange.Instruments;
using B3.Exchange.Integration;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ExchangeHost> _logger;
    private readonly Func<ChannelConfig, IUmdfPacketSink>? _packetSinkFactory;
    private readonly Func<ChannelConfig, SnapshotChannelConfig, IUmdfPacketSink>? _snapshotSinkFactory;
    private readonly Func<ChannelConfig, InstrumentDefinitionConfig, IUmdfPacketSink>? _instrumentDefSinkFactory;
    private readonly List<ChannelDispatcher> _dispatchers = new();
    private readonly List<InstrumentDefinitionPublisher> _instrumentDefPublishers = new();
    private readonly List<IDisposable> _ownedSinks = new();
    private readonly List<Timer> _snapshotTimers = new();
    private readonly MetricsRegistry _metrics = new();
    private readonly StartupReadinessProbe _startupProbe = new("startup");
    private readonly List<IReadinessProbe> _probes = new();
    private readonly object _probesLock = new();
    private EntryPointListener? _listener;
    private HostRouter? _router;
    private HttpServer? _http;

    public ExchangeHost(HostConfig config, ILoggerFactory? loggerFactory = null,
        Func<ChannelConfig, IUmdfPacketSink>? packetSinkFactory = null,
        Func<ChannelConfig, SnapshotChannelConfig, IUmdfPacketSink>? snapshotSinkFactory = null,
        Func<ChannelConfig, InstrumentDefinitionConfig, IUmdfPacketSink>? instrumentDefSinkFactory = null)
    {
        _config = config;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ExchangeHost>();
        _packetSinkFactory = packetSinkFactory;
        _snapshotSinkFactory = snapshotSinkFactory;
        _instrumentDefSinkFactory = instrumentDefSinkFactory;
        _probes.Add(_startupProbe);
    }

    public IPEndPoint? TcpEndpoint => _listener?.LocalEndpoint;
    public IPEndPoint? HttpEndpoint => _http?.LocalEndpoint;
    public MetricsRegistry Metrics => _metrics;
    public IReadOnlyList<ChannelDispatcher> Dispatchers => _dispatchers;

    /// <summary>Snapshot of the InstrumentDef publishers, primarily for tests.</summary>
    public IReadOnlyList<InstrumentDefinitionPublisher> InstrumentDefinitionPublishers => _instrumentDefPublishers;

    /// <summary>
    /// Register an additional readiness probe. Intended for the snapshot
    /// rotator (#1) and instrument-definition publisher (#2) once they
    /// land — each subsystem registers its own probe so /health/ready
    /// only flips green when every dependency is satisfied.
    /// Must be called before <see cref="StartAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if called after <see cref="StartAsync"/>.</exception>
    public void RegisterReadinessProbe(IReadinessProbe probe)
    {
        if (_http != null)
            throw new InvalidOperationException("Cannot register readiness probes after StartAsync has been called; probes are snapshotted at startup.");
        lock (_probesLock) _probes.Add(probe);
    }

    public async Task StartAsync()
    {
        _logger.LogInformation("exchange host starting with {ChannelCount} channels", _config.Channels.Count);
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
                sink = new MulticastUdpPacketSink(IPAddress.Parse(ch.IncrementalGroup), ch.IncrementalPort,
                    _loggerFactory.CreateLogger<MulticastUdpPacketSink>(), local, ch.Ttl);
            }
            if (sink is IDisposable d) _ownedSinks.Add(d);
            var engineLogger = _loggerFactory.CreateLogger<MatchingEngine>();
            var channelMetrics = _metrics.RegisterChannel(ch.ChannelNumber);

            // Capture the engine via a side-channel so we can build a snapshot
            // source that reads through the live book on the dispatcher thread.
            MatchingEngine? capturedEngine = null;
            var disp = new ChannelDispatcher(
                channelNumber: ch.ChannelNumber,
                engineFactory: s =>
                {
                    var e = new MatchingEngine(instruments, s, engineLogger, ch.SelfTradePrevention);
                    capturedEngine = e;
                    return e;
                },
                packetSink: sink,
                logger: _loggerFactory.CreateLogger<ChannelDispatcher>(),
                metrics: channelMetrics);
            disp.Start();
            _dispatchers.Add(disp);
            foreach (var inst in instruments)
            {
                if (routing.ContainsKey(inst.SecurityId))
                    throw new InvalidOperationException($"SecurityId {inst.SecurityId} mapped to multiple channels");
                routing.Add(inst.SecurityId, disp);
            }
            _logger.LogInformation("channel {ChannelNumber}: {InstrumentCount} instruments → {Group}:{Port}",
                ch.ChannelNumber, instruments.Count, ch.IncrementalGroup, ch.IncrementalPort);

            if (ch.Snapshot != null)
            {
                var snap = ch.Snapshot;
                IUmdfPacketSink snapSink;
                if (_snapshotSinkFactory != null)
                {
                    snapSink = _snapshotSinkFactory(ch, snap);
                }
                else
                {
                    var local = ch.LocalInterface != null ? IPAddress.Parse(ch.LocalInterface) : null;
                    snapSink = new MulticastUdpPacketSink(IPAddress.Parse(snap.Group), snap.Port,
                        _loggerFactory.CreateLogger<MulticastUdpPacketSink>(), local, snap.Ttl ?? ch.Ttl);
                }
                if (snapSink is IDisposable sd) _ownedSinks.Add(sd);

                var ids = instruments.Select(i => i.SecurityId).ToArray();
                var source = new MatchingEngineSnapshotSource(capturedEngine!, ids);
                int chunkCap = snap.MaxEntriesPerChunk ?? 30;
                var rotator = new SnapshotRotator(
                    channelNumber: ch.ChannelNumber,
                    source: source,
                    sink: snapSink,
                    maxEntriesPerChunk: chunkCap);
                disp.AttachSnapshotRotator(rotator);

                var cadence = TimeSpan.FromMilliseconds(Math.Max(50, snap.CadenceMs));
                var capturedDisp = disp;
                var timer = new Timer(_ => capturedDisp.EnqueueSnapshotTick(), null, cadence, cadence);
                _snapshotTimers.Add(timer);
                _logger.LogInformation("channel {ChannelNumber}: snapshot → {Group}:{Port} every {CadenceMs:n0}ms",
                    ch.ChannelNumber, snap.Group, snap.Port, cadence.TotalMilliseconds);
            }

            if (ch.InstrumentDefinition is { } idCfg)
            {
                IUmdfPacketSink idSink;
                if (_instrumentDefSinkFactory != null)
                {
                    idSink = _instrumentDefSinkFactory(ch, idCfg);
                }
                else
                {
                    var local = idCfg.LocalInterface != null
                        ? IPAddress.Parse(idCfg.LocalInterface)
                        : (ch.LocalInterface != null ? IPAddress.Parse(ch.LocalInterface) : null);
                    idSink = new MulticastUdpPacketSink(IPAddress.Parse(idCfg.Group), idCfg.Port,
                        _loggerFactory.CreateLogger<MulticastUdpPacketSink>(), local, idCfg.Ttl);
                }
                if (idSink is IDisposable idd) _ownedSinks.Add(idd);
                byte idChan = idCfg.ChannelNumber == 0 ? ch.ChannelNumber : idCfg.ChannelNumber;
                var publisher = new InstrumentDefinitionPublisher(
                    channelNumber: idChan,
                    instruments: instruments,
                    sink: idSink,
                    cadence: TimeSpan.FromMilliseconds(idCfg.CadenceMs));
                publisher.Start();
                _instrumentDefPublishers.Add(publisher);
                _logger.LogInformation("channel {ChannelNumber}: instrument-def → {Group}:{Port} every {CadenceMs}ms",
                    ch.ChannelNumber, idCfg.Group, idCfg.Port, idCfg.CadenceMs);
            }
        }

        _router = new HostRouter(routing, _loggerFactory.CreateLogger<HostRouter>());
        var listenEp = ParseEndpoint(_config.Tcp.Listen);
        var sessionOptions = new EntryPointSessionOptions
        {
            HeartbeatIntervalMs = _config.Tcp.HeartbeatIntervalMs,
            IdleTimeoutMs = _config.Tcp.IdleTimeoutMs,
            TestRequestGraceMs = _config.Tcp.TestRequestGraceMs,
        };
        _listener = new EntryPointListener(listenEp, _router, _loggerFactory,
            identityFactory: remote =>
            {
                var connectionId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
                return new EntryPointListener.AcceptedConnection(
                    ConnectionId: connectionId,
                    EnteringFirm: _config.Tcp.EnteringFirm,
                    SessionId: (uint)(connectionId & 0xFFFFFFFFu));
            },
            sessionOptions: sessionOptions,
            onSessionClosed: (s, reason) => _logger.LogInformation("session {ConnectionId} closed: {Reason}", s.ConnectionId, reason));
        _listener.Start();
        _logger.LogInformation("entrypoint listening on {Endpoint}", _listener.LocalEndpoint);

        _metrics.SetSessionProvider(new ListenerSessionProvider(_listener));

        if (_config.Http != null)
        {
            IReadOnlyList<IReadinessProbe> probeSnapshot;
            lock (_probesLock) probeSnapshot = _probes.ToList();
            _http = new HttpServer(_config.Http, _metrics, probeSnapshot,
                msg => _logger.LogInformation("{Message}", msg));
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
        _logger.LogInformation("exchange host shutting down");
        if (_http != null) await _http.DisposeAsync().ConfigureAwait(false);
        foreach (var t in _snapshotTimers) await t.DisposeAsync().ConfigureAwait(false);
        if (_listener != null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _instrumentDefPublishers) await p.DisposeAsync().ConfigureAwait(false);
        foreach (var d in _dispatchers) await d.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _ownedSinks) s.Dispose();
    }
}
