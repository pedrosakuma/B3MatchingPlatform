using System.Collections.Generic;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Sequential reader for post-trade audit log files written by
/// <see cref="FileAuditLogWriter"/>. Returns records until the underlying
/// file ends or the next record fails CRC/length validation — the latter
/// is the documented signal for a torn write on the writer side and is
/// treated as "log ends here" per the standard append-only convention
/// (see <see cref="AuditRecordCodec.TryDecode"/> docs).
///
/// PR-2 scope: full-day scan only. Firm-filtered reads land in PR-3
/// alongside the sparse <c>.idx</c> file.
/// </summary>
public static class AuditLogReader
{
    /// <summary>Opens <paramref name="path"/>, validates the file header,
    /// and yields every record in order. Throws
    /// <see cref="InvalidDataException"/> on an unreadable header
    /// (mismatched magic/version/date) — that is unrecoverable corruption,
    /// not a torn tail.</summary>
    public static IEnumerable<PostTradeRecord> ReadAll(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[AuditRecordCodec.FileHeaderSize];
        int hr = ReadFully(fs, header, 0, header.Length);
        if (hr != header.Length)
            throw new InvalidDataException($"audit file '{path}' truncated in header (got {hr} bytes)");
        _ = AuditRecordCodec.ReadFileHeader(header);

        var buffer = new byte[AuditRecordCodec.RecordSize];
        while (true)
        {
            int read = ReadFully(fs, buffer, 0, AuditRecordCodec.RecordSize);
            if (read == 0)
                yield break;
            if (read < AuditRecordCodec.RecordSize)
                yield break; // torn tail — treat as end-of-log
            if (!AuditRecordCodec.TryDecode(buffer, out var record))
                yield break;
            yield return record;
        }
    }

    private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, offset + total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
