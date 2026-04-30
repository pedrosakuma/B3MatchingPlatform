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
    private readonly List<ChannelDispatcher> _dispatchers = new();
    private readonly List<IDisposable> _ownedSinks = new();
    private EntryPointListener? _listener;
    private HostRouter? _router;

    public ExchangeHost(HostConfig config, ILoggerFactory? loggerFactory = null,
        Func<ChannelConfig, IUmdfPacketSink>? packetSinkFactory = null)
    {
        _config = config;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ExchangeHost>();
        _packetSinkFactory = packetSinkFactory;
    }

    public IPEndPoint? TcpEndpoint => _listener?.LocalEndpoint;

    public Task StartAsync()
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
            var disp = new ChannelDispatcher(
                channelNumber: ch.ChannelNumber,
                engineFactory: s => new MatchingEngine(instruments, s, engineLogger),
                packetSink: sink,
                logger: _loggerFactory.CreateLogger<ChannelDispatcher>());
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
        }

        _router = new HostRouter(routing, _loggerFactory.CreateLogger<HostRouter>());
        var listenEp = ParseEndpoint(_config.Tcp.Listen);
        _listener = new EntryPointListener(listenEp, _router, _loggerFactory,
            identityFactory: remote =>
            {
                var connectionId = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
                return new EntryPointListener.AcceptedConnection(
                    ConnectionId: connectionId,
                    EnteringFirm: _config.Tcp.EnteringFirm,
                    SessionId: (uint)(connectionId & 0xFFFFFFFFu));
            });
        _listener.Start();
        _logger.LogInformation("entrypoint listening on {Endpoint}", _listener.LocalEndpoint);
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
        _logger.LogInformation("exchange host shutting down");
        if (_listener != null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var d in _dispatchers) await d.DisposeAsync().ConfigureAwait(false);
        foreach (var s in _ownedSinks) s.Dispose();
    }
}
