using System.Net;
using B3.Exchange.Core;
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
    private readonly IReadOnlyDictionary<byte, ChannelDispatcher> _dispatchers;
    private readonly Func<IEnumerable<SessionDiagnostics>>? _sessionsProvider;
    private readonly IReadOnlyList<FirmInfo> _firms;
    private readonly Action<string>? _log;
    private WebApplication? _app;

    public HttpServer(HttpConfig config, MetricsRegistry metrics,
        IReadOnlyList<IReadinessProbe> probes,
        IReadOnlyDictionary<byte, ChannelDispatcher> dispatchers,
        Action<string>? log = null,
        Func<IEnumerable<SessionDiagnostics>>? sessionsProvider = null,
        IReadOnlyList<FirmInfo>? firms = null)
    {
        _config = config;
        _metrics = metrics;
        _probes = probes;
        _dispatchers = dispatchers;
        _sessionsProvider = sessionsProvider;
        _firms = firms ?? Array.Empty<FirmInfo>();
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
                // last == 0 means the dispatcher loop has not produced its
                // first heartbeat yet (still starting up). Liveness should be
                // tolerant during startup — readiness probes (/health/ready)
                // are the correct surface for "not yet ready". A truly dead
                // dispatcher will start ticking and then go stale, which the
                // staleness check below catches.
                if (last == 0) continue;
                if ((nowMs - last) > stale)
                    stuck.Add($"channel={c.ChannelNumber} last_tick_ms_ago={nowMs - last}");
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

        // Operator diagnostics endpoints (issue #70). Read-only.
        app.MapGet("/sessions", () =>
        {
            var arr = SnapshotSessions();
            return Results.Text(SerializeSessions(arr), "application/json");
        });

        app.MapGet("/sessions/{id}", (string id, HttpContext ctx) =>
        {
            var match = SnapshotSessions().FirstOrDefault(s => s.SessionId == id);
            if (match.SessionId is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Results.Text($"unknown session '{id}'\n", "text/plain");
            }
            return Results.Text(SerializeSession(match), "application/json");
        });

        app.MapGet("/firms", () => Results.Text(SerializeFirms(_firms), "application/json"));

        // Operator endpoints (issue #6). All work is dispatched via the
        // channel's inbound queue so the engine state mutation happens on
        // the dispatch thread; the HTTP handler returns 202 Accepted as soon
        // as the work item is enqueued. 404 for unknown channel; 503 if the
        // dispatcher's bounded inbound queue is full (BoundedChannel
        // FullMode is DropWrite, so TryWrite returns false rather than
        // blocking the HTTP thread).
        app.MapPost("/channel/{ch:int}/snapshot-now", (int ch, HttpContext ctx) =>
            HandleOperatorEnqueue(ctx, ch, d => d.EnqueueOperatorSnapshotNow(), "snapshot-now"));

        app.MapPost("/channel/{ch:int}/bump-version", (int ch, HttpContext ctx) =>
            HandleOperatorEnqueue(ctx, ch, d => d.EnqueueOperatorBumpVersion(), "bump-version"));

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

    private SessionDiagnostics[] SnapshotSessions()
    {
        if (_sessionsProvider is null) return Array.Empty<SessionDiagnostics>();
        return _sessionsProvider().ToArray();
    }

    // Hand-rolled JSON to keep the slim builder lean (no source-gen, no
    // Newtonsoft). All field values are well-formed by construction
    // except SessionId/FirmId/AttachedTransportId which are routed
    // through JsonEscape.

    private static string SerializeSessions(SessionDiagnostics[] sessions)
    {
        var sb = new System.Text.StringBuilder(64 + sessions.Length * 256);
        sb.Append('[');
        for (int i = 0; i < sessions.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendSession(sb, sessions[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string SerializeSession(SessionDiagnostics s)
    {
        var sb = new System.Text.StringBuilder(256);
        AppendSession(sb, s);
        return sb.ToString();
    }

    private static void AppendSession(System.Text.StringBuilder sb, SessionDiagnostics s)
    {
        sb.Append('{');
        sb.Append("\"sessionId\":\"").Append(JsonEscape(s.SessionId)).Append("\",");
        sb.Append("\"firmId\":\"").Append(JsonEscape(s.FirmId)).Append("\",");
        sb.Append("\"state\":").Append(s.State.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"sessionVerId\":").Append(s.SessionVerId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"outboundSeq\":").Append(s.OutboundSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"inboundExpectedSeq\":").Append(s.InboundExpectedSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"retxBufferDepth\":").Append(s.RetxBufferDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"sendQueueDepth\":").Append(s.SendQueueDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"attachedTransportId\":");
        if (s.AttachedTransportId is null) sb.Append("null"); else sb.Append('"').Append(JsonEscape(s.AttachedTransportId)).Append('"');
        sb.Append(',');
        sb.Append("\"lastActivityAtMs\":").Append(s.LastActivityAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');
    }

    private static string SerializeFirms(IReadOnlyList<FirmInfo> firms)
    {
        var sb = new System.Text.StringBuilder(64 + firms.Count * 96);
        sb.Append('[');
        for (int i = 0; i < firms.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = firms[i];
            sb.Append("{\"id\":\"").Append(JsonEscape(f.Id)).Append("\",")
              .Append("\"name\":\"").Append(JsonEscape(f.Name)).Append("\",")
              .Append("\"enteringFirmCode\":").Append(f.EnteringFirmCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string JsonEscape(string s)
    {
        // Fast path: no escaping needed when there are no quotes, backslashes,
        // or control chars (< U+0020).
        bool needs = false;
        foreach (var c in s)
        {
            if (c == '\\' || c == '"' || c < 0x20) { needs = true; break; }
        }
        if (!needs) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private IResult HandleOperatorEnqueue(HttpContext ctx, int channelNumber,
        Func<ChannelDispatcher, bool> enqueue, string opName)
    {
        if (channelNumber < 0 || channelNumber > 255 ||
            !_dispatchers.TryGetValue((byte)channelNumber, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channelNumber}\n", "text/plain");
        }
        if (!enqueue(disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {channelNumber} inbound queue full\n", "text/plain");
        }
        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        return Results.Text($"accepted {opName} channel={channelNumber}\n", "text/plain");
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
