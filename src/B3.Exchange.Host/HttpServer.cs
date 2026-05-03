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
    private readonly Func<int>? _dailyResetTrigger;
    private readonly Action<string>? _log;
    private WebApplication? _app;

    public HttpServer(HttpConfig config, MetricsRegistry metrics,
        IReadOnlyList<IReadinessProbe> probes,
        IReadOnlyDictionary<byte, ChannelDispatcher> dispatchers,
        Action<string>? log = null,
        Func<IEnumerable<SessionDiagnostics>>? sessionsProvider = null,
        IReadOnlyList<FirmInfo>? firms = null,
        Func<int>? dailyResetTrigger = null)
    {
        _config = config;
        _metrics = metrics;
        _probes = probes;
        _dispatchers = dispatchers;
        _sessionsProvider = sessionsProvider;
        _firms = firms ?? Array.Empty<FirmInfo>();
        _dailyResetTrigger = dailyResetTrigger;
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

        // Operator endpoint (issue #15): trade-bust replay. tradeId is in
        // the path; the echo fields the consumer audits (securityId, price,
        // size, tradeDate) come as query-string parameters so the call is
        // shell-friendly. priceMantissa/size default to 0; tradeDate
        // defaults to today's UTC date encoded as LocalMktDate (days since
        // 1970-01-01) when omitted. Returns 400 for malformed input.
        app.MapPost("/channel/{ch:int}/trade-bust/{tradeId:long}", (int ch, long tradeId, HttpContext ctx) =>
            HandleTradeBust(ctx, ch, tradeId));

        // #GAP-09 (#47): on-demand trading-day rollover. Terminates every
        // live FIXP session so clients reconnect with Negotiate +
        // Establish(nextSeqNo=1). The scheduled timer (HostConfig.dailyReset)
        // fires the same path. Returns 202 Accepted with the count of
        // sessions terminated (0 if the listener has none); 503 if the
        // host has not yet finished StartAsync.
        app.MapPost("/admin/daily-reset", (HttpContext ctx) =>
        {
            if (_dailyResetTrigger is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Results.Text("daily-reset not wired (host not started)\n", "text/plain");
            }
            int closed = _dailyResetTrigger();
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return Results.Text($"accepted daily-reset terminated={closed}\n", "text/plain");
        });

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

    private IResult HandleTradeBust(HttpContext ctx, int channelNumber, long tradeId)
    {
        if (channelNumber < 0 || channelNumber > 255 ||
            !_dispatchers.TryGetValue((byte)channelNumber, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channelNumber}\n", "text/plain");
        }
        if (tradeId <= 0 || tradeId > uint.MaxValue)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"tradeId must be in (0, {uint.MaxValue}]\n", "text/plain");
        }
        var q = ctx.Request.Query;
        if (!TryParseLong(q["securityId"], out long securityId) || securityId <= 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'securityId' query parameter\n", "text/plain");
        }
        if (!TryParseLong(q["priceMantissa"], out long priceMantissa, allowMissing: true))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("invalid 'priceMantissa' query parameter\n", "text/plain");
        }
        if (!TryParseLong(q["size"], out long size, allowMissing: true) || size < 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("invalid 'size' query parameter\n", "text/plain");
        }
        ushort tradeDate;
        if (q.ContainsKey("tradeDate"))
        {
            if (!TryParseLong(q["tradeDate"], out long td) || td < 0 || td > ushort.MaxValue)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Results.Text($"invalid 'tradeDate' (LocalMktDate; days since 1970-01-01, 0..{ushort.MaxValue})\n", "text/plain");
            }
            tradeDate = (ushort)td;
        }
        else
        {
            tradeDate = (ushort)(DateTimeOffset.UtcNow.Date - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
        }

        if (!disp.EnqueueOperatorTradeBust(securityId, priceMantissa, size, (uint)tradeId, tradeDate))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {channelNumber} inbound queue full\n", "text/plain");
        }
        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        return Results.Text(
            $"accepted trade-bust channel={channelNumber} tradeId={tradeId} securityId={securityId}\n",
            "text/plain");
    }

    private static bool TryParseLong(Microsoft.Extensions.Primitives.StringValues values, out long value, bool allowMissing = false)
    {
        value = 0;
        var s = values.ToString();
        if (string.IsNullOrEmpty(s)) return allowMissing;
        return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
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
