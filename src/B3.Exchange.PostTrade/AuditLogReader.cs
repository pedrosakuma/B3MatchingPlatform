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
/// PR-3 adds firm-filtered reads via the sparse <c>.idx</c> sidecar.
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
                yield break;
            if (!AuditRecordCodec.TryDecode(buffer, out var record))
                yield break;
            yield return record;
        }
    }

    /// <summary>Yields every record in <paramref name="logPath"/> whose
    /// <see cref="PostTradeRecord.BuyFirm"/> or
    /// <see cref="PostTradeRecord.SellFirm"/> matches <paramref name="firmId"/>.
    /// If a sibling <c>.idx</c> file exists, only blocks that the index marks
    /// as containing <paramref name="firmId"/> are scanned — yielding a
    /// significant speed-up versus a full-day scan on busy channels.
    /// Falls back transparently to <see cref="ReadAll(string)"/>+filter when
    /// the index is missing or corrupt (a corrupt index is never fatal —
    /// the <c>.log</c> remains the source of truth).</summary>
    public static IEnumerable<PostTradeRecord> ReadByFirm(string logPath, uint firmId)
    {
        var idxPath = Path.ChangeExtension(logPath, ".idx");
        if (!File.Exists(idxPath))
        {
            foreach (var r in ReadAll(logPath))
                if (r.BuyFirm == firmId || r.SellFirm == firmId)
                    yield return r;
            yield break;
        }

        List<(ulong Offset, ushort RecordCount)>? candidates;
        try
        {
            candidates = LoadCandidateBlocks(idxPath, firmId);
        }
        catch (InvalidDataException)
        {
            // Corrupt or torn index — fall back to a full scan. The log is
            // the source of truth; the index is an optimization only.
            candidates = null;
        }

        if (candidates is null)
        {
            foreach (var r in ReadAll(logPath))
                if (r.BuyFirm == firmId || r.SellFirm == firmId)
                    yield return r;
            yield break;
        }

        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[AuditRecordCodec.FileHeaderSize];
        int hr = ReadFully(fs, header, 0, header.Length);
        if (hr != header.Length)
            throw new InvalidDataException($"audit file '{logPath}' truncated in header (got {hr} bytes)");
        _ = AuditRecordCodec.ReadFileHeader(header);

        var buffer = new byte[AuditRecordCodec.RecordSize];
        foreach (var (offset, recordCount) in candidates)
        {
            fs.Seek((long)offset, SeekOrigin.Begin);
            for (int i = 0; i < recordCount; i++)
            {
                int read = ReadFully(fs, buffer, 0, AuditRecordCodec.RecordSize);
                if (read < AuditRecordCodec.RecordSize) yield break;
                if (!AuditRecordCodec.TryDecode(buffer, out var record)) yield break;
                if (record.BuyFirm == firmId || record.SellFirm == firmId)
                    yield return record;
            }
        }
    }

    private static List<(ulong Offset, ushort RecordCount)> LoadCandidateBlocks(string idxPath, uint firmId)
    {
        var candidates = new List<(ulong, ushort)>();
        using var ix = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[AuditIndexCodec.FileHeaderSize];
        int hr = ReadFully(ix, header, 0, header.Length);
        if (hr != header.Length)
            throw new InvalidDataException($"audit index '{idxPath}' truncated in header (got {hr} bytes)");
        _ = AuditIndexCodec.ReadFileHeader(header);

        var prefix = new byte[AuditIndexCodec.BlockEntryFixedPrefix];
        while (true)
        {
            int prefRead = ReadFully(ix, prefix, 0, prefix.Length);
            if (prefRead == 0) break;
            if (prefRead < prefix.Length) break; // torn tail

            ushort entryLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(0, 2));
            ulong off = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(2, 8));
            ushort recCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(10, 2));
            ushort firmCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(12, 2));
            int total = entryLen + 2;
            int firmsBytes = total - AuditIndexCodec.BlockEntryFixedPrefix;
            if (firmsBytes != firmCount * 4) break; // self-inconsistent entry — treat as torn

            var firmBuf = firmsBytes == 0 ? Array.Empty<byte>() : new byte[firmsBytes];
            if (firmsBytes > 0)
            {
                int fr = ReadFully(ix, firmBuf, 0, firmsBytes);
                if (fr < firmsBytes) break; // torn tail in firm list
            }
            if (firmCount > 0 && FirmListContains(firmBuf, firmCount, firmId))
                candidates.Add((off, recCount));
        }
        return candidates;
    }

    private static bool FirmListContains(byte[] firmBytes, int firmCount, uint firmId)
    {
        int lo = 0, hi = firmCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(firmBytes.AsSpan(mid * 4, 4));
            if (v == firmId) return true;
            if (v < firmId) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
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
