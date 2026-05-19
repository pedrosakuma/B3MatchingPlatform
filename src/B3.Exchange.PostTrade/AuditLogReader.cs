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
    /// and yields every fill record in order. Throws
    /// <see cref="InvalidDataException"/> on an unreadable header
    /// (mismatched magic/version/date) — that is unrecoverable corruption,
    /// not a torn tail.
    ///
    /// **Schema-v2 files** (ADR 0008): bust and reject-attempt records
    /// are silently skipped so this API keeps its v1-era "fills only"
    /// contract. Callers that need the full event stream use
    /// <see cref="ReadAllEntries(string)"/>.</summary>
    public static IEnumerable<PostTradeRecord> ReadAll(string path)
    {
        foreach (var entry in ReadAllEntries(path))
        {
            if (entry.Kind == AuditRecordKind.Fill)
                yield return entry.Fill;
        }
    }

    /// <summary>Opens <paramref name="path"/>, validates the file header,
    /// and yields every record (fill / bust / reject-attempt) in the
    /// order they were written. The per-file schema view (ADR 0008 §1)
    /// applies: v1 files contain only fills (anything else is corruption
    /// and ends the read); v2 files dispatch by <c>recordLen</c>.
    ///
    /// Torn-tail semantics are unchanged from <see cref="ReadAll(string)"/>:
    /// short reads, unknown record lengths, recordType mismatches, and
    /// CRC failures all terminate iteration cleanly rather than throwing,
    /// matching the standard append-only-log convention.</summary>
    public static IEnumerable<AuditEntry> ReadAllEntries(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[AuditRecordCodec.FileHeaderSize];
        int hr = ReadFully(fs, header, 0, header.Length);
        if (hr != header.Length)
            throw new InvalidDataException($"audit file '{path}' truncated in header (got {hr} bytes)");
        var (_, _, schemaVersion) = AuditRecordCodec.ReadFileHeader(header);

        var buffer = new byte[AuditRecordCodec.MaxRecordSize];
        while (true)
        {
            // Peek the length prefix to dispatch record type.
            int prefixRead = ReadFully(fs, buffer, 0, 4);
            if (prefixRead == 0) yield break;
            if (prefixRead < 4) yield break;
            if (!AuditRecordCodec.TryPeekRecordLen(buffer.AsSpan(0, 4), out var recordLen))
                yield break;
            if (!AuditRecordCodec.TryGetRecordSize(recordLen, out var totalSize, out var kind))
                yield break;
            // v1 files only ever carry fills; anything else is corruption.
            if (schemaVersion == AuditRecordCodec.SchemaVersionV1 && kind != AuditRecordKind.Fill)
                yield break;

            int bodyRead = ReadFully(fs, buffer, 4, totalSize - 4);
            if (bodyRead < totalSize - 4) yield break;

            switch (kind)
            {
                case AuditRecordKind.Fill:
                    if (!AuditRecordCodec.TryDecode(buffer.AsSpan(0, totalSize), out var fill))
                        yield break;
                    yield return new AuditEntry(in fill);
                    break;
                case AuditRecordKind.Bust:
                    if (!AuditRecordCodec.TryDecodeBust(buffer.AsSpan(0, totalSize), out var bust))
                        yield break;
                    yield return new AuditEntry(in bust);
                    break;
                case AuditRecordKind.RejectAttempt:
                    if (!AuditRecordCodec.TryDecodeRejectAttempt(buffer.AsSpan(0, totalSize), out var rej))
                        yield break;
                    yield return new AuditEntry(in rej);
                    break;
                default:
                    yield break;
            }
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
        var (_, _, schemaVersion) = AuditRecordCodec.ReadFileHeader(header);
        // ADR 0008 / issue #369 PR-2: schema-aware dispatch in both the
        // indexed-block and unindexed-suffix paths. Bust and reject-
        // attempt records sit on the same stream as fills (per ADR 0008
        // §1) but have different sizes (44 / 40 bytes vs 85 for a fill);
        // the v1-era fixed 85-byte stride would either CRC-fail on the
        // first non-fill or read past it into the next record's body.
        // The writer's invariant (firm-sparse .idx covers fill records
        // only — non-fill records are written outside any open block)
        // means recordCount in each .idx entry still counts fills only.
        bool schemaIsHeterogeneous = schemaVersion == AuditRecordCodec.SchemaVersionV2
            || schemaVersion == AuditRecordCodec.SchemaVersionV3;

        var buffer = new byte[AuditRecordCodec.MaxRecordSize];
        // 1) Indexed candidates — seek directly to each block. Each entry
        // in the .idx represents a contiguous run of fill records (the
        // writer flushes any open fill block before writing a non-fill
        // record, see ADR 0008 PR-2), so a fixed 85-byte stride is still
        // correct here.
        foreach (var (offset, recordCount) in candidates)
        {
            fs.Seek((long)offset, SeekOrigin.Begin);
            for (int i = 0; i < recordCount; i++)
            {
                int read = ReadFully(fs, buffer, 0, AuditRecordCodec.RecordSize);
                if (read < AuditRecordCodec.RecordSize) break;
                if (!AuditRecordCodec.TryDecode(buffer.AsSpan(0, AuditRecordCodec.RecordSize), out var record)) break;
                if (record.BuyFirm == firmId || record.SellFirm == firmId)
                    yield return record;
            }
        }

        // 2) Unindexed suffix — anything past the last byte the index claims
        // to cover. On v2 files this stream is heterogeneous (fills + bust
        // + reject-attempt records); peek the recordLen and dispatch.
        if (indexedEndOffset < fs.Length)
        {
            fs.Seek(indexedEndOffset, SeekOrigin.Begin);
            while (true)
            {
                if (!schemaIsHeterogeneous)
                {
                    int read = ReadFully(fs, buffer, 0, AuditRecordCodec.RecordSize);
                    if (read == 0) break;
                    if (read < AuditRecordCodec.RecordSize) break;
                    if (!AuditRecordCodec.TryDecode(buffer.AsSpan(0, AuditRecordCodec.RecordSize), out var record)) break;
                    if (record.BuyFirm == firmId || record.SellFirm == firmId)
                        yield return record;
                    continue;
                }

                // v2/v3: peek recordLen (4 bytes), read remaining body, dispatch.
                int peeked = ReadFully(fs, buffer, 0, 4);
                if (peeked == 0) break;
                if (peeked < 4) break;
                if (!AuditRecordCodec.TryPeekRecordLen(buffer.AsSpan(0, 4), out uint recordLen)) break;
                if (!AuditRecordCodec.TryGetRecordSize(recordLen, out int onDiskSize, out var kind)) break;
                int restRead = ReadFully(fs, buffer, 4, onDiskSize - 4);
                if (restRead < onDiskSize - 4) break;
                if (kind == AuditRecordKind.Fill)
                {
                    if (!AuditRecordCodec.TryDecode(buffer.AsSpan(0, onDiskSize), out var record)) break;
                    if (record.BuyFirm == firmId || record.SellFirm == firmId)
                        yield return record;
                }
                // bust / reject-attempt records: skip (fills-only API).
                // CRC validation happens in the type-specific TryDecode in
                // ReadAllEntries; here we trust the recordLen dispatch and
                // continue. A bit-flip in a skipped record will most likely
                // surface as a bad recordLen on the next iteration.
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
