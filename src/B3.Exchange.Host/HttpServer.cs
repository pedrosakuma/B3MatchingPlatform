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
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _instrumentRouting;
    private readonly MetricsRegistry _metrics;
    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly IReadOnlyDictionary<byte, ChannelDispatcher> _dispatchers;
    private readonly Func<IEnumerable<SessionDiagnostics>>? _sessionsProvider;
    private readonly IReadOnlyList<FirmInfo> _firms;
    private readonly Func<int>? _dailyResetTrigger;
    private readonly Func<byte, DateOnly, B3.Exchange.PostTrade.EodFillsExportResult?>? _eodExportTrigger;
    private readonly IReadOnlyDictionary<byte, IChannelStatePersister> _persisters;
    private readonly IReadOnlyDictionary<byte, IChannelWriteAheadLog> _wals;
    private readonly Action<string>? _log;
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
        Func<byte, DateOnly, B3.Exchange.PostTrade.EodFillsExportResult?>? eodExportTrigger = null)
    {
        _config = config;
        _metrics = metrics;
        _probes = probes;
        _dispatchers = dispatchers;
        _sessionsProvider = sessionsProvider;
        _firms = firms ?? Array.Empty<FirmInfo>();
        _dailyResetTrigger = dailyResetTrigger;
        _persisters = persisters ?? new Dictionary<byte, IChannelStatePersister>();
        _wals = wals ?? new Dictionary<byte, IChannelWriteAheadLog>();
        _instrumentRouting = instrumentRouting ?? new Dictionary<long, ChannelDispatcher>();
        _eodExportTrigger = eodExportTrigger;
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

        // Issue #330 PR-2: trigger the post-trade EOD fills CSV export
        // for one channel + one UTC business date. The body of the request
        // is ignored; query string carries the parameters so curl-shaped
        // operator workflows match the rest of /admin/*.
        //
        //   POST /admin/post-trade/eod-export?channel=N&date=YYYY-MM-DD
        //
        // Returns 200 with a JSON body {csvPath, rowCount, sha256} on
        // success; 400 on missing/invalid query params; 404 when the
        // channel has no EOD export configured (postTradeAudit disabled
        // or eodDropDir empty); 404 again (with a distinct reason) when
        // the audit log for the requested date is missing; 500 with the
        // exception type+message on any other failure. The endpoint is
        // network-gated (no token auth) per the same convention as the
        // rest of /admin/*; PR-3 wires the daily-reset auto-trigger.
        app.MapPost("/admin/post-trade/eod-export", (HttpContext ctx) =>
            HandleEodExport(ctx));

        // Issue #271: admin endpoints for snapshot management.
        //
        // GET /admin/channels/{ch}/snapshot
        //   Reads the most recently persisted on-disk snapshot via the
        //   channel's IChannelStatePersister and returns it as JSON.
        //   This is the *durable* state, NOT the live in-memory state.
        //   To inspect live state first call POST .../snapshot/force
        //   then GET — that round-trip routes through the dispatch
        //   thread and persists atomically. 404 if the channel has no
        //   persister wired (persistence.dataDir empty); 204 if the
        //   persister has no snapshot yet.
        app.MapGet("/admin/channels/{ch:int}/snapshot", (int ch, HttpContext ctx) =>
            HandleAdminGetSnapshot(ctx, ch));

        // POST /admin/channels/{ch}/snapshot/force
        //   Alias of /channel/{ch}/snapshot-now under the /admin
        //   namespace for ops tooling discoverability. Enqueues an
        //   operator snapshot work item; the dispatch thread captures
        //   and persists. Returns 202 Accepted on enqueue success.
        app.MapPost("/admin/channels/{ch:int}/snapshot/force", (int ch, HttpContext ctx) =>
            HandleOperatorEnqueue(ctx, ch, d => d.EnqueueOperatorPersistSnapshot(), "snapshot-force"));

        // POST /admin/channels/{ch}/snapshot/reset?force=true
        //   Deletes every on-disk snapshot artifact (all rolling
        //   generations + legacy file) AND truncates/removes the WAL
        //   for the channel. The live in-memory engine state is NOT
        //   touched — restart the host to actually start the channel
        //   empty. Requires explicit ?force=true to acknowledge the
        //   irreversible nature of the operation.
        app.MapPost("/admin/channels/{ch:int}/snapshot/reset", (int ch, HttpContext ctx) =>
            HandleAdminReset(ctx, ch));

        // POST /admin/channels/{ch}/snapshot/validate
        //   Loads the most recently persisted snapshot and runs the
        //   structural validator the boot path uses (duplicate orderId
        //   check, owners-reference-known-orders, stop-record sanity,
        //   etc.) WITHOUT restoring it. 200 + "ok" on success;
        //   422 + reason on validation failure; 404 when no snapshot
        //   exists; 503 when no persister is wired.
        app.MapPost("/admin/channels/{ch:int}/snapshot/validate", (int ch, HttpContext ctx) =>
            HandleAdminValidate(ctx, ch));

        // Issue #321: operator-initiated trading-phase transition for a
        // single instrument. Body: { "targetPhase": "...", "asOf": <opt> }.
        // 200 with structured outcome on success; 404 unknown securityId;
        // 409 invalid transition (e.g. Open → Reserved); 503 inbound
        // queue full; 400 malformed body or unknown phase.
        app.MapPost("/admin/instruments/{securityId:long}/phase",
            async (long securityId, HttpContext ctx) =>
                await HandleSetPhaseAsync(ctx, securityId).ConfigureAwait(false));

        // Issue #322: operator-initiated single-stock halt and resume.
        // Halt body: { "reason": "RegulatoryHalt"|..., "note": "..." }.
        // Resume body: empty or { } — phase is preserved by the engine.
        // 200 on state change with structured outcome; 409 when re-halt
        // is a no-op (already halted) or resume targets an unhalted
        // instrument; 404 unknown securityId; 503 inbound queue full;
        // 400 malformed body; 504 dispatcher wedged.
        app.MapPost("/admin/instruments/{securityId:long}/halt",
            async (long securityId, HttpContext ctx) =>
                await HandleHaltAsync(ctx, securityId).ConfigureAwait(false));
        app.MapPost("/admin/instruments/{securityId:long}/resume",
            async (long securityId, HttpContext ctx) =>
                await HandleResumeAsync(ctx, securityId).ConfigureAwait(false));

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

    private IResult HandleAdminGetSnapshot(HttpContext ctx, int channelNumber)
    {
        if (channelNumber < 0 || channelNumber > 255)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channelNumber}\n", "text/plain");
        }
        if (!_persisters.TryGetValue((byte)channelNumber, out var persister))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"channel {channelNumber} has no persister wired\n", "text/plain");
        }
        ChannelStateSnapshot? snap;
        try { snap = persister.TryLoad((byte)channelNumber); }
        catch (Exception ex)
        {
            _log?.Invoke($"admin: snapshot-get channel={channelNumber} failed: {ex.Message}");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"failed to load snapshot: {ex.Message}\n", "text/plain");
        }
        if (snap is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Results.Empty;
        }
        _log?.Invoke($"admin: snapshot-get channel={channelNumber} returned snapshot version={snap.Version}");
        var json = System.Text.Json.JsonSerializer.Serialize(snap, AdminJsonOptions);
        return Results.Text(json, "application/json");
    }

    private IResult HandleEodExport(HttpContext ctx)
    {
        if (_eodExportTrigger is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text("eod-export not wired (host not started)\n", "text/plain");
        }

        var q = ctx.Request.Query;
        if (!q.TryGetValue("channel", out var chRaw) || !byte.TryParse(chRaw.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var channel))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'channel' (expected byte 0..255)\n", "text/plain");
        }
        if (!q.TryGetValue("date", out var dateRaw) || !DateOnly.TryParseExact(dateRaw.ToString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'date' (expected YYYY-MM-DD UTC)\n", "text/plain");
        }

        B3.Exchange.PostTrade.EodFillsExportResult? result;
        try
        {
            result = _eodExportTrigger(channel, date);
        }
        catch (FileNotFoundException ex)
        {
            _log?.Invoke($"admin: eod-export channel={channel} date={date:yyyy-MM-dd} audit log missing: {ex.FileName}");
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"audit log not found for channel={channel} date={date:yyyy-MM-dd}\n", "text/plain");
        }
        catch (ExchangeHost.EodExportInProgressException)
        {
            _log?.Invoke($"admin: eod-export channel={channel} date={date:yyyy-MM-dd} rejected: already in progress");
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return Results.Text($"eod-export already in progress for channel={channel} date={date:yyyy-MM-dd}\n", "text/plain");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"admin: eod-export channel={channel} date={date:yyyy-MM-dd} failed: {ex.GetType().Name}: {ex.Message}");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"eod-export failed: {ex.GetType().Name}: {ex.Message}\n", "text/plain");
        }

        if (result is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"channel {channel} has no EOD export configured (postTradeAudit.eodDropDir empty)\n", "text/plain");
        }

        _log?.Invoke($"admin: eod-export channel={channel} date={date:yyyy-MM-dd} rows={result.Value.RowCount} sha256={result.Value.Sha256Hex} path={result.Value.CsvPath}");
        var payload = "{"
            + "\"csvPath\":" + System.Text.Json.JsonSerializer.Serialize(result.Value.CsvPath)
            + ",\"rowCount\":" + result.Value.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"sha256\":\"" + result.Value.Sha256Hex + "\""
            + "}\n";
        return Results.Text(payload, "application/json");
    }

    private IResult HandleAdminReset(HttpContext ctx, int channelNumber)
    {
        if (channelNumber < 0 || channelNumber > 255)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channelNumber}\n", "text/plain");
        }
        var force = ctx.Request.Query["force"].ToString();
        if (!string.Equals(force, "true", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("snapshot/reset is irreversible; pass ?force=true to confirm\n", "text/plain");
        }
        bool any = false;
        int filesRemoved = 0;
        if (_persisters.TryGetValue((byte)channelNumber, out var persister))
        {
            any = true;
            try { filesRemoved += persister.DeleteAll((byte)channelNumber); }
            catch (Exception ex)
            {
                _log?.Invoke($"admin: snapshot-reset channel={channelNumber} persister failed: {ex.Message}");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Results.Text($"persister DeleteAll failed: {ex.Message}\n", "text/plain");
            }
        }
        if (_wals.TryGetValue((byte)channelNumber, out var wal))
        {
            any = true;
            try { wal.Reset(); }
            catch (Exception ex)
            {
                _log?.Invoke($"admin: snapshot-reset channel={channelNumber} wal reset failed: {ex.Message}");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Results.Text($"wal Reset failed: {ex.Message}\n", "text/plain");
            }
        }
        if (!any)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"channel {channelNumber} has no persister or WAL wired\n", "text/plain");
        }
        _log?.Invoke($"admin: snapshot-reset channel={channelNumber} files-removed={filesRemoved}");
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Text(
            $"reset channel={channelNumber} files-removed={filesRemoved} (in-memory state untouched; restart host for empty boot)\n",
            "text/plain");
    }

    private IResult HandleAdminValidate(HttpContext ctx, int channelNumber)
    {
        if (channelNumber < 0 || channelNumber > 255)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channelNumber}\n", "text/plain");
        }
        if (!_persisters.TryGetValue((byte)channelNumber, out var persister))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {channelNumber} has no persister wired\n", "text/plain");
        }
        ChannelStateSnapshot? snap;
        try { snap = persister.TryLoad((byte)channelNumber); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"failed to load snapshot: {ex.Message}\n", "text/plain");
        }
        if (snap is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"channel {channelNumber} has no persisted snapshot\n", "text/plain");
        }
        if (!ChannelDispatcher.TryValidateSnapshot(snap, out var err))
        {
            _log?.Invoke($"admin: snapshot-validate channel={channelNumber} INVALID: {err}");
            ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Results.Text($"invalid: {err}\n", "text/plain");
        }
        _log?.Invoke($"admin: snapshot-validate channel={channelNumber} ok version={snap.Version}");
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Text($"ok version={snap.Version} sequenceNumber={snap.SequenceNumber} lastAppliedSeq={snap.LastAppliedSeq}\n", "text/plain");
    }

    private static readonly System.Text.Json.JsonSerializerOptions AdminJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
        },
    };

    /// <summary>
    /// Issue #321: max time the HTTP request blocks waiting for the
    /// dispatch-thread completion of the phase change. Generous enough to
    /// absorb a transient queue spike but bounded so a wedged dispatcher
    /// surfaces as a 504 instead of hanging the operator's tooling.
    /// </summary>
    private static readonly TimeSpan PhaseChangeTimeout = TimeSpan.FromSeconds(5);

    private async Task<IResult> HandleSetPhaseAsync(HttpContext ctx, long securityId)
    {
        if (!_instrumentRouting.TryGetValue(securityId, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown securityId {securityId}\n", "text/plain");
        }
        SetPhaseRequest? body;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync<SetPhaseRequest>(
                ctx.Request.Body, AdminJsonOptions, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"malformed body: {ex.Message}\n", "text/plain");
        }
        if (body is null || string.IsNullOrWhiteSpace(body.TargetPhase))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("body must contain non-empty 'targetPhase'\n", "text/plain");
        }
        if (!Enum.TryParse<B3.Exchange.Matching.TradingPhase>(body.TargetPhase, ignoreCase: true, out var target))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"unknown targetPhase '{body.TargetPhase}'\n", "text/plain");
        }
        if (!disp.TryGetPhaseSnapshot(securityId, out var current))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown securityId {securityId} (no phase snapshot)\n", "text/plain");
        }
        bool useUncross =
            (current == B3.Exchange.Matching.TradingPhase.Reserved && target == B3.Exchange.Matching.TradingPhase.Open) ||
            (current == B3.Exchange.Matching.TradingPhase.FinalClosingCall && target == B3.Exchange.Matching.TradingPhase.Close);

        var tcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = useUncross
            ? disp.EnqueueOperatorUncrossAuction(securityId, target, tcs)
            : disp.EnqueueOperatorSetTradingPhase(securityId, target, tcs);
        if (!enqueued)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {disp.ChannelNumber} inbound queue full\n", "text/plain");
        }

        PhaseChangeOutcome outcome;
        try
        {
            outcome = await tcs.Task.WaitAsync(PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return Results.Text("timed out waiting for phase change\n", "text/plain");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return Results.Text($"invalid transition: {ex.Message}\n", "text/plain");
        }
        catch (KeyNotFoundException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"not found: {ex.Message}\n", "text/plain");
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"phase change failed: {ex.Message}\n", "text/plain");
        }

        _metrics.IncPhaseTransition(securityId, outcome.PreviousPhase, outcome.CurrentPhase, "operator");

        var responseJson = SerializePhaseOutcome(outcome);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Text(responseJson, "application/json");
    }

    private static string SerializePhaseOutcome(PhaseChangeOutcome o)
    {
        var sb = new System.Text.StringBuilder(96);
        sb.Append("{\"previousPhase\":\"").Append(o.PreviousPhase.ToString())
          .Append("\",\"currentPhase\":\"").Append(o.CurrentPhase.ToString())
          .Append("\"");
        if (o.UncrossPrint is { } p)
        {
            sb.Append(",\"uncrossPrint\":{\"kind\":\"")
              .Append(p.Kind.ToString())
              .Append("\",\"priceMantissa\":")
              .Append(p.PriceMantissa.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"clearedQuantity\":")
              .Append(p.ClearedQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private sealed class SetPhaseRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("targetPhase")]
        public string? TargetPhase { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("asOf")]
        public ulong? AsOf { get; set; }
    }

    private sealed class HaltRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("reason")]
        public string? Reason { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("note")]
        public string? Note { get; set; }
    }

    private async Task<IResult> HandleHaltAsync(HttpContext ctx, long securityId)
    {
        if (!_instrumentRouting.TryGetValue(securityId, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown securityId {securityId}\n", "text/plain");
        }
        HaltRequest? body;
        try
        {
            body = await System.Text.Json.JsonSerializer.DeserializeAsync<HaltRequest>(
                ctx.Request.Body, AdminJsonOptions, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"malformed body: {ex.Message}\n", "text/plain");
        }
        if (body is null || string.IsNullOrWhiteSpace(body.Reason))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("body must contain non-empty 'reason'\n", "text/plain");
        }
        if (!Enum.TryParse<B3.Exchange.Matching.HaltReason>(body.Reason, ignoreCase: true, out var reason)
            || reason == default
            || !Enum.IsDefined(typeof(B3.Exchange.Matching.HaltReason), reason))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"unknown reason '{body.Reason}'\n", "text/plain");
        }

        var tcs = new TaskCompletionSource<HaltOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!disp.EnqueueOperatorHalt(securityId, reason, body.Note, tcs))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {disp.ChannelNumber} inbound queue full\n", "text/plain");
        }

        HaltOutcome outcome;
        try
        {
            outcome = await tcs.Task.WaitAsync(PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return Results.Text("timed out waiting for halt\n", "text/plain");
        }
        catch (KeyNotFoundException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"not found: {ex.Message}\n", "text/plain");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return Results.Text($"halt rejected: {ex.Message}\n", "text/plain");
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"halt failed: {ex.Message}\n", "text/plain");
        }

        // Issue #322 review (gpt-5.5): halt is idempotent per the contract
        // ("re-halt mesma reason = no-op + 200"). A no-op halt returns 200
        // with alreadyHalted=true so operator tooling can distinguish a
        // newly-applied halt from a redundant one without treating idempotent
        // calls as errors. Resume keeps 409 for not-halted (handled below).
        if (outcome.StateChanged) _metrics.IncInstrumentHalted(securityId, reason);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Text(SerializeHaltOutcome(outcome, alreadyHalted: !outcome.StateChanged), "application/json");
    }

    private sealed class ResumeRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("phase")]
        public string? Phase { get; set; }
    }

    private async Task<IResult> HandleResumeAsync(HttpContext ctx, long securityId)
    {
        if (!_instrumentRouting.TryGetValue(securityId, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown securityId {securityId}\n", "text/plain");
        }

        // Issue #322 review (gpt-5.5): /resume optionally accepts a phase to
        // chain a SetTradingPhase / UncrossAuction immediately after the
        // resume succeeds. Previously the body was ignored, so callers got
        // a misleading 200 with the phase unchanged. Body is optional;
        // empty/missing skips the chain.
        B3.Exchange.Matching.TradingPhase? requestedPhase = null;
        if (ctx.Request.ContentLength > 0 || (ctx.Request.Headers.TryGetValue("Content-Type", out var ct) && ct.ToString().Contains("json", StringComparison.OrdinalIgnoreCase)))
        {
            ResumeRequest? body;
            try
            {
                body = await System.Text.Json.JsonSerializer.DeserializeAsync<ResumeRequest>(
                    ctx.Request.Body, AdminJsonOptions, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Results.Text($"malformed body: {ex.Message}\n", "text/plain");
            }
            if (body is { Phase: { Length: > 0 } phaseStr })
            {
                if (!Enum.TryParse<B3.Exchange.Matching.TradingPhase>(phaseStr, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(typeof(B3.Exchange.Matching.TradingPhase), parsed))
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Results.Text($"unknown phase '{phaseStr}'\n", "text/plain");
                }
                requestedPhase = parsed;
            }
        }

        var tcs = new TaskCompletionSource<HaltOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!disp.EnqueueOperatorResume(securityId, tcs))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {disp.ChannelNumber} inbound queue full\n", "text/plain");
        }

        HaltOutcome outcome;
        try
        {
            outcome = await tcs.Task.WaitAsync(PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return Results.Text("timed out waiting for resume\n", "text/plain");
        }
        catch (KeyNotFoundException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"not found: {ex.Message}\n", "text/plain");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return Results.Text($"resume rejected: {ex.Message}\n", "text/plain");
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"resume failed: {ex.Message}\n", "text/plain");
        }

        if (!outcome.StateChanged)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            return Results.Text(SerializeHaltOutcome(outcome), "application/json");
        }
        _metrics.IncInstrumentResumed(securityId);

        // Chain optional phase change. Errors here do NOT roll back the
        // resume (already applied + persisted); we surface a 200 with a
        // chainedPhaseError field so the caller can react.
        string? chainedPhase = null;
        string? chainError = null;
        if (requestedPhase is { } target)
        {
            chainedPhase = target.ToString();
            disp.TryGetPhaseSnapshot(securityId, out var current);
            bool useUncross =
                (current == B3.Exchange.Matching.TradingPhase.Reserved && target == B3.Exchange.Matching.TradingPhase.Open) ||
                (current == B3.Exchange.Matching.TradingPhase.FinalClosingCall && target == B3.Exchange.Matching.TradingPhase.Close);
            var phaseTcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = useUncross
                ? disp.EnqueueOperatorUncrossAuction(securityId, target, phaseTcs)
                : disp.EnqueueOperatorSetTradingPhase(securityId, target, phaseTcs);
            if (!enqueued)
            {
                chainError = "queue full";
            }
            else
            {
                try
                {
                    var phaseOutcome = await phaseTcs.Task.WaitAsync(PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
                    _metrics.IncPhaseTransition(securityId, phaseOutcome.PreviousPhase, phaseOutcome.CurrentPhase, "operator");
                }
                catch (Exception ex)
                {
                    chainError = ex.Message;
                }
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Text(SerializeHaltOutcome(outcome, chainedPhase: chainedPhase, chainError: chainError), "application/json");
    }

    private static string SerializeHaltOutcome(HaltOutcome o, bool? alreadyHalted = null, string? chainedPhase = null, string? chainError = null)
    {
        var sb = new System.Text.StringBuilder(128);
        sb.Append("{\"stateChanged\":").Append(o.StateChanged ? "true" : "false")
          .Append(",\"isHaltedNow\":").Append(o.IsHaltedNow ? "true" : "false");
        if (alreadyHalted is { } ah)
        {
            sb.Append(",\"alreadyHalted\":").Append(ah ? "true" : "false");
        }
        if (o.Reason is { } r)
        {
            sb.Append(",\"reason\":\"").Append(r.ToString()).Append('"');
        }
        if (o.HaltedAtNanos != 0UL)
        {
            sb.Append(",\"haltedAtNanos\":")
              .Append(o.HaltedAtNanos.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(o.Note))
        {
            sb.Append(",\"note\":\"").Append(JsonEscape(o.Note)).Append('"');
        }
        if (!string.IsNullOrEmpty(chainedPhase))
        {
            sb.Append(",\"chainedPhase\":\"").Append(JsonEscape(chainedPhase)).Append('"');
        }
        if (!string.IsNullOrEmpty(chainError))
        {
            sb.Append(",\"chainedPhaseError\":\"").Append(JsonEscape(chainError)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
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
