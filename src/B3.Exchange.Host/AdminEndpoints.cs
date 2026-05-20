using B3.Exchange.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace B3.Exchange.Host;

/// <summary>
/// Host-wide admin endpoints: daily-reset, EOD export, and snapshot
/// inspection/management (GET / force / reset / validate). All admin
/// routes are network-gated (no token auth) per repository convention.
/// </summary>
internal sealed class AdminEndpoints
{
    private readonly IReadOnlyDictionary<byte, ChannelDispatcher> _dispatchers;
    private readonly IReadOnlyDictionary<byte, IChannelStatePersister> _persisters;
    private readonly IReadOnlyDictionary<byte, IChannelWriteAheadLog> _wals;
    private readonly Func<int>? _dailyResetTrigger;
    private readonly Func<byte, DateOnly, B3.Exchange.PostTrade.EodFillsExportResult?>? _eodExportTrigger;
    private readonly Action<string>? _log;

    public AdminEndpoints(
        IReadOnlyDictionary<byte, ChannelDispatcher> dispatchers,
        IReadOnlyDictionary<byte, IChannelStatePersister> persisters,
        IReadOnlyDictionary<byte, IChannelWriteAheadLog> wals,
        Func<int>? dailyResetTrigger,
        Func<byte, DateOnly, B3.Exchange.PostTrade.EodFillsExportResult?>? eodExportTrigger,
        Action<string>? log)
    {
        _dispatchers = dispatchers;
        _persisters = persisters;
        _wals = wals;
        _dailyResetTrigger = dailyResetTrigger;
        _eodExportTrigger = eodExportTrigger;
        _log = log;
    }

    public void Map(IEndpointRouteBuilder app)
    {
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
            HandleSnapshotForce(ctx, ch));

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
    }

    private IResult HandleSnapshotForce(HttpContext ctx, int channelNumber)
    {
        if (channelNumber < 0 || channelNumber > 255 ||
            !_dispatchers.TryGetValue((byte)channelNumber, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channelNumber}\n", "text/plain");
        }
        if (!disp.EnqueueOperatorPersistSnapshot())
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {channelNumber} inbound queue full\n", "text/plain");
        }
        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        return Results.Text($"accepted snapshot-force channel={channelNumber}\n", "text/plain");
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
        var json = System.Text.Json.JsonSerializer.Serialize(snap, HttpInternals.AdminJsonOptions);
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
}
