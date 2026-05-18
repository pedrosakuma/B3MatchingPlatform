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
    /// The index is treated strictly as an optimization: any portion of the
    /// log not covered by an intact index block is scanned linearly so the
    /// reader can never return fewer records than <see cref="ReadAll(string)"/>
    /// would. Possible reasons for unindexed coverage:
    /// <list type="bullet">
    /// <item>missing or unreadable <c>.idx</c> file (full fallback)</item>
    /// <item>a writer's in-progress block not yet flushed to <c>.idx</c></item>
    /// <item>a torn or self-inconsistent index entry mid-file</item>
    /// </list></summary>
    public static IEnumerable<PostTradeRecord> ReadByFirm(string logPath, uint firmId)
    {
        var idxPath = Path.ChangeExtension(logPath, ".idx");
        List<(ulong Offset, ushort RecordCount)> candidates;
        long indexedEndOffset;
        if (!File.Exists(idxPath))
        {
            candidates = new List<(ulong, ushort)>();
            indexedEndOffset = AuditRecordCodec.FileHeaderSize;
        }
        else
        {
            try
            {
                (candidates, indexedEndOffset) = LoadCandidateBlocks(idxPath, firmId);
            }
            catch (InvalidDataException)
            {
                // Index unusable (bad header) — full scan from start of records.
                candidates = new List<(ulong, ushort)>();
                indexedEndOffset = AuditRecordCodec.FileHeaderSize;
            }
        }

        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[AuditRecordCodec.FileHeaderSize];
        int hr = ReadFully(fs, header, 0, header.Length);
        if (hr != header.Length)
            throw new InvalidDataException($"audit file '{logPath}' truncated in header (got {hr} bytes)");
        _ = AuditRecordCodec.ReadFileHeader(header);

        var buffer = new byte[AuditRecordCodec.RecordSize];
        // 1) Indexed candidates — seek directly to each block.
        foreach (var (offset, recordCount) in candidates)
        {
            fs.Seek((long)offset, SeekOrigin.Begin);
            for (int i = 0; i < recordCount; i++)
            {
                int read = ReadFully(fs, buffer, 0, AuditRecordCodec.RecordSize);
                if (read < AuditRecordCodec.RecordSize) break;
                if (!AuditRecordCodec.TryDecode(buffer, out var record)) break;
                if (record.BuyFirm == firmId || record.SellFirm == firmId)
                    yield return record;
            }
        }

        // 2) Unindexed suffix — anything past the last byte the index claims
        // to cover. Critical for correctness when (a) the index is stale by
        // an in-progress block, (b) a malformed entry forced us to stop
        // trusting the index mid-file, or (c) no index exists at all.
        if (indexedEndOffset < fs.Length)
        {
            fs.Seek(indexedEndOffset, SeekOrigin.Begin);
            while (true)
            {
                int read = ReadFully(fs, buffer, 0, AuditRecordCodec.RecordSize);
                if (read == 0) break;
                if (read < AuditRecordCodec.RecordSize) break;
                if (!AuditRecordCodec.TryDecode(buffer, out var record)) break;
                if (record.BuyFirm == firmId || record.SellFirm == firmId)
                    yield return record;
            }
        }
    }

    private static (List<(ulong Offset, ushort RecordCount)> Candidates, long IndexedEndOffset)
        LoadCandidateBlocks(string idxPath, uint firmId)
    {
        var candidates = new List<(ulong, ushort)>();
        long indexedEndOffset = AuditRecordCodec.FileHeaderSize;
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
            if (prefRead < prefix.Length) break;

            ushort entryLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(0, 2));
            ulong off = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(2, 8));
            ushort recCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(10, 2));
            ushort firmCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(12, 2));
            int total = entryLen + 2;
            int firmsBytes = total - AuditIndexCodec.BlockEntryFixedPrefix;
            if (firmsBytes != firmCount * 4) break;

            var firmBuf = firmsBytes == 0 ? Array.Empty<byte>() : new byte[firmsBytes];
            if (firmsBytes > 0)
            {
                int fr = ReadFully(ix, firmBuf, 0, firmsBytes);
                if (fr < firmsBytes) break;
            }
            if (firmCount > 0 && FirmListContains(firmBuf, firmCount, firmId))
                candidates.Add((off, recCount));
            // Index coverage advances only for fully-parsed entries; on any
            // break above, indexedEndOffset stays at the previous block end,
            // and the reader's suffix scan picks up from there.
            indexedEndOffset = (long)off + ((long)recCount * AuditRecordCodec.RecordSize);
        }
        return (candidates, indexedEndOffset);
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
