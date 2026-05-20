using B3.Exchange.Contracts.Time;
using B3.Exchange.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace B3.Exchange.Host;

/// <summary>
/// Operator-facing endpoints: channel-scoped mutations (snapshot-now,
/// bump-version, trade-bust) and instrument-scoped mutations (phase,
/// halt, resume). All work is dispatched through the channel's inbound
/// queue so engine state mutates on the dispatch thread.
/// </summary>
internal sealed class OperatorEndpoints
{
    private readonly IReadOnlyDictionary<byte, ChannelDispatcher> _dispatchers;
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _instrumentRouting;
    private readonly MetricsRegistry _metrics;
    private readonly INanosTimeSource _timeSource;

    public OperatorEndpoints(
        IReadOnlyDictionary<byte, ChannelDispatcher> dispatchers,
        IReadOnlyDictionary<long, ChannelDispatcher> instrumentRouting,
        MetricsRegistry metrics,
        INanosTimeSource timeSource)
    {
        _dispatchers = dispatchers;
        _instrumentRouting = instrumentRouting;
        _metrics = metrics;
        _timeSource = timeSource;
    }

    public void Map(IEndpointRouteBuilder app)
    {
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

        // ADR 0008 PR-2: operator trade-bust endpoint with full
        // validation surface. Distinct from the legacy
        // /channel/{ch}/trade-bust/{tradeId} replay path (which does no
        // validation and no audit write). Query parameters:
        //   channel        - channel number (0-255)
        //   tradeId        - the engine TradeId of the trade to bust
        //   tradeDate      - YYYY-MM-DD of the busted trade's day
        //   correlationId  - operator-supplied dedup key (ulong)
        //   reason         - optional ushort reason code (default 0)
        //   securityId     - optional long echo (server validates against the fill)
        //   busterFirm     - optional uint operator firm id (default 0)
        // Status codes mirror ADR §2.3.
        app.MapPost("/admin/post-trade/bust",
            (Delegate)(async (HttpContext ctx) => await HandlePostTradeBustAsync(ctx).ConfigureAwait(false)));

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
        if (!HttpInternals.TryParseLong(q["securityId"], out long securityId) || securityId <= 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'securityId' query parameter\n", "text/plain");
        }
        if (!HttpInternals.TryParseLong(q["priceMantissa"], out long priceMantissa, allowMissing: true))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("invalid 'priceMantissa' query parameter\n", "text/plain");
        }
        if (!HttpInternals.TryParseLong(q["size"], out long size, allowMissing: true) || size < 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("invalid 'size' query parameter\n", "text/plain");
        }
        ushort tradeDate;
        if (q.ContainsKey("tradeDate"))
        {
            if (!HttpInternals.TryParseLong(q["tradeDate"], out long td) || td < 0 || td > ushort.MaxValue)
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

    private async Task<IResult> HandlePostTradeBustAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        if (!HttpInternals.TryParseLong(q["channel"], out long chRaw) || chRaw < 0 || chRaw > 255)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'channel' (0..255)\n", "text/plain");
        }
        byte channel = (byte)chRaw;
        if (!_dispatchers.TryGetValue(channel, out var disp))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Text($"unknown channel {channel}\n", "text/plain");
        }
        if (!HttpInternals.TryParseLong(q["tradeId"], out long tidRaw) || tidRaw <= 0 || tidRaw > uint.MaxValue)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"missing or invalid 'tradeId' (1..{uint.MaxValue})\n", "text/plain");
        }
        uint tradeId = (uint)tidRaw;
        string tradeDateStr = q["tradeDate"].ToString();
        if (string.IsNullOrEmpty(tradeDateStr)
            || !DateOnly.TryParseExact(tradeDateStr, "yyyy-MM-dd", out var tradeDate))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'tradeDate' (YYYY-MM-DD)\n", "text/plain");
        }
        // UMDF TradeBust_57 encodes tradeDate as LocalMktDate (uint16,
        // days since 1970-01-01). Range-check up front so accepted
        // requests never crash mid-dispatch after writing audit state.
        int tradeDateDaysSinceEpoch = tradeDate.DayNumber - HttpInternals.LocalMktDateEpoch.DayNumber;
        if (tradeDateDaysSinceEpoch < 0 || tradeDateDaysSinceEpoch > ushort.MaxValue)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text($"'tradeDate' out of LocalMktDate range (1970-01-01..{HttpInternals.LocalMktDateEpoch.AddDays(ushort.MaxValue):yyyy-MM-dd})\n", "text/plain");
        }
        if (!HttpInternals.TryParseULong(q["correlationId"], out ulong correlationId) || correlationId == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Results.Text("missing or invalid 'correlationId' (> 0)\n", "text/plain");
        }
        ushort reason = 0;
        if (q.ContainsKey("reason"))
        {
            if (!HttpInternals.TryParseLong(q["reason"], out long r) || r < 0 || r > ushort.MaxValue)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Results.Text($"invalid 'reason' (0..{ushort.MaxValue})\n", "text/plain");
            }
            reason = (ushort)r;
        }
        long? securityIdEcho = null;
        if (q.ContainsKey("securityId"))
        {
            if (!HttpInternals.TryParseLong(q["securityId"], out long s) || s <= 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Results.Text("invalid 'securityId' echo (must be > 0 when supplied)\n", "text/plain");
            }
            securityIdEcho = s;
        }
        uint busterFirm = 0;
        if (q.ContainsKey("busterFirm"))
        {
            if (!HttpInternals.TryParseLong(q["busterFirm"], out long bf) || bf < 0 || bf > uint.MaxValue)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Results.Text($"invalid 'busterFirm' (0..{uint.MaxValue})\n", "text/plain");
            }
            busterFirm = (uint)bf;
        }

        var tcs = new TaskCompletionSource<B3.Exchange.Core.ChannelDispatcher.OperatorBustV2Outcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ulong nowNanos = _timeSource.NowNanos();
        if (!disp.EnqueueOperatorBustV2(tradeId, tradeDate, correlationId, reason, busterFirm,
                securityIdEcho, nowNanos, tcs))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"channel {channel} inbound queue full or not wired for bust-v2\n", "text/plain");
        }

        B3.Exchange.Core.ChannelDispatcher.OperatorBustV2Outcome outcome;
        try
        {
            outcome = await tcs.Task.WaitAsync(HttpInternals.PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            return Results.Text("timed out waiting for bust validation\n", "text/plain");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Text($"unavailable: {ex.Message}\n", "text/plain");
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Results.Text($"bust failed: {ex.Message}\n", "text/plain");
        }

        switch (outcome.Kind)
        {
            case B3.Exchange.PostTrade.BustValidationKind.Accept:
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Results.Text(
                    $"{{\"status\":\"busted\",\"channel\":{channel},\"tradeId\":{tradeId},\"correlationId\":{correlationId}}}",
                    "application/json");
            case B3.Exchange.PostTrade.BustValidationKind.IdempotentReplay:
                ctx.Response.Headers["X-Idempotent"] = "true";
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Results.Text(
                    $"{{\"status\":\"idempotent-replay\",\"channel\":{channel},\"tradeId\":{tradeId},\"correlationId\":{correlationId}}}",
                    "application/json");
            case B3.Exchange.PostTrade.BustValidationKind.MissingDay:
                ctx.Response.StatusCode = StatusCodes.Status410Gone;
                return Results.Text(
                    $"fills-{tradeDate:yyyy-MM-dd}.log missing for channel {channel}\n", "text/plain");
            case B3.Exchange.PostTrade.BustValidationKind.UnknownTradeId:
                ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return Results.Text(
                    $"{{\"status\":\"reject\",\"reason\":\"unknown-trade-id\",\"code\":1}}",
                    "application/json");
            case B3.Exchange.PostTrade.BustValidationKind.AlreadyBustedDifferentCorrelation:
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                return Results.Text(
                    $"{{\"status\":\"reject\",\"reason\":\"already-busted\",\"code\":2,\"existingCorrelationId\":{outcome.ExistingCorrelationId}}}",
                    "application/json");
            case B3.Exchange.PostTrade.BustValidationKind.SecurityIdMismatch:
                ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return Results.Text(
                    $"{{\"status\":\"reject\",\"reason\":\"security-id-mismatch\",\"code\":3}}",
                    "application/json");
            default:
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Results.Text($"unexpected outcome {outcome.Kind}\n", "text/plain");
        }
    }

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
                ctx.Request.Body, HttpInternals.AdminJsonOptions, ctx.RequestAborted).ConfigureAwait(false);
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
            outcome = await tcs.Task.WaitAsync(HttpInternals.PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
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
                ctx.Request.Body, HttpInternals.AdminJsonOptions, ctx.RequestAborted).ConfigureAwait(false);
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
            outcome = await tcs.Task.WaitAsync(HttpInternals.PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
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
                    ctx.Request.Body, HttpInternals.AdminJsonOptions, ctx.RequestAborted).ConfigureAwait(false);
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
            outcome = await tcs.Task.WaitAsync(HttpInternals.PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
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
                    var phaseOutcome = await phaseTcs.Task.WaitAsync(HttpInternals.PhaseChangeTimeout, ctx.RequestAborted).ConfigureAwait(false);
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
            sb.Append(",\"note\":\"").Append(HttpInternals.JsonEscape(o.Note)).Append('"');
        }
        if (!string.IsNullOrEmpty(chainedPhase))
        {
            sb.Append(",\"chainedPhase\":\"").Append(HttpInternals.JsonEscape(chainedPhase)).Append('"');
        }
        if (!string.IsNullOrEmpty(chainError))
        {
            sb.Append(",\"chainedPhaseError\":\"").Append(HttpInternals.JsonEscape(chainError)).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }
}
