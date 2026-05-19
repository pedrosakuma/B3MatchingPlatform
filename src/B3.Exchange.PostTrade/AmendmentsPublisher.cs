using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.PostTrade;

/// <summary>Pluggable seam for the post-EOD amendments-file
/// publisher; <see cref="ChannelDispatcher"/> invokes
/// <see cref="Publish"/> immediately after writing the bust audit
/// record on the post-EOD path (ADR 0008 §4). Tests can substitute
/// fakes to assert that the dispatcher routes correctly without
/// touching the file system.</summary>
public interface IAmendmentsPublisher
{
    /// <summary>Regenerates
    /// <c>{dropRootDir}/{channelNumber}/{businessDate}/amendments.csv</c>
    /// from the audit log (every bust record whose
    /// <see cref="BustRecord.DeclaredTradeDateDays"/> matches
    /// <paramref name="businessDate"/>) and atomically swaps the
    /// sibling <c>amendments.csv.done</c> sentinel per the protocol
    /// in ADR 0008 §4 steps 1-6.</summary>
    void Publish(string auditRootDir, string dropRootDir, byte channelNumber, DateOnly businessDate, DateTime generatedAtUtc);
}

/// <summary>
/// Default <see cref="IAmendmentsPublisher"/> — projects every post-EOD
/// <see cref="AuditRecordKind.Bust"/> record whose
/// <see cref="BustRecord.DeclaredTradeDateDays"/> matches the requested
/// business date into an <c>amendments.csv</c> drop next to the day's
/// <c>fills.csv</c>. See ADR 0008 §4 for the column schema and the
/// publish protocol — the implementation mirrors
/// <see cref="EodFillsExporter"/>'s stage→fsync→delete-old-done→
/// rename-csv→rename-done sequence so consumers never observe a body
/// whose digest does not match the <c>.done</c> sidecar.
/// </summary>
public sealed class AmendmentsPublisher : IAmendmentsPublisher
{
    private readonly ILogger<AmendmentsPublisher> _logger;

    public AmendmentsPublisher(ILogger<AmendmentsPublisher>? logger = null)
    {
        _logger = logger ?? NullLogger<AmendmentsPublisher>.Instance;
    }

