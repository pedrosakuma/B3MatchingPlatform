using System.Net;
using B3.Exchange.Contracts.Time;
using B3.Exchange.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// Minimal Kestrel-hosted operability surface. Exposes:
///   GET /health/live    — 200 if every dispatcher heartbeat is fresher
///                         than <see cref="HttpConfig.LivenessStaleMs"/>,
///                         503 otherwise. Body lists the channels.
///   GET /health/ready   — 200 once every <see cref="IReadinessProbe"/>
///                         registered with the host reports ready.
///   GET /metrics        — Prometheus 0.0.4 text exposition of every
///                         counter/gauge held by <see cref="MetricsRegistry"/>.
///
/// Implemented with the minimal <c>WebApplication</c> hosting model and
/// no external NuGet packages — Kestrel ships in the
/// <c>Microsoft.AspNetCore.App</c> shared framework.
///
/// Route handlers are split into three classes for maintainability
/// (issue #383):
///   * <see cref="DiagnosticsEndpoints"/> — read-only diagnostics.
///   * <see cref="OperatorEndpoints"/> — channel- and instrument-scoped
///     mutations dispatched through the engine's inbound queue.
///   * <see cref="AdminEndpoints"/> — host-wide admin (daily-reset,
///     EOD export, snapshot get/force/reset/validate).
/// </summary>
public sealed class HttpServer : IAsyncDisposable
{
    private readonly HttpConfig _config;
    private readonly Action<string>? _log;
    private readonly DiagnosticsEndpoints _diagnostics;
    private readonly OperatorEndpoints _operator;
    private readonly AdminEndpoints _admin;
    private WebApplication? _app;

    public HttpServer(HttpConfig config, MetricsRegistry metrics,
        IReadOnlyList<IReadinessProbe> probes,
        IReadOnlyDictionary<byte, ChannelDispatcher> dispatchers,
        Action<string>? log = null,
        Func<IEnumerable<SessionDiagnostics>>? sessionsProvider = null,
        IReadOnlyList<FirmInfo>? firms = null,
        Func<int>? dailyResetTrigger = null,
        IReadOnlyDictionary<byte, IChannelStatePersister>? persisters = null,
        IReadOnlyDictionary<byte, IChannelWriteAheadLog>? wals = null,
        IReadOnlyDictionary<long, ChannelDispatcher>? instrumentRouting = null,
        Func<byte, DateOnly, B3.Exchange.PostTrade.EodFillsExportResult?>? eodExportTrigger = null,
        INanosTimeSource? timeSource = null)
    {
        _config = config;
        _log = log;
        var firmsList = firms ?? Array.Empty<FirmInfo>();
        var persistersMap = persisters ?? new Dictionary<byte, IChannelStatePersister>();
        var walsMap = wals ?? new Dictionary<byte, IChannelWriteAheadLog>();
        var routingMap = instrumentRouting ?? new Dictionary<long, ChannelDispatcher>();
        var time = timeSource ?? SystemNanosTimeSource.Instance;

        _diagnostics = new DiagnosticsEndpoints(config, metrics, probes, sessionsProvider, firmsList);
        _operator = new OperatorEndpoints(dispatchers, routingMap, metrics, time);
        _admin = new AdminEndpoints(dispatchers, persistersMap, walsMap, dailyResetTrigger, eodExportTrigger, log);
    }

    public IPEndPoint? LocalEndpoint { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var ep = ParseEndpoint(_config.Listen);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(ep.Address, ep.Port);
            o.AddServerHeader = false;
        });

        var app = builder.Build();

        _diagnostics.Map(app);
        _operator.Map(app);
        _admin.Map(app);

        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;

        // Resolve the actual bound endpoint (port may be 0 for "any free").
        var serverAddresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        var addr = serverAddresses?.Addresses.FirstOrDefault();
        if (addr != null && Uri.TryCreate(addr, UriKind.Absolute, out var uri))
            LocalEndpoint = new IPEndPoint(IPAddress.Parse(uri.Host), uri.Port);
        else
            LocalEndpoint = ep;

        _log?.Invoke($"http listening on {LocalEndpoint}");
    }

    private static IPEndPoint ParseEndpoint(string s)
    {
        // Use IPEndPoint.Parse for robust parsing of IPv4, IPv6, and numeric host:port formats.
        // Note: IPEndPoint.Parse does not support hostname resolution (e.g., localhost:8080).
        // For hostname support, parse separately and use DNS resolution if needed.
        if (!s.Contains(':'))
            throw new FormatException($"expected host:port, got '{s}'");

        try
        {
            // IPEndPoint.Parse supports "[IPv6]:port" and "IPv4:port" formats.
            return IPEndPoint.Parse(s);
        }
        catch (Exception ex)
        {
            throw new FormatException($"failed to parse endpoint '{s}' (IP literal and port only; hostname resolution not supported)", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            try { await _app.DisposeAsync().ConfigureAwait(false); } catch { }
            _app = null;
        }
    }
}
