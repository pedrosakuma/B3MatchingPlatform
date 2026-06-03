using System.Net;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Instruments;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
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
    private readonly Dictionary<byte, IChannelStatePersister> _persistersByChannel = new();
    private readonly Dictionary<byte, IChannelWriteAheadLog> _walsByChannel = new();
    private readonly List<InstrumentDefinitionPublisher> _instrumentDefPublishers = new();
    private readonly List<IDisposable> _ownedSinks = new();
    private readonly List<Timer> _snapshotTimers = new();
    private readonly List<Timer> _priceBandTimers = new();
    private readonly List<B3.Exchange.PostTrade.FileAuditLogWriter> _auditWriters = new();
    private readonly List<Timer> _auditRetentionTimers = new();
    private readonly Dictionary<byte, EodExportContext> _eodExportByChannel = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(byte Channel, DateOnly Date), byte> _eodExportInFlight = new();
    private readonly MetricsRegistry _metrics = new();
    private readonly StartupReadinessProbe _startupProbe = new("startup");
    private readonly ShutdownReadinessProbe _shutdownProbe = new("shutdown");
    private readonly List<IReadinessProbe> _probes = new();
    private readonly object _probesLock = new();
    private EntryPointListener? _listener;
    private HostRouter? _router;
    private HttpServer? _http;
    private DailyResetScheduler? _dailyReset;
    private PhaseScheduler? _phaseScheduler;
    private OptionExpirySweeper? _optionExpirySweeper;
    private GtdExpirySweeper? _gtdExpirySweeper;
    private readonly List<B3.Exchange.Persistence.DataDirLock> _dataDirLocks = new();
    private B3.Exchange.Gateway.Persistence.FileFixpOutboundJournal? _outboundJournal;
    private B3.Exchange.Gateway.Persistence.FileFixpSessionStatePersister? _statePersister;

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
    public int TriggerDailyReset(string reason = "operator-trigger", CloseKind closeKind = CloseKind.DailyReset)
    {
        // OPT-03 / ADR 0014: sweep expired option series BEFORE the
        // listener tears down. Each per-order ER_Cancel from the
        // sweep's MassCancel must route back to its originating TCP
        // session via the OrderRegistry → IEntryPointResponseChannel
        // path — that path is only live while the gateway sessions
        // are still attached.
        //
        // SweepExpiredSeries blocks on a per-channel
        // TaskCompletionSource until every enqueued ExpireSecurity
        // work item has been processed end-to-end (MassCancel +
        // SetTradingPhase Close + UMDF packet flushed + ER_Cancel
        // frames handed to the outbound encoder). That ordering
        // guarantees the resting orders' cancels reach the wire
        // before TerminateAllSessions closes the sockets.
        _optionExpirySweeper?.SweepExpiredSeries(reason, waitTimeout: TimeSpan.FromSeconds(15));
        // GAP-23 / #499: sweep expired GTD orders BEFORE the listener tears
        // down, for the same routing reason as the option sweep above — each
        // per-order ER_Cancel must reach its originating TCP session while
        // the gateway is still attached. Runs after the option sweep so any
        // GTD order on an expiring option series is already gone (no-op
        // here); waits on per-channel completions so the cancels reach the
        // outbound encoder before DrainAllSessionsOutbound + TerminateAllSessions.
        _gtdExpirySweeper?.Sweep(reason, waitTimeout: TimeSpan.FromSeconds(15));
        // Issue #487: drain outbound queues BEFORE closing sockets.
        // ER_Cancel frames from the sweep are enqueued to the session's
        // send queue but may not have been written to the socket yet.
        // Without this drain, TerminateAllSessions can close the socket
        // while frames are still pending, causing clients to see EOF
        // instead of the ER_Cancel.
        _listener?.DrainAllSessionsOutboundAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var listener = _listener;
        int terminated = listener is null ? -1 : listener.TerminateAllSessions(reason, closeKind);
        // Issue #330 PR-3 (review BLOCKING): drain per-channel inbound
        // queues before reading yesterday's audit log. TerminateAllSessions
        // only closes live FIXP transports; commands already decoded and
        // enqueued on the dispatcher Channel keep flowing through
        // ProcessOne → _postTradeSink.OnTrade after this call returns. If
        // a fill with TransactTime in yesterday's UTC window is still in
        // flight when EodFillsExporter starts reading, we'd export an
        // incomplete CSV. Mirror the graceful-shutdown phase-3 drain
        // (ExchangeHost.StopAsync) but keep it synchronous so the
        // /admin/daily-reset response stays simple.
        DrainDispatcherInboundForRollover(reason);
        // Issue #330 PR-3: chain the EOD fills export for every channel
        // with eodDropDir configured (yesterday UTC). Failures are
        // logged and swallowed so a CSV problem on one channel never
        // hides the listener-termination count from the operator.
        TriggerEodExportForAllChannels(reason);
        return terminated;
    }

    private void DrainDispatcherInboundForRollover(string reason)
    {
        if (_dispatchers.Count == 0) return;
        int graceMs = Math.Max(0, _config.Shutdown.DrainGraceMs);
        int pollMs = Math.Max(1, _config.Shutdown.DrainPollMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int residual = 0;
        while (true)
        {
            residual = 0;
            foreach (var d in _dispatchers) residual += d.InboundQueueDepth;
            if (residual == 0) break;
            if (sw.ElapsedMilliseconds >= graceMs) break;
            Thread.Sleep(pollMs);
        }
        if (residual == 0)
        {
            _logger.LogInformation(
                "daily-reset ({Reason}): drain phase=ok duration={DurationMs}ms",
                reason, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "daily-reset ({Reason}): drain phase=grace-expired duration={DurationMs}ms residual={Residual} — EOD export may miss in-flight fills",
                reason, sw.ElapsedMilliseconds, residual);
        }
    }

    /// <summary>
    /// Issue #330 PR-3: iterate every channel with an EOD export
    /// configured and project yesterday's UTC business day's audit log
    /// into the CSV drop. Per-channel failures (e.g. missing audit log
    /// for a quiet day) are logged at warn level and swallowed so a
    /// single failing channel never aborts the daily rollover for the
    /// rest. Called from both the scheduled timer and the
    /// <c>/admin/daily-reset</c> HTTP endpoint via
    /// <see cref="TriggerDailyReset"/>.
    /// </summary>
    private void TriggerEodExportForAllChannels(string reason)
    {
        if (_eodExportByChannel.Count == 0) return;
        // Project the just-closed UTC business day. Using yesterday UTC
        // matches the audit writer's day-boundary semantics — the
        // currently-open day's audit file is still being written, so
        // operators export the last sealed day.
        var businessDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        foreach (var channel in _eodExportByChannel.Keys.ToList())
        {
            try
            {
                var result = TriggerEodExport(channel, businessDate);
                if (result is { } r)
                {
                    _logger.LogInformation(
                        "daily-reset ({Reason}): channel={Channel} date={Date} EOD export rows={Rows} sha256={Sha}",
                        reason, channel, businessDate, r.RowCount, r.Sha256Hex);
                }
            }
            catch (FileNotFoundException)
            {
                _logger.LogWarning(
                    "daily-reset ({Reason}): channel={Channel} date={Date} EOD export skipped — no audit log for that day",
                    reason, channel, businessDate);
            }
            catch (EodExportInProgressException)
            {
                _logger.LogWarning(
                    "daily-reset ({Reason}): channel={Channel} date={Date} EOD export skipped — already in progress",
                    reason, channel, businessDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "daily-reset ({Reason}): channel={Channel} date={Date} EOD export FAILED",
                    reason, channel, businessDate);
            }
        }
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
        // Issue #290: acquire an exclusive lock on every distinct
        // persistence dataDir before constructing any persister or
        // WAL. Two host processes pointed at the same dataDir would
        // silently corrupt each other's snapshot + WAL files; the
        // lock turns that misconfiguration into a loud refuse-to-
        // start error with the holder PID surfaced in the exception
        // message. Acquired locks are released by DisposeAsync.
        AcquireDataDirLocks();
        var sessionRegistry = new SessionRegistry();
        var gatewayRouter = new GatewayRouter(sessionRegistry, _loggerFactory.CreateLogger<GatewayRouter>());

        var firmRegistry = FirmRegistry;
        var defaultSession = ResolveDefaultSession(firmRegistry);
        var routing = new Dictionary<long, ChannelDispatcher>();
        // OPT-03 / ADR 0014: accumulate (instrument, dispatcher) pairs
        // across every channel so OptionExpirySweeper can fan out per
        // option series at end-of-trading-day.
        var instrumentDispatcherPairs = new List<(Instrument Instrument, ChannelDispatcher Dispatcher)>();
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
            var persister = BuildPersister(ch);
            var wal = BuildWal(ch);
            var auditWriter = BuildPostTradeAuditWriter(ch);
            if (persister != null) _persistersByChannel[ch.ChannelNumber] = persister;
            if (wal != null)
            {
                _walsByChannel[ch.ChannelNumber] = wal;
                channelMetrics.SetWalSizeBytes(wal.CurrentSizeBytes);
                channelMetrics.SetWalDropsOnFull(wal.DropsOnFullCount);
            }
            if (auditWriter != null) _auditWriters.Add(auditWriter);
            // Issue #270: cross-channel consistency check on restore.
            // Resolve the policy here so the dispatcher can stay
            // host-agnostic (it just gets a predicate + enum).
            var orphanPolicy = ParseOrphanPolicy(ch.Persistence?.OrphanSessionPolicy);
            // Issue #485: sessionExists now checks if the sessionId is a known
            // FIXP session credential (new format: numeric string like "12345").
            // Legacy snapshots with "conn-*" format will always return false
            // (treated as orphans per user's migration choice).
            Func<string, bool> sessionExists = sid =>
            {
                // Legacy "conn-*" or "pending-*" format: always orphan
                if (sid.StartsWith("conn-", StringComparison.Ordinal) ||
                    sid.StartsWith("pending-", StringComparison.Ordinal))
                    return false;
                return firmRegistry.FindSession(sid) is not null;
            };
            // ADR 0008 PR-2: rebuild the bust dedup index from on-disk
            // audit files within the retention window so a restart picks
            // up where the previous run left off. Only meaningful when
            // the audit writer is wired (otherwise there are no files).
            B3.Exchange.PostTrade.BustDedupIndex? bustDedup = null;
            string? auditRootDir = null;
            string? dropRootDir = null;
            B3.Exchange.PostTrade.IAmendmentsPublisher? amendmentsPublisher = null;
            if (auditWriter != null && ch.PostTradeAudit?.DataDir is { } dataDir && !string.IsNullOrWhiteSpace(dataDir))
            {
                auditRootDir = dataDir;
                int retention = ch.PostTradeAudit.RetentionDays;
                bustDedup = B3.Exchange.PostTrade.BustDedupIndex.LoadFromAuditFiles(
                    dataDir, ch.ChannelNumber, retention, DateOnly.FromDateTime(DateTime.UtcNow));
                if (!string.IsNullOrWhiteSpace(ch.PostTradeAudit.EodDropDir))
                {
                    dropRootDir = ch.PostTradeAudit.EodDropDir;
                    amendmentsPublisher = new B3.Exchange.PostTrade.AmendmentsPublisher(
                        _loggerFactory.CreateLogger<B3.Exchange.PostTrade.AmendmentsPublisher>());
                }
            }

            var disp = new ChannelDispatcher(
                channelNumber: ch.ChannelNumber,
                engineFactory: s =>
                {
                    var e = new MatchingEngine(instruments, s, engineLogger, ch.SelfTradePrevention);
                    capturedEngine = e;
                    return e;
                },
                options: new ChannelDispatcherOptions
                {
                    PacketSink = sink,
                    Outbound = gatewayRouter,
                    Logger = _loggerFactory.CreateLogger<ChannelDispatcher>(),
                    Metrics = channelMetrics,
                    SessionFirmCounters = _metrics.SessionFirmMessages,
                    OpenOrders = _metrics.OpenOrders,
                    MaxOpenOrdersPerFirm = _config.MaxOpenOrdersPerFirm,
                    RetxBuffer = retxBuffer,
                    Persister = persister,
                    SnapshotThrottle = ch.Persistence?.Throttle?.ToPolicy(),
                    UseAsyncSnapshotWriter = ch.Persistence?.AsyncWriter ?? false,
                    Wal = wal,
                    WalAppendFailurePolicy = ch.Persistence?.Wal?.ResolveOnAppendFailure() ?? B3.Exchange.Core.WalAppendFailurePolicy.Continue,
                    SessionExists = sessionExists,
                    OrphanPolicy = orphanPolicy,
                    SeedSecurityIds = instruments.Select(i => i.SecurityId).ToArray(),
                    PostTradeSink = auditWriter,
                    AuditRootDir = auditRootDir,
                    BustDedup = bustDedup,
                    DropRootDir = dropRootDir,
                    AmendmentsPublisher = amendmentsPublisher,
                });
            disp.Start();
            _dispatchers.Add(disp);
            foreach (var inst in instruments)
            {
                if (routing.ContainsKey(inst.SecurityId))
                    throw new InvalidOperationException($"SecurityId {inst.SecurityId} mapped to multiple channels");
                routing.Add(inst.SecurityId, disp);
                instrumentDispatcherPairs.Add((inst, disp));
            }
            _logger.LogInformation("channel {ChannelNumber}: {InstrumentCount} instruments → {Group}:{Port}",
                ch.ChannelNumber, instruments.Count, ch.IncrementalGroup, ch.IncrementalPort);

            if (auditWriter != null)
            {
                var audit = ch.PostTradeAudit!;
                _logger.LogInformation(
                    "channel {ChannelNumber}: post-trade audit log enabled at {DataDir} (retentionDays={RetentionDays})",
                    ch.ChannelNumber, audit.DataDir, audit.RetentionDays);

                if (!string.IsNullOrWhiteSpace(audit.EodDropDir))
                {
                    // Snapshot the symbol map at startup. InstrumentLoader
                    // already de-duplicates symbols per channel so the
                    // dictionary is unambiguous; unknown securityIds (e.g.
                    // delisted between runs) fall back to the numeric id
                    // inside EodFillsExporter.
                    var symbols = instruments.ToDictionary(i => i.SecurityId, i => i.Symbol);
                    var exporterLogger = _loggerFactory.CreateLogger<B3.Exchange.PostTrade.EodFillsExporter>();
                    var exporter = new B3.Exchange.PostTrade.EodFillsExporter(exporterLogger);
                    _eodExportByChannel[ch.ChannelNumber] = new EodExportContext(
                        exporter, audit.DataDir, audit.EodDropDir, symbols, disp.PostTradeRoutingLock);
                    _logger.LogInformation(
                        "channel {ChannelNumber}: EOD fills export enabled (dropDir={DropDir})",
                        ch.ChannelNumber, audit.EodDropDir);
                }
                if (audit.RetentionDays > 0)
                {
                    var capturedWriter = auditWriter;
                    int retentionDays = audit.RetentionDays;
                    byte chNum = ch.ChannelNumber;
                    var retentionLogger = _loggerFactory.CreateLogger<B3.Exchange.PostTrade.FileAuditLogWriter>();
                    var timer = new Timer(_ =>
                    {
                        try
                        {
                            int deleted = capturedWriter.PruneOldDays(
                                DateOnly.FromDateTime(DateTime.UtcNow), retentionDays);
                            if (deleted > 0)
                            {
                                retentionLogger.LogInformation(
                                    "channel {ChannelNumber}: pruned {Count} expired audit file(s) (retentionDays={RetentionDays})",
                                    chNum, deleted, retentionDays);
                            }
                        }
                        catch (Exception ex)
                        {
                            retentionLogger.LogWarning(ex,
                                "channel {ChannelNumber}: audit log retention prune failed", chNum);
                        }
                    }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
                    _auditRetentionTimers.Add(timer);
                }
            }

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

            if (ch.PriceBandPublishIntervalMs > 0)
            {
                var cadence = TimeSpan.FromMilliseconds(Math.Max(1, ch.PriceBandPublishIntervalMs));
                var publisher = new PriceBandPublisher(instruments, cadence);
                if (publisher.Count > 0)
                {
                    disp.AttachPriceBandPublisher(publisher);
                    var capturedDisp = disp;
                    var timer = new Timer(_ => capturedDisp.EnqueuePriceBandTick(), null, cadence, cadence);
                    _priceBandTimers.Add(timer);
                    _logger.LogInformation("channel {ChannelNumber}: price-band on incremental feed every {CadenceMs:n0}ms for {InstrumentCount} instruments",
                        ch.ChannelNumber, cadence.TotalMilliseconds, publisher.Count);
                }
                else
                {
                    _logger.LogInformation("channel {ChannelNumber}: price-band cadence configured but no instruments carry lowerPriceBand/upperPriceBand",
                        ch.ChannelNumber);
                }
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

        // OPT-03 / ADR 0014: stand up the option-expiry sweeper once
        // every channel dispatcher has been built. Only instruments
        // whose SecurityType is option and that carry an
        // ExpirationDate are admitted as sinks; non-option channels
        // therefore pay nothing here.
        {
            var sinks = OptionExpirySweeper.BuildSinks(instrumentDispatcherPairs);
            _optionExpirySweeper = new OptionExpirySweeper(sinks,
                _loggerFactory.CreateLogger<OptionExpirySweeper>());
            if (sinks.Count > 0)
            {
                _logger.LogInformation(
                    "option-expiry sweeper armed for {SeriesCount} option series across {ChannelCount} channels",
                    sinks.Count, _dispatchers.Count);
            }
        }
        // GAP-23 / #499: stand up the GTD expiry sweeper. It fans out one
        // expiry command per channel at daily reset; "today" is the B3 local
        // market date in the configured daily-reset timezone (the engine
        // stays clockless and receives the date as a command argument).
        {
            var todayProvider = GtdExpirySweeper.BuildTodayProvider(
                _config.DailyReset?.Timezone,
                _loggerFactory.CreateLogger<GtdExpirySweeper>());
            _gtdExpirySweeper = new GtdExpirySweeper(
                _dispatchers,
                _loggerFactory.CreateLogger<GtdExpirySweeper>(),
                todayProvider);
        }
        var listenEp = ParseEndpoint(_config.Tcp.Listen);
        var sessionOptions = new FixpSessionOptions
        {
            HeartbeatIntervalMs = _config.Tcp.HeartbeatIntervalMs,
            IdleTimeoutMs = _config.Tcp.IdleTimeoutMs,
            TestRequestGraceMs = _config.Tcp.TestRequestGraceMs,
            SendingTimeSkewToleranceNs = (ulong)Math.Max(_config.Tcp.SendingTimeSkewToleranceMs, 0) * 1_000_000UL,
            LifecycleMetrics = _metrics.Sessions,
            ThrottleTimeWindowMs = _config.Tcp.Throttle?.TimeWindowMs ?? 0,
            ThrottleMaxMessages = _config.Tcp.Throttle?.MaxMessages ?? 0,
            MaxOrderRatePerSecond = 200,
            ThrottleMetrics = _metrics.Throttle,
            OnTransportSendQueueFull = _metrics.Transport.IncSendQueueFull,
            FatFinger = new InboundFatFingerOptions
            {
                MaxOrderQty = _config.Tcp.MaxOrderQty,
                MaxPriceMantissa = _config.Tcp.MaxPrice,
                PriceBandPercent = _config.Tcp.PriceBandPercent,
                LastTradePriceProvider = _router.LastTradePriceMantissa,
            },
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
        // Issue #405/#431: optional FIXP session-resync persistence. When
        // configured, every outbound business frame is appended to a
        // quota/retention-managed per-session journal and the FIXP envelope state
        // (SessionVerId, LastIncomingSeqNo, OutboundMsgSeqNum) is
        // snapshotted on every Negotiate/Suspend/non-terminal Close.
        // On boot we load whatever state survived a crash + seed the
        // SessionClaimRegistry so a peer reconnecting with its
        // original SessionId can resume the full FIXP §1.5
        // RECOVERABLE serverFlow contract — every business frame
        // produced pre-crash is recoverable post-restart via the
        // journal cold-read path.
        IReadOnlyDictionary<uint, B3.Exchange.Gateway.Persistence.FixpSessionStateSnapshot>? persistedSessionStates = null;
        if (!string.IsNullOrWhiteSpace(_config.Tcp.RetransmitPersistenceDir))
        {
            _outboundJournal = new B3.Exchange.Gateway.Persistence.FileFixpOutboundJournal(
                _config.Tcp.RetransmitPersistenceDir!,
                _loggerFactory.CreateLogger<B3.Exchange.Gateway.Persistence.FileFixpOutboundJournal>(),
                maxBytesPerSession: _config.Tcp.MaxJournalBytes,
                maxRetention: TimeSpan.FromHours(_config.Tcp.MaxJournalRetentionHours),
                metrics: _metrics.Journal);
            _statePersister = new B3.Exchange.Gateway.Persistence.FileFixpSessionStatePersister(
                _config.Tcp.RetransmitPersistenceDir!,
                _loggerFactory.CreateLogger<B3.Exchange.Gateway.Persistence.FileFixpSessionStatePersister>());
            var loaded = _statePersister.LoadAll();
            var loadedDict = loaded.ToDictionary(s => s.SessionId, s => s);
            persistedSessionStates = loadedDict;
            foreach (var snap in loadedDict.Values)
            {
                sessionClaims.SeedLastVersion(snap.SessionId, snap.SessionVerId);
            }
            _logger.LogInformation(
                "fixp session resync enabled: dir={Dir} recoveredStates={Count}",
                _config.Tcp.RetransmitPersistenceDir, loadedDict.Count);
        }
        _listener = new EntryPointListener(listenEp, _router, sessionRegistry, _loggerFactory,
            identityFactory: remote =>
            {
                var connectionId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
                // Pre-Negotiate placeholder: firm/session are stamped from
                // a default and rewritten by FixpSession on a successful
                // Negotiate. Issue #485: SessionId=0 so Identity uses
                // "pending-{connId}" format until Negotiate completes,
                // avoiding collision with real FIXP SessionIds.
                var enteringFirm = defaultSession.firmCode;
                return new EntryPointListener.AcceptedConnection(
                    ConnectionId: connectionId,
                    EnteringFirm: enteringFirm,
                    SessionId: 0);
            },
            sessionOptions: sessionOptions,
            onSessionClosed: (s, reason) => _logger.LogInformation("session {ConnectionId} closed: {Reason}", s.ConnectionId, reason),
            negotiationValidator: requireHandshake ? negotiationValidator : null,
            sessionClaims: requireHandshake ? sessionClaims : null,
            establishValidator: requireHandshake ? establishValidator : null,
            retransmitMetrics: _metrics.Retransmit,
            outboundJournal: _outboundJournal,
            statePersister: _statePersister,
            persistedSessionStates: persistedSessionStates,
            persistedMaxOrderRateResolver: sessionId =>
                firmRegistry.FindSessionByWire(sessionId)?.Policy.MaxOrderRatePerSecond);
        _listener.Start();
        _logger.LogInformation("entrypoint listening on {Endpoint}", _listener.LocalEndpoint);

        // Issue #288: opt-in per-session label cardinality for the
        // RetransmitBuffer utilization gauge.
        if (_config.Metrics?.FixpSessionLabelsEnabled == true)
            _metrics.EmitFixpSessionLabels = true;

        var sessionProvider = new ListenerSessionProvider(_listener, firmRegistry);
        _metrics.SetSessionProvider(sessionProvider);

        // Issue #286: register the WAL-halt readiness probe BEFORE the
        // HttpServer snapshots _probes. Registering after StartAsync
        // throws (the snapshot is taken on construction); registering
        // before construction also makes /health/ready report the
        // halted channels from the very first scrape after boot.
        if (_config.Channels.Any(c => c.Persistence?.Wal is { } w
            && w.ResolveOnAppendFailure() == B3.Exchange.Core.WalAppendFailurePolicy.Halt))
        {
            RegisterReadinessProbe(new B3.Exchange.Core.WalHaltReadinessProbe(_dispatchers));
        }

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
                dailyResetTrigger: () => TriggerDailyReset("http-trigger"),
                persisters: _persistersByChannel,
                wals: _walsByChannel,
                instrumentRouting: routing,
                eodExportTrigger: TriggerEodExport);
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
                // Both the scheduled timer and /admin/daily-reset go
                // through TriggerDailyReset so the listener-terminate +
                // EOD-export chaining (#330 PR-3) is identical for
                // automated and manual rollovers.
                action: kind => TriggerDailyReset("daily-reset", kind),
                logger: _loggerFactory.CreateLogger<DailyResetScheduler>());
            _dailyReset.Start();
        }

        if (_config.PhaseScheduler is { Enabled: true } pcfg)
        {
            _phaseScheduler = new PhaseScheduler(
                pcfg,
                routing,
                _metrics,
                _loggerFactory.CreateLogger<PhaseScheduler>());
            _phaseScheduler.Start();
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
                    LastActivityAtMs: s.LastActivityAtMs)
                {
                    RetxBufferCapacity = s.RetxBufferCapacity,
                };
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

    /// <summary>
    /// Builds the per-channel persister (issue #260). Returns
    /// <c>null</c> when the channel has no <c>persistence</c> block,
    /// preserving the legacy stateless boot for configs that haven't
    /// <summary>
    /// Issue #290: walks the channels list, dedupes by absolute
    /// dataDir path, and acquires an exclusive
    /// <see cref="B3.Exchange.Persistence.DataDirLock"/> per
    /// distinct directory. Throws on the first directory that is
    /// already held by another process so the host fails fast
    /// rather than silently corrupting concurrent writers.
    /// </summary>
    private void AcquireDataDirLocks()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in _config.Channels)
        {
            if (ch.Persistence is null) continue;
            var dir = ch.Persistence.DataDir;
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var canonical = Path.GetFullPath(dir);
            if (!seen.Add(canonical)) continue;
            var lockHandle = B3.Exchange.Persistence.DataDirLock.Acquire(
                canonical,
                _loggerFactory.CreateLogger<B3.Exchange.Persistence.DataDirLock>());
            _dataDirLocks.Add(lockHandle);
        }
    }

    /// <summary>
    /// Builds the per-channel persister (issue #260). Returns
    /// <c>null</c> when the channel has no <c>persistence</c> block,
    /// preserving the legacy stateless boot for configs that haven't
    /// opted in.
    /// </summary>
    private IChannelStatePersister? BuildPersister(ChannelConfig ch)
    {
        if (ch.Persistence is null) return null;
        if (string.IsNullOrWhiteSpace(ch.Persistence.DataDir))
        {
            _logger.LogWarning("channel {ChannelNumber}: persistence.dataDir is empty; skipping persister",
                ch.ChannelNumber);
            return null;
        }
        int generations = ch.Persistence.Generations > 0
            ? ch.Persistence.Generations
            : FileChannelStatePersister.DefaultGenerations;
        var writeFormat = ParseSnapshotFormat(ch.Persistence.Format);
        return new FileChannelStatePersister(
            ch.Persistence.DataDir,
            _loggerFactory.CreateLogger<FileChannelStatePersister>(),
            generations,
            migrations: null,
            writeFormat: writeFormat);
    }

    /// <summary>
    /// Issue #266: parses the <c>persistence.format</c> JSON string into
    /// the <see cref="SnapshotFileFormat"/> enum used by the file
    /// persister when WRITING snapshots. Defaults to
    /// <see cref="SnapshotFileFormat.Json"/> when absent.
    /// </summary>
    private static SnapshotFileFormat ParseSnapshotFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SnapshotFileFormat.Json;
        return raw.Trim().ToLowerInvariant() switch
        {
            "json" => SnapshotFileFormat.Json,
            "binary" => SnapshotFileFormat.Binary,
            _ => throw new InvalidOperationException(
                $"persistence.format must be 'json' or 'binary' (got '{raw}')"),
        };
    }

    /// <summary>
    /// Builds the per-channel Write-Ahead Log (issue #269). Returns
    /// <c>null</c> when the channel has no <c>persistence.wal</c> block
    /// or it is not enabled — preserving the snapshot-only behaviour
    /// for configs that haven't opted in.
    /// </summary>
    private IChannelWriteAheadLog? BuildWal(ChannelConfig ch)
    {
        if (ch.Persistence?.Wal is not { Enabled: true } walCfg) return null;
        if (string.IsNullOrWhiteSpace(ch.Persistence.DataDir))
        {
            _logger.LogWarning(
                "channel {ChannelNumber}: persistence.wal.enabled=true but persistence.dataDir is empty; skipping WAL",
                ch.ChannelNumber);
            return null;
        }
        var fsyncMode = walCfg.ResolveFsyncMode();
        // GroupCommit MUST NOT also fsync per write — the WAL ctor
        // hard-rejects the contradictory combination. ResolveFsyncMode
        // already ensures the operator-facing config is consistent;
        // here we just normalise the internal flag passed downstream.
        bool fsyncPerWrite = fsyncMode == B3.Exchange.Core.WalFsyncMode.PerWrite;
        var groupCommitInterval = TimeSpan.FromMilliseconds(Math.Max(1, walCfg.GroupCommitIntervalMs));
        return new FileChannelWriteAheadLog(
            ch.Persistence.DataDir,
            ch.ChannelNumber,
            _loggerFactory.CreateLogger<FileChannelWriteAheadLog>(),
            fsyncPerWrite,
            walCfg.MaxBytes,
            walCfg.ResolveOnFull(),
            fsyncMode,
            groupCommitInterval);
    }

    /// <summary>
    /// Builds the per-channel post-trade audit log writer
    /// (issues #329 / #352). Returns <c>null</c> when the channel
    /// has no <c>postTradeAudit</c> block or <c>enabled=false</c>,
    /// in which case the dispatcher falls back to
    /// <see cref="B3.Exchange.PostTrade.NullPostTradeSink"/>.
    /// </summary>
    private B3.Exchange.PostTrade.FileAuditLogWriter? BuildPostTradeAuditWriter(ChannelConfig ch)
    {
        if (ch.PostTradeAudit is not { Enabled: true } audit) return null;
        if (string.IsNullOrWhiteSpace(audit.DataDir))
        {
            _logger.LogWarning(
                "channel {ChannelNumber}: postTradeAudit.enabled=true but dataDir is empty; skipping audit log",
                ch.ChannelNumber);
            return null;
        }
        if (audit.RetentionDays < 0)
        {
            throw new InvalidOperationException(
                $"channel {ch.ChannelNumber}: postTradeAudit.retentionDays must be >= 0 (got {audit.RetentionDays}); use 0 to disable automatic retention");
        }
        return new B3.Exchange.PostTrade.FileAuditLogWriter(
            audit.DataDir,
            ch.ChannelNumber);
    }

    /// <summary>
    /// Issue #330 PR-2: trigger the EOD fills CSV export for a channel
    /// and UTC business date. Wired into
    /// <c>POST /admin/post-trade/eod-export?channel=N&amp;date=YYYY-MM-DD</c>
    /// and (PR-3) the daily-reset scheduler.
    /// </summary>
    /// <returns><see langword="null"/> when the channel has no EOD export
    /// configured (operator should treat as 404); otherwise the export
    /// result so the endpoint can surface row count / sha256 / path.</returns>
    /// <remarks>
    /// Concurrent calls for the SAME <c>(channel, date)</c> are serialized
    /// here — the second caller throws <see cref="EodExportInProgressException"/>
    /// so the endpoint can surface a 409 Conflict without partially
    /// publishing two interleaved CSV / .done pairs. Different
    /// <c>(channel, date)</c> pairs run concurrently (the underlying
    /// exporter is stateless). Other exceptions from
    /// <c>EodFillsExporter.Export</c> propagate to the caller.
    /// </remarks>
    internal B3.Exchange.PostTrade.EodFillsExportResult? TriggerEodExport(byte channelNumber, DateOnly businessDate)
    {
        if (!_eodExportByChannel.TryGetValue(channelNumber, out var ctx)) return null;
        var key = (channelNumber, businessDate);
        if (!_eodExportInFlight.TryAdd(key, 0))
        {
            throw new EodExportInProgressException(channelNumber, businessDate);
        }
        try
        {
            // ADR 0008 §3 race-window closure: hold the per-channel
            // post-trade routing lock across the full export so any
            // bust accepted concurrently either (a) lands in the
            // pre-EOD file before the cancelled-set scan, or (b) is
            // routed to the post-EOD path because it observes the
            // new .done sidecar after we release the lock.
            lock (ctx.RoutingLock)
            {
                return ctx.Exporter.Export(
                    ctx.AuditRootDir,
                    ctx.DropRootDir,
                    channelNumber,
                    businessDate,
                    secId => ctx.Symbols.TryGetValue(secId, out var sym) ? sym : null,
                    DateTime.UtcNow);
            }
        }
        finally
        {
            _eodExportInFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Thrown by <see cref="TriggerEodExport"/> when another export for
    /// the same <c>(channel, date)</c> is already in flight. The HTTP
    /// endpoint translates this into a 409 Conflict so concurrent
    /// operators never observe a CSV / .done pair whose bytes came from
    /// different exporter runs.
    /// </summary>
    internal sealed class EodExportInProgressException : InvalidOperationException
    {
        public byte Channel { get; }
        public DateOnly Date { get; }
        public EodExportInProgressException(byte channel, DateOnly date)
            : base($"EOD export already in progress for channel={channel} date={date:yyyy-MM-dd}")
        {
            Channel = channel;
            Date = date;
        }
    }

    /// <summary>
    /// Per-channel EOD export wiring captured at host startup. Held in
    /// <c>_eodExportByChannel</c> so the HTTP endpoint and (PR-3) the
    /// daily-reset auto-trigger can resolve it without rebuilding the
    /// symbol map on every call.
    /// </summary>
    private sealed record EodExportContext(
        B3.Exchange.PostTrade.EodFillsExporter Exporter,
        string AuditRootDir,
        string DropRootDir,
        IReadOnlyDictionary<long, string> Symbols,
        object RoutingLock);

    /// <summary>
    /// Issue #270: parses the <c>persistence.orphanSessionPolicy</c>
    /// JSON string into the <see cref="OrphanSessionPolicy"/> enum
    /// expected by <see cref="ChannelDispatcher"/>. Defaults to
    /// <see cref="OrphanSessionPolicy.Drop"/> when absent.
    /// </summary>
    private static OrphanSessionPolicy ParseOrphanPolicy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return OrphanSessionPolicy.Drop;
        return raw.Trim().ToLowerInvariant() switch
        {
            "drop" => OrphanSessionPolicy.Drop,
            "reject" => OrphanSessionPolicy.Reject,
            _ => throw new InvalidOperationException(
                $"persistence.orphanSessionPolicy must be 'drop' or 'reject' (got '{raw}')"),
        };
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
                    MaxOrderRatePerSecond: sc.Policy.MaxOrderRatePerSecond,
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
        if (_phaseScheduler != null) await _phaseScheduler.DisposeAsync().ConfigureAwait(false);
        if (_http != null) await _http.DisposeAsync().ConfigureAwait(false);
        foreach (var t in _snapshotTimers) await t.DisposeAsync().ConfigureAwait(false);
        foreach (var t in _priceBandTimers) await t.DisposeAsync().ConfigureAwait(false);
        if (_listener != null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _instrumentDefPublishers) await p.DisposeAsync().ConfigureAwait(false);
        foreach (var d in _dispatchers) await d.DisposeAsync().ConfigureAwait(false);
        // Dispose audit-log retention timers and writers AFTER the
        // dispatchers have drained: the dispatcher's final
        // OnCommandBoundary/Checkpoint must run before the writer is
        // closed so the on-disk watermark matches the last appended
        // record.
        foreach (var t in _auditRetentionTimers) await t.DisposeAsync().ConfigureAwait(false);
        _auditRetentionTimers.Clear();
        foreach (var w in _auditWriters)
        {
            try { w.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "audit writer dispose threw"); }
        }
        _auditWriters.Clear();
        foreach (var s in _ownedSinks) s.Dispose();
        _outboundJournal?.Dispose();
        _statePersister?.Dispose();
        // Issue #290: release dataDir locks last so any persister/WAL
        // shutdown that needs the directory still has it.
        foreach (var l in _dataDirLocks)
        {
            try { l.Dispose(); } catch { }
        }
        _dataDirLocks.Clear();
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
            catch (OperationCanceledException) { break; /* expected: drain cancelled by hard shutdown */ }
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

        // Phase 4: drain outbound queues then broadcast Terminate(Finished).
        // Issue #487: ensure pending ER frames reach the wire before close.
        sw.Restart();
        if (_listener != null)
        {
            try { await _listener.DrainAllSessionsOutboundAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "drain-outbound threw"); }
        }
        sw.Stop();
        _logger.LogInformation("shutdown phase=drain-outbound duration={DurationMs}ms", sw.ElapsedMilliseconds);

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
        foreach (var t in _priceBandTimers)
        {
            try { await t.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _priceBandTimers.Clear();
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