    public void Publish(string auditRootDir, string dropRootDir, byte channelNumber, DateOnly businessDate, DateTime generatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(auditRootDir);
        ArgumentException.ThrowIfNullOrEmpty(dropRootDir);
        if (generatedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("generatedAtUtc must be DateTimeKind.Utc", nameof(generatedAtUtc));

        var channel = channelNumber.ToString(CultureInfo.InvariantCulture);
        var dateStr = businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dropDir = Path.Combine(dropRootDir, channel, dateStr);
        Directory.CreateDirectory(dropDir);

        // 1) Collect every bust whose declaredTradeDate matches the
        // target. ADR 0008 §4 step 1: regenerate from the audit log,
        // never append, so a torn write cannot leave a partial row
        // visible. Sort by bustTransactTime so consumers can apply in
        // a deterministic order (rule §4 reconciliation).
        var busts = new List<BustRecord>();
        var channelDir = Path.Combine(auditRootDir, channel);
        if (Directory.Exists(channelDir))
        {
            int targetDays = businessDate.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
            foreach (var path in Directory.EnumerateFiles(channelDir, "fills-*.log"))
            {
                foreach (var entry in AuditLogReader.ReadAllEntries(path))
                {
                    if (entry.Kind != AuditRecordKind.Bust) continue;
                    var bust = entry.Bust;
                    if (bust.DeclaredTradeDateDays != targetDays) continue;
                    busts.Add(bust);
                }
            }
        }
        busts.Sort((a, b) => a.BustTransactTimeNanos.CompareTo(b.BustTransactTimeNanos));

        // 2) Build SHA-256 lookup for each cancelled tradeId by scanning
        // the published fills.csv once. The hash byte-range is defined
        // as "first byte of the row through and including the row-
        // terminating LF" (ADR 0008 §4 sha256OfOriginalFillRow).
        var fillsCsvPath = Path.Combine(dropDir, "fills.csv");
        Dictionary<uint, string> tradeIdToHash = busts.Count == 0
            ? new Dictionary<uint, string>()
            : ComputeRowHashes(fillsCsvPath, new HashSet<uint>(busts.Select(b => b.CancelledTradeId)));

        string csvStaging = StagingPath(dropDir, "amendments.csv");
        string? doneStaging = null;
        long rowCount;
        string sha256Hex;
        try
        {
            using (var sha = SHA256.Create())
            {
                using (var fs = OpenStagingForWrite(csvStaging))
                using (var cs = new CryptoStream(fs, sha, CryptoStreamMode.Write, leaveOpen: true))
                using (var sw = new StreamWriter(cs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true))
                {
                    sw.Write("cancelTradeId,bustTransactTime,reasonCode,correlationId,sha256OfOriginalFillRow\n");
                    long rows = 0;
                    foreach (var b in busts)
                    {
                        sw.Write(b.CancelledTradeId.ToString(CultureInfo.InvariantCulture));
                        sw.Write(',');
                        sw.Write(FormatTimestampMicros(b.BustTransactTimeNanos));
                        sw.Write(',');
                        sw.Write(b.ReasonCode.ToString(CultureInfo.InvariantCulture));
                        sw.Write(',');
                        sw.Write(b.CorrelationId.ToString(CultureInfo.InvariantCulture));
                        sw.Write(',');
                        sw.Write(tradeIdToHash.TryGetValue(b.CancelledTradeId, out var h) ? h : string.Empty);
                        sw.Write('\n');
                        rows++;
                    }
                    rowCount = rows;
                    sw.Flush();
                    cs.FlushFinalBlock();
                    fs.Flush(flushToDisk: true);
                }
                sha256Hex = ToHex(sha.Hash!);
            }

            var donePayload = BuildDonePayload(rowCount, sha256Hex, generatedAtUtc);
            doneStaging = StagingPath(dropDir, "amendments.csv.done");
            using (var fs = OpenStagingForWrite(doneStaging))
            {
                fs.Write(donePayload);
                fs.Flush(flushToDisk: true);
            }

            var csvFinal = Path.Combine(dropDir, "amendments.csv");
            var doneFinal = Path.Combine(dropDir, "amendments.csv.done");

            if (File.Exists(doneFinal))
            {
                File.Delete(doneFinal);
                FsyncDirectory(dropDir);
            }

            File.Move(csvStaging, csvFinal, overwrite: true);
            FsyncDirectory(dropDir);
            csvStaging = null!;

            File.Move(doneStaging, doneFinal, overwrite: true);
            FsyncDirectory(dropDir);
            doneStaging = null;

            _logger.LogInformation(
                "amendments publish: channel={Channel} date={Date} rows={Rows} sha256={Sha} path={Path}",
                channel, dateStr, rowCount, sha256Hex, csvFinal);
        }
        finally
        {
            BestEffortDelete(csvStaging);
            BestEffortDelete(doneStaging);
        }
    }

    private static Dictionary<uint, string> ComputeRowHashes(string fillsCsvPath, HashSet<uint> wantedTradeIds)
    {
        var result = new Dictionary<uint, string>();
        if (!File.Exists(fillsCsvPath) || wantedTradeIds.Count == 0) return result;

        // Stream the file, identifying row boundaries on LF. The first
        // row is the header (skipped); for every subsequent row, parse
        // the leading tradeId up to the first comma, and if it matches
        // a wanted id, compute SHA-256 of the row bytes (including the
        // trailing LF).
        using var fs = new FileStream(fillsCsvPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var ms = new MemoryStream(64 * 1024);
        int b;
        bool inHeader = true;
        while ((b = fs.ReadByte()) >= 0)
        {
            ms.WriteByte((byte)b);
            if (b == (byte)'\n')
            {
                if (inHeader)
                {
                    inHeader = false;
                }
                else
                {
                    var rowBytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                    int commaIdx = rowBytes.IndexOf((byte)',');
                    if (commaIdx > 0
                        && uint.TryParse(Encoding.UTF8.GetString(rowBytes[..commaIdx]), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint tradeId)
                        && wantedTradeIds.Contains(tradeId)
                        && !result.ContainsKey(tradeId))
                    {
                        result[tradeId] = ToHex(SHA256.HashData(rowBytes));
                    }
                }
                ms.SetLength(0);
            }
        }
        return result;
    }

    private static string FormatTimestampMicros(ulong transactTimeNanos)
    {
        long micros = (long)(transactTimeNanos / 1000UL);
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000)
            .AddTicks((micros % 1000) * 10);
        return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
    }

    private static byte[] BuildDonePayload(long rowCount, string sha256Hex, DateTime generatedAtUtc)
    {
        var generatedAt = generatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var json = "{\"rowCount\":" + rowCount.ToString(CultureInfo.InvariantCulture)
            + ",\"sha256\":\"" + sha256Hex + "\""
            + ",\"generatedAt\":\"" + generatedAt + "\"}\n";
        return Encoding.UTF8.GetBytes(json);
    }

    private static string ToHex(byte[] bytes) => ToHex((ReadOnlySpan<byte>)bytes);

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
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
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        return Path.Combine(dir, $".{finalName}.tmp-{pid}-{nonce}");
    }

    private static FileStream OpenStagingForWrite(string path)
        => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, FileOptions.None);

    private static void FsyncDirectory(string dir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            using var handle = File.OpenHandle(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
            RandomAccess.FlushToDisk(handle);
        }
        catch
        {
            // Best-effort; some FSes reject directory fsync.
        }
    }

    private static void BestEffortDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* swallow */ }
    }
}
