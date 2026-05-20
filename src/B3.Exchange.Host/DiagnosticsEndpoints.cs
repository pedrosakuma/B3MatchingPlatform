using System.Net;
using B3.Exchange.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace B3.Exchange.Host;

/// <summary>
/// Read-only diagnostics endpoints.
/// </summary>
internal sealed class DiagnosticsEndpoints
{
    private readonly HttpConfig _config;
    private readonly MetricsRegistry _metrics;
    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly Func<IEnumerable<SessionDiagnostics>>? _sessionsProvider;
    private readonly IReadOnlyList<FirmInfo> _firms;

    public DiagnosticsEndpoints(HttpConfig config, MetricsRegistry metrics,
        IReadOnlyList<IReadinessProbe> probes,
        Func<IEnumerable<SessionDiagnostics>>? sessionsProvider,
        IReadOnlyList<FirmInfo> firms)
    {
        _config = config;
        _metrics = metrics;
        _probes = probes;
        _sessionsProvider = sessionsProvider;
        _firms = firms;
    }

    public void Map(IEndpointRouteBuilder app)
    {
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
    }

    private SessionDiagnostics[] SnapshotSessions()
    {
        if (_sessionsProvider is null) return Array.Empty<SessionDiagnostics>();
        return _sessionsProvider().ToArray();
    }

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

    private static string JsonEscape(string s) => HttpInternals.JsonEscape(s);
}
