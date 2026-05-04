using System.Net;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Instruments;
using B3.Exchange.Core;
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
    private readonly ShutdownReadinessProbe _shutdownProbe = new("shutdown");
    private readonly List<IReadinessProbe> _probes = new();
    private readonly object _probesLock = new();
    private EntryPointListener? _listener;
    private HostRouter? _router;
    private HttpServer? _http;
    private DailyResetScheduler? _dailyReset;

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
        _probes.Add(_shutdownProbe);
    }

    public IPEndPoint? TcpEndpoint => _listener?.LocalEndpoint;
    public IPEndPoint? HttpEndpoint => _http?.LocalEndpoint;
    public MetricsRegistry Metrics => _metrics;
    public IReadOnlyList<ChannelDispatcher> Dispatchers => _dispatchers;

    /// <summary>
    /// On-demand trigger for the daily-rollover routine (#GAP-09 /
    /// issue #47). Returns the number of sessions that were terminated,
    /// or -1 if the host has not finished <see cref="StartAsync"/>.
    /// Safe to call from any thread.
    /// </summary>
    public int TriggerDailyReset(string reason = "operator-trigger")
    {
        var listener = _listener;
        if (listener is null) return -1;
        return listener.TerminateAllSessions(reason);
    }

    /// <summary>Firm + session credentials parsed from <c>HostConfig</c>.
    /// Built lazily on first access (or on <see cref="StartAsync"/>); empty
    /// when <c>firms[]</c>/<c>sessions[]</c> are not configured.</summary>
    public FirmRegistry FirmRegistry => _firmRegistry ??= BuildFirmRegistry();
    private FirmRegistry? _firmRegistry;

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
        var sessionRegistry = new SessionRegistry();
        var gatewayRouter = new GatewayRouter(sessionRegistry, _loggerFactory.CreateLogger<GatewayRouter>());

        var firmRegistry = FirmRegistry;
        var defaultSession = ResolveDefaultSession(firmRegistry);
        var routing = new Dictionary<long, ChannelDispatcher>();
        foreach (var ch in _config.Channels)
        {
            var instruments = InstrumentLoader.LoadFromFile(ch.InstrumentsFile);
            var channelMetrics = _metrics.RegisterChannel(ch.ChannelNumber);
            IUmdfPacketSink sink;
            if (_packetSinkFactory != null)
            {
                sink = _packetSinkFactory(ch);
            }
            else
            {
                sink = WrapResilient(
                    BuildUdpSink(ch.Transport, ch.IncrementalGroup, ch.IncrementalPort,
                        ch.LocalInterface, ch.Ttl),
                    channelMetrics);
            }
            if (sink is IDisposable d) _ownedSinks.Add(d);
            var engineLogger = _loggerFactory.CreateLogger<MatchingEngine>();

            // Wrap with chaos decorator if configured (issue #119). Off by
            // default; only activates when the operator opts in via config.
            if (ch.Chaos is { } chaosCfg)
            {
                var coreCfg = chaosCfg.ToCore();
                if (coreCfg.IsActive)
                {
                    var chaos = new ChaosUdpPacketSinkDecorator(
                        sink, coreCfg,
                        _loggerFactory.CreateLogger<ChaosUdpPacketSinkDecorator>(),
                        channelMetrics);
                    _ownedSinks.Add(chaos);
                    sink = chaos;
                }
            }

            // Capture the engine via a side-channel so we can build a snapshot
            // source that reads through the live book on the dispatcher thread.
            MatchingEngine? capturedEngine = null;
            // Issue #216 (L3a): build the UMDF retransmit ring per channel
            // unless the operator explicitly opted out via bufferSize=0.
            UmdfPacketRetransmitBuffer? retxBuffer = null;
            int retxCapacity = ch.UmdfRetransmit?.BufferSize ?? RetransmitBufferDefaults.UmdfRingCapacity;
            if (retxCapacity > 0)
            {
                retxBuffer = new UmdfPacketRetransmitBuffer(retxCapacity);
            }
            var disp = new ChannelDispatcher(
                channelNumber: ch.ChannelNumber,
                engineFactory: s =>
                {
                    var e = new MatchingEngine(instruments, s, engineLogger, ch.SelfTradePrevention);
                    capturedEngine = e;
                    return e;
                },
                packetSink: sink,
                outbound: gatewayRouter,
                logger: _loggerFactory.CreateLogger<ChannelDispatcher>(),
                metrics: channelMetrics,
                sessionFirmCounters: _metrics.SessionFirmMessages,
                retxBuffer: retxBuffer);
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
                    snapSink = WrapResilientCounting(
                        BuildUdpSink(ch.Transport, snap.Group, snap.Port,
                            ch.LocalInterface, snap.Ttl ?? ch.Ttl),
                        channelMetrics, UmdfFeedKind.Snapshot);
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
                    var localIface = idCfg.LocalInterface ?? ch.LocalInterface;
                    idSink = WrapResilientCounting(
                        BuildUdpSink(ch.Transport, idCfg.Group, idCfg.Port, localIface, idCfg.Ttl),
                        channelMetrics, UmdfFeedKind.InstrumentDef);
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

        _router = new HostRouter(routing, gatewayRouter, _loggerFactory.CreateLogger<HostRouter>());
        var listenEp = ParseEndpoint(_config.Tcp.Listen);
        var sessionOptions = new FixpSessionOptions
        {
            HeartbeatIntervalMs = _config.Tcp.HeartbeatIntervalMs,
            IdleTimeoutMs = _config.Tcp.IdleTimeoutMs,
            TestRequestGraceMs = _config.Tcp.TestRequestGraceMs,
            LifecycleMetrics = _metrics.Sessions,
            ThrottleTimeWindowMs = _config.Tcp.Throttle?.TimeWindowMs ?? 0,
            ThrottleMaxMessages = _config.Tcp.Throttle?.MaxMessages ?? 0,
            ThrottleMetrics = _config.Tcp.Throttle is not null ? _metrics.Throttle : null,
            OnTransportSendQueueFull = _metrics.Transport.IncSendQueueFull,
        };
        // Phase 2 (#42): real Negotiate handshake. The validator is pure
        // (no IO); the claim ledger lives for the host process lifetime
        // so duplicate-connection detection works across reconnects.
        var sessionClaims = new SessionClaimRegistry();
        var negotiationValidator = new NegotiationValidator(firmRegistry, sessionClaims, devMode: _config.Auth.DevMode);
        // Phase 2 (#43): Establish handshake validator. Pure (no IO);
        // tolerates ±5 minutes of clock skew on Establish.timestamp.
        var establishValidator = new EstablishValidator();
        // Legacy passthrough mode (RequireFixpHandshake=false) leaves both
        // session-layer validators unwired so app messages flow without a
        // prior Negotiate/Establish — used by the synthetic trader and
        // pre-#42 integration tests until they learn the handshake.
        bool requireHandshake = _config.Auth.RequireFixpHandshake;
        _listener = new EntryPointListener(listenEp, _router, sessionRegistry, _loggerFactory,
            identityFactory: remote =>
            {
                var connectionId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
                // Pre-Negotiate placeholder: firm/session are stamped from
                // a default and rewritten by FixpSession on a successful
                // Negotiate. SessionId here is just a unique pre-handshake
                // tag for log correlation.
                var enteringFirm = defaultSession.firmCode;
                return new EntryPointListener.AcceptedConnection(
                    ConnectionId: connectionId,
                    EnteringFirm: enteringFirm,
                    SessionId: (uint)(connectionId & 0xFFFFFFFFu));
            },
            sessionOptions: sessionOptions,
            onSessionClosed: (s, reason) => _logger.LogInformation("session {ConnectionId} closed: {Reason}", s.ConnectionId, reason),
            negotiationValidator: requireHandshake ? negotiationValidator : null,
            sessionClaims: requireHandshake ? sessionClaims : null,
            establishValidator: requireHandshake ? establishValidator : null);
        _listener.Start();
        _logger.LogInformation("entrypoint listening on {Endpoint}", _listener.LocalEndpoint);

        var sessionProvider = new ListenerSessionProvider(_listener, firmRegistry);
        _metrics.SetSessionProvider(sessionProvider);

        if (_config.Http != null)
        {
            IReadOnlyList<IReadinessProbe> probeSnapshot;
            lock (_probesLock) probeSnapshot = _probes.ToList();
            var dispatchersByChannel = _dispatchers.ToDictionary(d => d.ChannelNumber);
            // Snapshot firms once at startup (FirmRegistry is immutable
            // after build) and expose them as plain DTOs to HttpServer
            // to avoid leaking Gateway types into the operator surface.
            var firmInfos = firmRegistry.Firms.Values
                .Select(f => new FirmInfo(f.Id, f.Name, f.EnteringFirmCode))
                .ToList();
            // Reuse the SAME provider instance that's already wired into
            // MetricsRegistry so /sessions and /metrics observe exactly
            // the same snapshot of live sessions on each scrape.
            _http = new HttpServer(_config.Http, _metrics, probeSnapshot, dispatchersByChannel,
                msg => _logger.LogInformation("{Message}", msg),
                sessionsProvider: sessionProvider.Sample,
                firms: firmInfos,
                dailyResetTrigger: () => TriggerDailyReset("http-trigger"));
            await _http.StartAsync().ConfigureAwait(false);
        }

        // #GAP-09 (#47): daily trading-day rollover. The listener exists
        // by this point so it is safe to capture it for the timer
        // callback. Always terminate via the public Trigger so the
        // scheduler logs and re-arms even if the action throws.
        if (_config.DailyReset is { Enabled: true } drCfg)
        {
            _dailyReset = new DailyResetScheduler(
                drCfg.Schedule,
                drCfg.Timezone,
                action: () => _listener?.TerminateAllSessions("daily-reset"),
                logger: _loggerFactory.CreateLogger<DailyResetScheduler>());
            _dailyReset.Start();
        }

        _startupProbe.MarkReady();
    }

    private sealed class ListenerSessionProvider : ISessionMetricsProvider
    {
        private readonly EntryPointListener _listener;
        private readonly FirmRegistry _firms;
        public ListenerSessionProvider(EntryPointListener listener, FirmRegistry firms)
        {
            _listener = listener;
            _firms = firms;
        }
        public IEnumerable<SessionDiagnostics> Sample()
        {
            // Build a one-shot reverse index (wire EnteringFirm code → firm Id).
            // Cheap to rebuild each scrape since firm count is small (a handful)
            // and FirmRegistry is immutable.
            var byCode = new Dictionary<uint, string>();
            foreach (var f in _firms.Firms.Values)
                byCode[f.EnteringFirmCode] = f.Id;

            foreach (var s in _listener.ActiveSessions)
            {
                if (!s.IsRegistered) continue;
                var firmId = byCode.TryGetValue(s.EnteringFirm, out var id) ? id : "unknown";
                yield return new SessionDiagnostics(
                    SessionId: "conn-" + s.ConnectionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FirmId: firmId,
                    State: (int)s.State,
                    SessionVerId: s.SessionVerId,
                    OutboundSeq: s.OutboundSeq,
                    InboundExpectedSeq: s.LastIncomingSeqNo,
                    RetxBufferDepth: s.RetxBufferDepth,
                    SendQueueDepth: s.SendQueueDepth,
                    AttachedTransportId: s.AttachedTransportId,
                    LastActivityAtMs: s.LastActivityAtMs);
            }
        }
    }

    /// <summary>
    /// Issue #172 — wrap any UDP-backed sink with the resilient decorator
    /// so transient publish failures (NIC down, route lost, MTU mismatch)
    /// are caught + counted via <c>exch_umdf_publish_errors_total</c>
    /// instead of propagating into the dispatcher loop and aborting the
    /// channel. Test paths injecting their own sink via
    /// <c>packetSinkFactory</c> are deliberately not wrapped — tests may
    /// want raw access to assert wire-level behaviour.
    /// </summary>
    private IUmdfPacketSink WrapResilient(IUmdfPacketSink inner, ChannelMetrics metrics)
        => new ResilientUdpPacketSinkDecorator(
            inner,
            _loggerFactory.CreateLogger<ResilientUdpPacketSinkDecorator>(),
            metrics);

    /// <summary>
    /// Issue #174: wraps an outbound packet sink with the resilient error
    /// decorator and a counting decorator for the named feed. The
    /// incremental feed is counted directly by <c>ChannelDispatcher</c>,
    /// so this helper is only used for snapshot/instrument-def feeds.
    /// </summary>
    private IUmdfPacketSink WrapResilientCounting(IUmdfPacketSink inner, ChannelMetrics metrics, UmdfFeedKind feed)
        => new CountingUdpPacketSinkDecorator(WrapResilient(inner, metrics), metrics, feed);

    private IUmdfPacketSink BuildUdpSink(UmdfTransport transport, string host, int port,
        string? localInterface, byte ttl)
    {
        if (transport == UmdfTransport.Unicast)
        {
            // Unicast mode is bridge-network friendly: `host` is treated as
            // a DNS name (or IP literal) and resolved on construction. The
            // multicast-only options (TTL, local interface) are ignored —
            // log a warning if the operator set them so they don't expect
            // multicast routing semantics.
            if (localInterface is not null)
                _logger.LogWarning("transport=unicast: ignoring localInterface='{Iface}' (multicast-only)", localInterface);
            return new UnicastUdpPacketSink(host, port,
                _loggerFactory.CreateLogger<UnicastUdpPacketSink>());
        }
        var local = localInterface != null ? IPAddress.Parse(localInterface) : null;
        return new MulticastUdpPacketSink(IPAddress.Parse(host), port,
            _loggerFactory.CreateLogger<MulticastUdpPacketSink>(), local, ttl);
    }

    private FirmRegistry BuildFirmRegistry()
    {
        var firms = _config.Firms.Select(fc =>
        {
            if (string.IsNullOrWhiteSpace(fc.Id))
                throw new InvalidOperationException("HostConfig.firms[].id must be non-empty");
            if (fc.EnteringFirmCode == 0)
                throw new InvalidOperationException(
                    $"HostConfig.firms['{fc.Id}'].enteringFirmCode must be > 0");
            return new Firm(fc.Id, string.IsNullOrWhiteSpace(fc.Name) ? fc.Id : fc.Name, fc.EnteringFirmCode);
        });

        var sessions = _config.Sessions.Select(sc =>
        {
            var policy = sc.Policy is null
                ? SessionPolicy.Default
                : new SessionPolicy(
                    ThrottleMessagesPerSecond: sc.Policy.ThrottleMessagesPerSecond,
                    KeepAliveIntervalMs: sc.Policy.KeepAliveIntervalMs,
                    IdleTimeoutMs: sc.Policy.IdleTimeoutMs,
                    TestRequestGraceMs: sc.Policy.TestRequestGraceMs,
                    RetransmitBufferSize: sc.Policy.RetransmitBufferSize);
            return new SessionCredential(
                SessionId: sc.SessionId,
                FirmId: sc.FirmId,
                AccessKey: sc.AccessKey ?? "",
                AllowedSourceCidrs: sc.AllowedSourceCidrs,
                Policy: policy);
        });

        var registry = new FirmRegistry(firms, sessions);

        if (_config.Auth.DevMode &&
            registry.Credentials.Values.Any(c => !string.IsNullOrEmpty(c.AccessKey)))
        {
            _logger.LogWarning(
                "auth.devMode=true but {Count} session(s) declare a non-empty accessKey; access keys will be ignored",
                registry.Credentials.Values.Count(c => !string.IsNullOrEmpty(c.AccessKey)));
        }

        return registry;
    }

    private (string sessionId, uint firmCode) ResolveDefaultSession(FirmRegistry registry)
    {
        if (registry.Credentials.Count > 0)
        {
            // Stable: pick the lexicographically-first session id so the
            // selection is deterministic regardless of YAML/JSON ordering
            // quirks. #42 will replace this with peer-claimed sessionID.
            var sid = registry.Credentials.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            var firm = registry.FirmOf(sid)
                ?? throw new InvalidOperationException($"BUG: session '{sid}' has no resolved firm");
            _logger.LogInformation(
                "pre-#42 default session: '{SessionId}' firm '{FirmId}' enteringFirmCode={EnteringFirmCode}",
                sid, firm.Id, firm.EnteringFirmCode);
            return (sid, firm.EnteringFirmCode);
        }

        // Backwards compatibility: pre-#67 single-tenant config.
        _logger.LogWarning(
            "HostConfig.firms[]/sessions[] is empty; falling back to deprecated tcp.enteringFirm={EnteringFirm}. " +
            "Update your config to declare firms[] and sessions[] per docs/B3-ENTRYPOINT-ARCHITECTURE.md §8.",
            _config.Tcp.EnteringFirm);
        return ("legacy", _config.Tcp.EnteringFirm);
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
        await StopAsync().ConfigureAwait(false);
        if (_dailyReset != null) await _dailyReset.DisposeAsync().ConfigureAwait(false);
        if (_http != null) await _http.DisposeAsync().ConfigureAwait(false);
        foreach (var t in _snapshotTimers) await t.DisposeAsync().ConfigureAwait(false);
        if (_listener != null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _instrumentDefPublishers) await p.DisposeAsync().ConfigureAwait(false);
        foreach (var d in _dispatchers) await d.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _ownedSinks) s.Dispose();
    }

    private int _stopCalled;

    /// <summary>
    /// Graceful shutdown (issue #171 / A7). Drives the host through the
    /// phases the operability spec requires:
    ///
    /// <list type="number">
    ///   <item>Flip the shutdown readiness probe → NOT_READY so /health/ready
    ///         returns 503 and load balancers stop routing traffic to us.</item>
    ///   <item>Stop the EntryPoint accept loop (no new TCP connections).</item>
    ///   <item>Wait up to <c>HostConfig.Shutdown.DrainGraceMs</c> for every
    ///         per-channel inbound queue to drain so in-flight work is
    ///         observed by the engine before we close anything.</item>
    ///   <item>Broadcast <c>Terminate(Finished=1)</c> to every live FIXP
    ///         session so clients see an orderly drain instead of an RST/timeout.</item>
    ///   <item>Dispose dispatchers (each flushes its last UMDF packet on
    ///         the way down) and the listener.</item>
    /// </list>
    ///
    /// <para>Each phase logs <c>shutdown phase=X duration=Yms</c>. Idempotent —
    /// only the first caller does work; subsequent callers return immediately.
    /// <see cref="DisposeAsync"/> calls this; callers driving shutdown
    /// from a SIGTERM handler should call it explicitly so they can pass
    /// a <see cref="CancellationToken"/> bounding the total duration.</para>
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _stopCalled, 1) == 1) return;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("graceful shutdown starting");

        // Phase 1: flip readiness so external probes (LB, k8s) stop sending traffic.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _shutdownProbe.MarkNotReady();
        sw.Stop();
        _logger.LogInformation("shutdown phase=mark-not-ready duration={DurationMs}ms", sw.ElapsedMilliseconds);

        // Phase 2: stop accepting new connections; existing sessions stay alive.
        sw.Restart();
        if (_listener != null)
        {
            try { await _listener.StopAcceptingAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "stop-accepting threw"); }
        }
        sw.Stop();
        _logger.LogInformation("shutdown phase=stop-accepting duration={DurationMs}ms", sw.ElapsedMilliseconds);

        // Phase 3: poll-wait for per-channel inbound queues to drain so the
        // engine has observed every command currently held by the gateway.
        sw.Restart();
        int graceMs = Math.Max(0, _config.Shutdown.DrainGraceMs);
        int pollMs = Math.Max(1, _config.Shutdown.DrainPollMs);
        var drainDeadline = System.Diagnostics.Stopwatch.StartNew();
        int residual;
        while (true)
        {
            residual = 0;
            foreach (var d in _dispatchers) residual += d.InboundQueueDepth;
            if (residual == 0) break;
            if (drainDeadline.ElapsedMilliseconds >= graceMs) break;
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(pollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        sw.Stop();
        if (residual == 0)
        {
            _logger.LogInformation("shutdown phase=drain-inbound duration={DurationMs}ms residual=0",
                sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning("shutdown phase=drain-inbound duration={DurationMs}ms residual={Residual} (grace expired)",
                sw.ElapsedMilliseconds, residual);
        }

        // Phase 4: broadcast Terminate(Finished) and close each session.
        sw.Restart();
        int terminated = 0;
        if (_listener != null)
        {
            try
            {
                terminated = await _listener.TerminateAllSessionsAsync(
                    SessionRejectEncoder.TerminationCode.Finished, "graceful-shutdown")
                    .ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "terminate-all threw"); }
        }
        sw.Stop();
        _logger.LogInformation("shutdown phase=terminate-sessions duration={DurationMs}ms count={Count}",
            sw.ElapsedMilliseconds, terminated);

        // Phase 5: stop snapshot/instrument-def cadence so we don't emit
        // packets after dispatchers tear their sinks down.
        sw.Restart();
        foreach (var t in _snapshotTimers)
        {
            try { await t.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _snapshotTimers.Clear();
        foreach (var p in _instrumentDefPublishers)
        {
            try { await p.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _instrumentDefPublishers.Clear();
        sw.Stop();
        _logger.LogInformation("shutdown phase=stop-publishers duration={DurationMs}ms", sw.ElapsedMilliseconds);

        // Phase 6: dispose dispatchers (each flushes its last UMDF packet).
        sw.Restart();
        foreach (var d in _dispatchers)
        {
            try { await d.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            { _logger.LogWarning(ex, "dispatcher dispose threw"); }
        }
        _dispatchers.Clear();
        sw.Stop();
        _logger.LogInformation("shutdown phase=close-dispatchers duration={DurationMs}ms", sw.ElapsedMilliseconds);

        totalSw.Stop();
        _logger.LogInformation("graceful shutdown complete totalDuration={TotalMs}ms", totalSw.ElapsedMilliseconds);
    }
}
