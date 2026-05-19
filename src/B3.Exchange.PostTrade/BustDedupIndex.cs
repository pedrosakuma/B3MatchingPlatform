namespace B3.Exchange.PostTrade;

/// <summary>
/// Per-channel idempotency index for operator trade-bust requests
/// (ADR 0008 §2.1). Maps <c>tradeId → (correlationId, tradeDate)</c> for
/// every accepted bust visible within the rolling retention window. The
/// index lives on the dispatch thread and is mutated only by the
/// bust-processing code path.
///
/// <para>Rebuilt at startup by scanning audit files in
/// <c>{rootDir}/{channel}/</c> via
/// <see cref="AuditLogReader.ReadAllEntries(string)"/> and filtering for
/// <see cref="AuditRecordKind.Bust"/> records. Only files whose date
/// falls within <c>[today - retentionDays, today]</c> are inspected;
/// this window must match <see cref="FileAuditLogWriter.PruneOldDays"/>
/// so every still-retained bust is reflected here on restart.</para>
/// </summary>
public sealed class BustDedupIndex
{
    private readonly Dictionary<uint, Entry> _byTradeId = new();

    public readonly record struct Entry(ulong CorrelationId, DateOnly TradeDate);

    public int Count => _byTradeId.Count;

    public bool TryGet(uint tradeId, out Entry entry) => _byTradeId.TryGetValue(tradeId, out entry);

    /// <summary>Records an accepted bust. Last write wins so a replay that
    /// re-applies the same record is idempotent. The caller is responsible
    /// for routing replays through <see cref="TryGet"/> first.</summary>
    public void Add(uint tradeId, ulong correlationId, DateOnly tradeDate)
        => _byTradeId[tradeId] = new Entry(correlationId, tradeDate);

    /// <summary>
    /// Rebuilds the index by scanning <c>{rootDir}/{channel}/fills-*.log</c>
    /// files whose embedded business date falls within
    /// <c>[todayUtc - retentionDays, todayUtc]</c> — the same window
    /// <see cref="FileAuditLogWriter.PruneOldDays"/> preserves. When
    /// <paramref name="retentionDays"/> is 0 (= unlimited retention) every
    /// file under the channel directory is scanned.
    ///
    /// <para>Bust records carry the day they reference in the on-disk
    /// record (the file name carries the day they were WRITTEN, which is
    /// the validator's "today" at the time of the operator request) —
    /// the validator's idempotency check needs the referenced day, so it
    /// is the <see cref="BustRecord"/> body that wins. The file-name day
    /// is only used to skip files outside the retention window.</para>
    /// </summary>
    public static BustDedupIndex LoadFromAuditFiles(string rootDir, byte channel, int retentionDays, DateOnly todayUtc)
    {
        var index = new BustDedupIndex();
        var channelDir = Path.Combine(rootDir, channel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(channelDir)) return index;

        DateOnly? minDate = retentionDays > 0
            ? todayUtc.AddDays(-retentionDays)
            : null;

        foreach (var path in Directory.EnumerateFiles(channelDir, "fills-*.log"))
        {
            var fileDate = TryParseDateFromFileName(path);
            if (fileDate is null) continue;
            if (minDate is { } floor && fileDate.Value < floor) continue;
            foreach (var entry in AuditLogReader.ReadAllEntries(path))
            {
                if (entry.Kind != AuditRecordKind.Bust) continue;
                var bust = entry.Bust;
                // ADR 0008 v3 BustRecord carries the original trade's
                // day (declaredTradeDate) so post-EOD busts — written
                // to fills-<bustToday>.log, not fills-<tradeDate>.log —
                // still attribute correctly. v2 records leave the field
                // as Absent (-1); fall back to the file-name day, which
                // for v2 always equals the trade's day per the PR-2
                // writer contract.
                var resolvedDate = bust.DeclaredTradeDateDays >= 0
                    ? DateOnly.FromDayNumber(bust.DeclaredTradeDateDays + EpochUnixDayNumber)
                    : fileDate.Value;
                index._byTradeId[bust.CancelledTradeId] = new Entry(bust.CorrelationId, resolvedDate);
            }
        }
        return index;
    }

    private static DateOnly? TryParseDateFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length != "fills-YYYY-MM-DD".Length) return null;
        if (!name.StartsWith("fills-", StringComparison.Ordinal)) return null;
        return DateOnly.TryParseExact(name.AsSpan("fills-".Length), "yyyy-MM-dd", out var d) ? d : null;
    }

    // Number of days between DateOnly.MinValue (0001-01-01) and the Unix
    // epoch (1970-01-01). BustRecord.DeclaredTradeDateDays uses the
    // SBE LocalMktDate convention (days since 1970-01-01).
    private static readonly int EpochUnixDayNumber = new DateOnly(1970, 1, 1).DayNumber;
}
