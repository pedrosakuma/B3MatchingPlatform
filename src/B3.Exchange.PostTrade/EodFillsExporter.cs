using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Result of a single <see cref="EodFillsExporter.Export"/> invocation —
/// the on-disk path to the published <c>fills.csv</c>, the row count
/// (excluding header), and a hex-encoded SHA-256 digest of the CSV bytes.
/// The same triple appears in the <c>fills.csv.done</c> sidecar so external
/// reconciliation consumers can verify integrity without re-hashing.
/// </summary>
public readonly record struct EodFillsExportResult(string CsvPath, long RowCount, string Sha256Hex);

/// <summary>
/// Projects the per-trade audit log (<see cref="FileAuditLogWriter"/>
/// output) for one channel + one UTC business date into the
/// "BVBG-like" EOD CSV drop required by ADR 0001 section 3 (issue #330 PR-1).
///
/// <para><b>Layout:</b> <c>{dropRootDir}/{channelNumber}/{YYYY-MM-DD}/fills.csv</c>
/// plus a sibling <c>fills.csv.done</c> sidecar whose presence is the
/// consumer-visible "ready" signal.</para>
///
/// <para><b>Atomic publish contract</b> (consumer must never see a partial
/// file or a stale signal — the <c>.done</c> sidecar must never sit next
/// to a different generation's <c>fills.csv</c>):</para>
/// <list type="number">
/// <item>Write CSV to a staging file <c>.fills.csv.tmp-{pid}-{guid}</c>
/// and <c>fsync</c> it via <see cref="FileStream.Flush(bool)"/>.</item>
/// <item>Write the <c>.done</c> JSON to a sibling staging file and
/// <c>fsync</c> it.</item>
/// <item><b>Invalidate</b> any prior <c>fills.csv.done</c> by removing it
/// and <c>fsync</c>'ing the parent dir. From this point a consumer polling
/// for <c>.done</c> sees "not ready" until step 5 completes.</item>
/// <item><c>rename(2)</c> the CSV staging file to <c>fills.csv</c> (POSIX
/// atomic) and <c>fsync</c> the parent directory.</item>
/// <item><c>rename(2)</c> the <c>.done</c> staging file to
/// <c>fills.csv.done</c> and <c>fsync</c> the parent directory. The
/// signal file is the LAST thing to land on disk and is always paired
/// with the CSV whose bytes its <c>sha256</c> describes.</item>
/// </list>
///
/// <para>Crash recovery: a crash between steps 3 and 5 leaves
/// <c>fills.csv</c> with no <c>.done</c> — consumers correctly observe
/// "not ready" and the next exporter run rebuilds both files. A crash
/// between steps 4 and 5 leaves a new <c>fills.csv</c> with no
/// <c>.done</c> — same recovery.</para>
///
/// <para>Failure modes (exception during projection): any in-process
/// exception before step 3 leaves the previously-published
/// <c>fills.csv</c> / <c>.done</c> pair untouched. After step 3 the old
/// <c>.done</c> is gone and the operator must rerun — partial output
/// never reaches the drop directory in either case. Staging files are
/// best-effort deleted in a <see langword="finally"/> block.</para>
///
/// <para>Idempotency: rerun for the same channel + date streams the audit
/// log deterministically, so <c>fills.csv</c> is byte-identical run-to-run
/// (only <c>generatedAt</c> inside <c>.done</c> changes).</para>
///
/// <para><b>Threading:</b> this class is stateless; concurrent
/// <see cref="Export"/> calls for distinct (channel, date) pairs are safe.
/// Concurrent calls for the SAME (channel, date) are racy by construction
/// (two renames into the same destination) — the operator endpoint and
/// daily-reset scheduler serialize per-channel calls upstream.</para>
/// </summary>
public sealed class EodFillsExporter
{
    private readonly ILogger<EodFillsExporter> _logger;

    public EodFillsExporter(ILogger<EodFillsExporter>? logger = null)
    {
        _logger = logger ?? NullLogger<EodFillsExporter>.Instance;
    }

