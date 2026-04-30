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
    private readonly Func<ChannelConfig, SnapshotChannelConfig, IUmdfPacketSink>? _snapshotSinkFactory;
    private readonly Func<ChannelConfig, InstrumentDefinitionConfig, IUmdfPacketSink>? _instrumentDefSinkFactory;
    private readonly List<ChannelDispatcher> _dispatchers = new();
    private readonly List<InstrumentDefinitionPublisher> _instrumentDefPublishers = new();
    private readonly List<IDisposable> _ownedSinks = new();
    private readonly List<Timer> _snapshotTimers = new();
    private EntryPointListener? _listener;
    private HostRouter? _router;

    public ExchangeHost(HostConfig config, Action<string>? log = null,
        Func<ChannelConfig, IUmdfPacketSink>? packetSinkFactory = null,
        Func<ChannelConfig, SnapshotChannelConfig, IUmdfPacketSink>? snapshotSinkFactory = null,
        Func<ChannelConfig, InstrumentDefinitionConfig, IUmdfPacketSink>? instrumentDefSinkFactory = null)
    {
        _config = config;
        _log = log;
        _packetSinkFactory = packetSinkFactory;
        _snapshotSinkFactory = snapshotSinkFactory;
        _instrumentDefSinkFactory = instrumentDefSinkFactory;
    }

    public IPEndPoint? TcpEndpoint => _listener?.LocalEndpoint;

    public IReadOnlyList<ChannelDispatcher> Dispatchers => _dispatchers;

    /// <summary>Snapshot of the InstrumentDef publishers, primarily for tests.</summary>
    public IReadOnlyList<InstrumentDefinitionPublisher> InstrumentDefinitionPublishers => _instrumentDefPublishers;

    public Task StartAsync()
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

            // Capture the engine via a side-channel so we can build a snapshot
            // source that reads through the live book on the dispatcher thread.
            MatchingEngine? capturedEngine = null;
            var disp = new ChannelDispatcher(
                channelNumber: ch.ChannelNumber,
                engineFactory: s =>
                {
                    var e = new MatchingEngine(instruments, s, ch.SelfTradePrevention);
                    capturedEngine = e;
                    return e;
                },
                packetSink: sink);
            disp.Start();
            _dispatchers.Add(disp);
            foreach (var inst in instruments)
            {
                if (routing.ContainsKey(inst.SecurityId))
                    throw new InvalidOperationException($"SecurityId {inst.SecurityId} mapped to multiple channels");
                routing.Add(inst.SecurityId, disp);
            }
            _log?.Invoke($"channel {ch.ChannelNumber}: {instruments.Count} instruments → {ch.IncrementalGroup}:{ch.IncrementalPort}");

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
                    snapSink = new MulticastUdpPacketSink(IPAddress.Parse(snap.Group), snap.Port, local, snap.Ttl ?? ch.Ttl);
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
                _log?.Invoke($"channel {ch.ChannelNumber}: snapshot → {snap.Group}:{snap.Port} every {cadence.TotalMilliseconds:n0}ms");
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
                    idSink = new MulticastUdpPacketSink(IPAddress.Parse(idCfg.Group), idCfg.Port, local, idCfg.Ttl);
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
                _log?.Invoke(
                    $"channel {ch.ChannelNumber}: instrument-def → {idCfg.Group}:{idCfg.Port} every {idCfg.CadenceMs}ms");
            }
        }

        _router = new HostRouter(routing);
        var listenEp = ParseEndpoint(_config.Tcp.Listen);
        var sessionOptions = new EntryPointSessionOptions
        {
            HeartbeatIntervalMs = _config.Tcp.HeartbeatIntervalMs,
            IdleTimeoutMs = _config.Tcp.IdleTimeoutMs,
            TestRequestGraceMs = _config.Tcp.TestRequestGraceMs,
        };
        _listener = new EntryPointListener(listenEp, _router,
            identityFactory: remote =>
            {
                var connectionId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
                return new EntryPointListener.AcceptedConnection(
                    ConnectionId: connectionId,
                    EnteringFirm: _config.Tcp.EnteringFirm,
                    SessionId: (uint)(connectionId & 0xFFFFFFFFu));
            },
            sessionOptions: sessionOptions,
            onSessionClosed: (s, reason) => _log?.Invoke($"session {s.ConnectionId} closed: {reason}"));
        _listener.Start();
        _log?.Invoke($"listening on {_listener.LocalEndpoint}");
        return Task.CompletedTask;
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
        foreach (var t in _snapshotTimers) await t.DisposeAsync().ConfigureAwait(false);
        if (_listener != null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _instrumentDefPublishers) await p.DisposeAsync().ConfigureAwait(false);
        foreach (var d in _dispatchers) await d.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _ownedSinks) s.Dispose();
    }
}
