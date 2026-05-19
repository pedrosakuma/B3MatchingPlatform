namespace B3.Exchange.PostTrade;

/// <summary>Validation outcome for a single operator bust request
/// (ADR 0008 §2.3). Each case maps to a specific HTTP status code in
/// the host's <c>POST /admin/post-trade/bust</c> handler.</summary>
public enum BustValidationKind : byte
{
    /// <summary>First-time accept — write bust record + emit UMDF
    /// TradeBust_57 frame. HTTP 200 "busted".</summary>
    Accept = 0,
    /// <summary>Same (tradeId, correlationId, tradeDate) already
    /// recorded — no write, no UMDF emit. HTTP 200 with
    /// <c>X-Idempotent: true</c>.</summary>
    IdempotentReplay = 1,
    /// <summary><c>fills-&lt;tradeDate&gt;.log</c> does not exist.
    /// HTTP 410. NO audit-log write (ADR §2.3).</summary>
    MissingDay = 2,
    /// <summary>tradeId not present in <c>fills-&lt;tradeDate&gt;.log</c>.
    /// HTTP 422, reject-attempt code 1.</summary>
    UnknownTradeId = 3,
    /// <summary>tradeId already busted with a different correlationId.
    /// HTTP 409, reject-attempt code 2.</summary>
    AlreadyBustedDifferentCorrelation = 4,
    /// <summary>Caller-supplied securityId echo doesn't match the fill.
    /// HTTP 422, reject-attempt code 3.</summary>
    SecurityIdMismatch = 5,
}

/// <summary>Caller-supplied request shape for the bust validator. Held
/// flat (no nesting) so it can be assembled directly from HTTP query
/// parameters at the dispatcher boundary.</summary>
public readonly record struct BustRequest(
    uint TradeId,
    DateOnly TradeDate,
    ulong CorrelationId,
    long? SecurityIdEcho,
    ushort ReasonCode,
    uint BusterFirm,
    ulong AttemptTransactTimeNanos);

/// <summary>Result of a single validation pass.
/// <see cref="MatchedFill"/> is populated on <see cref="BustValidationKind.Accept"/>
/// so callers can craft the UMDF frame without re-scanning the log.</summary>
public readonly record struct BustValidationResult(
    BustValidationKind Kind,
    PostTradeRecord MatchedFill,
    ulong ExistingCorrelationId);

/// <summary>Stateless validator for operator bust requests
/// (ADR 0008 §2.3). All disk access funnels through
/// <see cref="AuditLogReader"/>; the dedup index is owned by the
/// caller so the validator stays purely functional.</summary>
public static class BustValidator
{
    public static BustValidationResult Validate(
        string rootDir,
        byte channel,
        in BustRequest request,
        BustDedupIndex dedup)
    {
        // 1) Idempotency check first — replaying the exact same (tradeId,
        // correlationId, tradeDate) is a 200 even if the file no longer
        // exists.
        if (dedup.TryGet(request.TradeId, out var prior))
        {
            if (prior.CorrelationId == request.CorrelationId && prior.TradeDate == request.TradeDate)
                return new BustValidationResult(BustValidationKind.IdempotentReplay, default, prior.CorrelationId);
            return new BustValidationResult(BustValidationKind.AlreadyBustedDifferentCorrelation, default, prior.CorrelationId);
        }

        // 2) File-existence check (HTTP 410).
        var logPath = Path.Combine(
            rootDir,
            channel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"fills-{request.TradeDate:yyyy-MM-dd}.log");
        if (!File.Exists(logPath))
            return new BustValidationResult(BustValidationKind.MissingDay, default, 0);

        // 3) Locate the fill by tradeId.
        PostTradeRecord? matched = null;
        foreach (var entry in AuditLogReader.ReadAllEntries(logPath))
        {
            if (entry.Kind != AuditRecordKind.Fill) continue;
            if (entry.Fill.TradeId == request.TradeId)
            {
                matched = entry.Fill;
                break;
            }
        }
        if (matched is null)
            return new BustValidationResult(BustValidationKind.UnknownTradeId, default, 0);

        // 4) Optional securityId echo check.
        if (request.SecurityIdEcho is { } echo && echo != matched.Value.SecurityId)
            return new BustValidationResult(BustValidationKind.SecurityIdMismatch, matched.Value, 0);

        return new BustValidationResult(BustValidationKind.Accept, matched.Value, 0);
    }
}