    /// <summary>
    /// Projects the audit log for (<paramref name="channelNumber"/>,
    /// <paramref name="businessDate"/>) into an atomic CSV drop under
    /// <paramref name="dropRootDir"/>. See class-level docs for the
    /// contract and failure semantics.
    /// </summary>
    /// <param name="auditRootDir">The <see cref="FileAuditLogWriter"/>'s
    /// configured rootDir — the exporter resolves
    /// <c>{auditRootDir}/{channelNumber}/fills-{date}.log</c> from this.</param>
    /// <param name="dropRootDir">Root directory for EOD drops. The exporter
    /// creates per-channel + per-date subdirectories as needed.</param>
    /// <param name="channelNumber">UMDF channel number (matches the audit
    /// log directory layout).</param>
    /// <param name="businessDate">UTC business date selecting which audit
    /// log file to project.</param>
    /// <param name="symbolLookup">Maps a <see cref="long"/> SecurityId to
    /// the human-readable symbol that goes in the <c>symbol</c> column.
    /// May return <see langword="null"/> or empty for unknown securities;
    /// the exporter falls back to the numeric SecurityId and emits a
    /// single warn log per unique unresolved id per export run.</param>
    /// <param name="generatedAtUtc">Timestamp embedded in the <c>.done</c>
    /// sidecar's <c>generatedAt</c> field. Caller provides it so tests
    /// can pin a deterministic value; production callers pass
    /// <see cref="DateTime.UtcNow"/>.</param>
    /// <exception cref="FileNotFoundException">No audit log file exists
    /// for the requested (channel, date).</exception>
    /// <exception cref="InvalidDataException">The audit log's file header
    /// is corrupt (unrecoverable; not a torn tail).</exception>
    public EodFillsExportResult Export(
        string auditRootDir,
        string dropRootDir,
        byte channelNumber,
        DateOnly businessDate,
        Func<long, string?> symbolLookup,
        DateTime generatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(auditRootDir);
        ArgumentException.ThrowIfNullOrEmpty(dropRootDir);
        ArgumentNullException.ThrowIfNull(symbolLookup);
        if (generatedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("generatedAtUtc must be DateTimeKind.Utc", nameof(generatedAtUtc));

        var channel = channelNumber.ToString(CultureInfo.InvariantCulture);
        var dateStr = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var auditLogPath = Path.Combine(auditRootDir, channel, $"fills-{dateStr}.log");
        if (!File.Exists(auditLogPath))
            throw new FileNotFoundException($"audit log not found for channel={channel} date={dateStr}", auditLogPath);

        var dropDir = Path.Combine(dropRootDir, channel, dateStr);
        Directory.CreateDirectory(dropDir);

        var unresolved = new HashSet<long>();
        long rowCount;
        string sha256Hex;

        // 1) Write CSV to staging (also computes SHA-256 streaming).
        var csvStaging = StagingPath(dropDir, "fills.csv");
        string? doneStaging = null;
        try
        {
            using (var sha = SHA256.Create())
            {
                using (var fs = OpenStagingForWrite(csvStaging))
                using (var cs = new CryptoStream(fs, sha, CryptoStreamMode.Write, leaveOpen: true))
                using (var sw = new StreamWriter(cs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true))
                {
                    rowCount = WriteCsv(sw, auditLogPath, symbolLookup, unresolved);
                    sw.Flush();
                    cs.FlushFinalBlock();
                    fs.Flush(flushToDisk: true);
                }
                sha256Hex = ToHex(sha.Hash!);
            }

            // 2) Write .done staging file (paired with the CSV staging).
            // We do this BEFORE invalidating any prior .done so a failure
            // here still leaves the previous (csv, .done) pair intact.
            var donePayload = BuildDonePayload(rowCount, sha256Hex, generatedAtUtc);
            doneStaging = StagingPath(dropDir, "fills.csv.done");
            using (var fs = OpenStagingForWrite(doneStaging))
            {
                fs.Write(donePayload);
                fs.Flush(flushToDisk: true);
            }

            var csvFinal = Path.Combine(dropDir, "fills.csv");
            var doneFinal = Path.Combine(dropDir, "fills.csv.done");

            // 3) Invalidate any prior .done BEFORE publishing the new
            // CSV so consumers can never observe a stale ready signal
            // pointing at a freshly-replaced fills.csv. From here a
            // crash before step 5 leaves no .done — consumers correctly
            // poll "not ready" until the next exporter run.
            if (File.Exists(doneFinal))
            {
                File.Delete(doneFinal);
                FsyncDirectory(dropDir);
            }

            // 4) Publish the new CSV.
            File.Move(csvStaging, csvFinal, overwrite: true);
            FsyncDirectory(dropDir);
            csvStaging = null!; // staging file is gone; suppress finally-cleanup.

            // 5) Publish the new .done sidecar last — this is the
            // consumer-visible ready signal.
            File.Move(doneStaging, doneFinal, overwrite: true);
            FsyncDirectory(dropDir);
            doneStaging = null;

            if (unresolved.Count > 0)
            {
                _logger.LogWarning(
                    "eod fills export: {Count} unknown securityId(s) emitted as numeric fallback for channel={Channel} date={Date}",
                    unresolved.Count, channel, dateStr);
            }

            _logger.LogInformation(
                "eod fills export: channel={Channel} date={Date} rows={Rows} sha256={Sha} path={Path}",
                channel, dateStr, rowCount, sha256Hex, csvFinal);

            return new EodFillsExportResult(csvFinal, rowCount, sha256Hex);
        }
        finally
        {
            BestEffortDelete(csvStaging);
            BestEffortDelete(doneStaging);
        }
    }

    private long WriteCsv(StreamWriter sw, string auditLogPath, Func<long, string?> symbolLookup, HashSet<long> unresolved)
    {
        // Header — frozen by ADR 0001 / issue #330.
        sw.Write("tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm\n");

        long rows = 0;
        foreach (var r in AuditLogReader.ReadAll(auditLogPath))
        {
            var sym = symbolLookup(r.SecurityId);
            if (string.IsNullOrEmpty(sym))
            {
                unresolved.Add(r.SecurityId);
                sym = r.SecurityId.ToString(CultureInfo.InvariantCulture);
            }

            sw.Write(r.TradeId.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(FormatTimestampMicros(r.TransactTimeNanos));
            sw.Write(',');
            sw.Write(EscapeCsv(sym));
            sw.Write(',');
            sw.Write(r.AggressorSide == Side.Buy ? 'B' : 'S');
            sw.Write(',');
            sw.Write(r.Quantity.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(r.PriceMantissa.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(r.BuyClOrdId.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(r.SellClOrdId.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(r.BuyFirm.ToString(CultureInfo.InvariantCulture));
            sw.Write(',');
            sw.Write(r.SellFirm.ToString(CultureInfo.InvariantCulture));
            sw.Write('\n');
            rows++;
        }
        return rows;
    }

    private static string FormatTimestampMicros(ulong transactTimeNanos)
    {
        // PostTradeRecord.TransactTimeNanos is documented as wall-clock UTC
        // nanoseconds since Unix epoch (matches FileAuditLogWriter's input).
        // ADR 0001 / #330 require ISO-8601 UTC with microsecond precision.
        long micros = (long)(transactTimeNanos / 1000UL);
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000)
            .AddTicks((micros % 1000) * 10);
        return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string field)
    {
        // The frozen columns are numeric or short identifiers, but symbols
        // could in theory contain a quote/comma/newline. RFC 4180: wrap in
        // quotes and double any embedded quotes.
        if (field.IndexOfAny(s_csvSpecials) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static readonly char[] s_csvSpecials = new[] { ',', '"', '\n', '\r' };

    private static byte[] BuildDonePayload(long rowCount, string sha256Hex, DateTime generatedAtUtc)
    {
        // Hand-formatted JSON keeps the dependency surface zero and the
        // payload deterministic (no ordering / culture quirks).
        var generatedAt = generatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var json = "{\"rowCount\":" + rowCount.ToString(CultureInfo.InvariantCulture)
            + ",\"sha256\":\"" + sha256Hex + "\""
            + ",\"generatedAt\":\"" + generatedAt + "\"}\n";
        return Encoding.UTF8.GetBytes(json);
    }

    private static string ToHex(byte[] bytes)
    {
        // Lowercase hex; matches conventional sha256 sidecar formats.
        var c = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            c[i * 2] = HexChar(b >> 4);
            c[i * 2 + 1] = HexChar(b & 0xF);
        }
        return new string(c);
    }

    private static char HexChar(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));

    private static string StagingPath(string dir, string finalName)
    {
        // Leading dot keeps the staging file hidden on POSIX so directory
        // listeners don't react to it. {pid}-{guid} prevents collision
        // between concurrent exporter instances on the same machine.
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        return Path.Combine(dir, $".{finalName}.tmp-{pid}-{nonce}");
    }

    private static FileStream OpenStagingForWrite(string path)
        => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.None);

    private static void FsyncDirectory(string dir)
    {
        // RandomAccess.FlushToDisk on a directory fd is the POSIX-correct
        // way to make a rename(2) durable. On Windows it's a no-op (the
        // rename itself is journaled). Some container/tmpfs configs reject
        // a read-mode open on a directory with EACCES; treat dir fsync as
        // best-effort (matches FileChannelStatePersister.FsyncDirectory) —
        // the file rename itself is durable on every mainstream Linux FS.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            using var handle = File.OpenHandle(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
            RandomAccess.FlushToDisk(handle);
        }
        catch
        {
            // Best-effort; some FSes reject directory fsync (EINVAL) or
            // directory read-mode open (EACCES under restrictive mounts).
        }
    }

    private static void BestEffortDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* swallow */ }
    }
}
