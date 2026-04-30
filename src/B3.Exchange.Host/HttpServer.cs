using System.Net;
using B3.Exchange.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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
/// </summary>
public sealed class HttpServer : IAsyncDisposable
{
    private readonly HttpConfig _config;
    private readonly MetricsRegistry _metrics;
    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly Action<string>? _log;
    private WebApplication? _app;

    public HttpServer(HttpConfig config, MetricsRegistry metrics,
        IReadOnlyList<IReadinessProbe> probes, Action<string>? log = null)
    {
        _config = config;
        _metrics = metrics;
        _probes = probes;
        _log = log;
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

        app.MapGet("/health/live", (HttpContext ctx) =>
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int stale = _config.LivenessStaleMs;
            var channels = _metrics.Channels;
            var stuck = new List<string>();
            foreach (var c in channels)
            {
                long last = c.LastTickUnixMs;
                if (last == 0 || (nowMs - last) > stale)
                    stuck.Add($"channel={c.ChannelNumber} last_tick_ms_ago={(last == 0 ? -1 : nowMs - last)}");
            }
            if (stuck.Count > 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Results.Text("DOWN\n" + string.Join('\n', stuck) + '\n', "text/plain");
            }
            return Results.Text($"OK channels={channels.Count}\n", "text/plain");
        });

        app.MapGet("/health/ready", (HttpContext ctx) =>
        {
            var notReady = new List<string>();
            foreach (var p in _probes)
                if (!p.IsReady) notReady.Add(p.Name);
            if (notReady.Count > 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Results.Text("NOT_READY\n" + string.Join('\n', notReady) + '\n', "text/plain");
            }
            return Results.Text($"READY probes={_probes.Count}\n", "text/plain");
        });

        app.MapGet("/metrics", () =>
            Results.Text(_metrics.RenderProm(), "text/plain; version=0.0.4; charset=utf-8"));

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
