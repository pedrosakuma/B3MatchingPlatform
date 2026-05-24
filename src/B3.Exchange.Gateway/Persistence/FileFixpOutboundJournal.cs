using System.Buffers.Binary;
using System.Globalization;
using B3.Exchange.Contracts;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// File-backed segmented implementation of
/// <see cref="IFixpOutboundJournal"/>. One directory per session
/// under <c>{dataDir}/journal/session-{sessionId:x8}/</c> containing
/// segment files named <c>segment-{firstSeq:x8}.log</c>. Each segment
/// caps at <see cref="DefaultSegmentMaxBytes"/> bytes; when an
/// <see cref="Append"/> would exceed that, the active segment is
/// closed and a new one is opened starting at the next seq.
///
/// <para><b>Record layout</b>: each appended frame is written as
/// <c>[length:u4 LE | seq:u4 LE | ts:i8 LE | frame_bytes:length | crc32c:u4 LE]</c>.
/// The Crc32C covers <c>length || seq || ts || frame_bytes</c>; a
/// torn tail (partial record at EOF) is detected on read by either
/// an incomplete record or a Crc32C mismatch and silently dropped
/// per-record. A CRC mismatch in the middle of a segment is logged
/// at Critical and the segment is truncated at the corruption — we
/// never serve bytes that didn't survive the integrity check.</para>
///
/// <para><b>fsync policy</b>: every <see cref="Append"/> flushes via
/// <c>FileStream.Flush(flushToDisk: true)</c>. Matches the per-record
/// durability the FIXP peer expects; a windowed fsync optimisation
/// would be a straightforward optimisation behind the same
/// interface.</para>
///
/// <para><b>Pruning</b>: <see cref="PruneUpTo"/> deletes whole
/// segments whose last seq is <c>&lt;= uptoSeq</c>. Segments that
/// straddle the watermark are kept intact (conservative — never
/// drop a record we cannot prove is acked).</para>
///
/// <para><b>Quota/retention</b>: when <see cref="MaxBytesPerSession"/>
/// or <see cref="MaxRetention"/> is exceeded during <see cref="Append"/>,
/// whole old segments are deleted only if their last seq is at or below
/// the last peer-confirmed ACK watermark. If no segment is safe, the
/// append succeeds, a warning is logged, and metrics show the over-budget
/// journal.</para>
/// </summary>
public sealed class FileFixpOutboundJournal : IFixpOutboundJournal
{
    /// <summary>Subdirectory under the configured data directory.</summary>
    public const string JournalSubdir = "journal";

    /// <summary>Default per-segment cap (16 MiB).</summary>
    public const int DefaultSegmentMaxBytes = 16 * 1024 * 1024;

    /// <summary>Fixed per-record overhead: length(4) + seq(4) + ts(8) + crc(4).</summary>
    public const int RecordOverheadBytes = 4 + 4 + 8 + 4;

    private readonly string _journalDir;
    private readonly ILogger<FileFixpOutboundJournal> _logger;
    private readonly int _segmentMaxBytes;
    private readonly TimeSpan _maxRetention;
    private readonly FixpJournalMetrics? _metrics;
    private readonly object _lock = new();
    private readonly Dictionary<uint, ActiveSegment> _active = new();
    private readonly Dictionary<uint, uint> _confirmedPeerAck = new();
    private bool _disposed;

    /// <summary>
    /// Per-session on-disk byte budget. When non-zero an
    /// <see cref="Append"/> that would push the session above this
    /// value triggers ACK-watermark-gated rotation.
    /// Zero (default) disables the cap.
    /// </summary>
    public long MaxBytesPerSession { get; }

    /// <summary>Maximum retained age for the oldest journal entry. Zero disables age rotation.</summary>
    public TimeSpan MaxRetention => _maxRetention;

    public FileFixpOutboundJournal(string dataDir,
        ILogger<FileFixpOutboundJournal> logger,
        int segmentMaxBytes = DefaultSegmentMaxBytes,
        long maxBytesPerSession = 0,
        TimeSpan? maxRetention = null,
        FixpJournalMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(dataDir);
        ArgumentNullException.ThrowIfNull(logger);
        if (segmentMaxBytes <= RecordOverheadBytes)
            throw new ArgumentOutOfRangeException(nameof(segmentMaxBytes),
                $"must be greater than the per-record overhead ({RecordOverheadBytes} bytes)");
        if (maxBytesPerSession < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytesPerSession),
                "must be >= 0 (0 disables the cap)");
        if (maxRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxRetention),
                "must be >= TimeSpan.Zero (zero disables retention)");
        _journalDir = Path.Combine(dataDir, JournalSubdir);
        _logger = logger;
        _segmentMaxBytes = segmentMaxBytes;
        MaxBytesPerSession = maxBytesPerSession;
        _maxRetention = maxRetention ?? TimeSpan.Zero;
        _metrics = metrics;
        Directory.CreateDirectory(_journalDir);
    }

    private string SessionDir(uint sessionId)
        => Path.Combine(_journalDir, $"session-{sessionId:x8}");

    private static string SegmentFileName(uint firstSeq)
        => $"segment-{firstSeq:x8}.log";

    public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
            throw new ArgumentException("frame must not be empty", nameof(frame));
        if (seq == 0u)
            throw new ArgumentOutOfRangeException(nameof(seq), "seq must be > 0");

        int recordBytes = RecordOverheadBytes + frame.Length;
        ActiveSegment segment;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_active.TryGetValue(sessionId, out segment!))
            {
                segment = OpenOrCreateActiveSegment(sessionId, seq);
                _active[sessionId] = segment;
            }

            if (segment.LastSeq != 0 && seq <= segment.LastSeq)
                throw new InvalidOperationException(
                    $"journal append for session 0x{sessionId:x8} seq={seq} is not strictly greater than last persisted seq {segment.LastSeq}");

            if (segment.Stream.Length + recordBytes > _segmentMaxBytes && segment.LastSeq != 0)
            {
                segment.Dispose();
                segment = CreateNewActiveSegment(sessionId, seq);
                _active[sessionId] = segment;
            }

            EnforceQuotasBeforeAppendLocked(sessionId, segment, recordBytes, timestampNanos);
        }

        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), seq);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(8, 8), timestampNanos);
        uint crc = ComputeRecordCrc(header, frame);
        Span<byte> footer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, crc);

        lock (segment.WriteLock)
        {
            segment.Stream.Write(header);
            segment.Stream.Write(frame);
            segment.Stream.Write(footer);
            segment.Stream.Flush(flushToDisk: true);
            segment.LastSeq = seq;
            segment.EntryCount++;
        }
        lock (_lock)
        {
            if (_active.TryGetValue(sessionId, out var active))
                ObserveSessionLocked(sessionId, active, timestampNanos);
        }
    }

    private long CurrentSessionBytesLocked(uint sessionId, ActiveSegment active)
    {
        long total = active.Stream.Length;
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return total;
        foreach (var path in Directory.EnumerateFiles(dir, "segment-*.log"))
        {
            if (string.Equals(path, active.Path, StringComparison.Ordinal)) continue;
            try { total += new FileInfo(path).Length; }
            catch (FileNotFoundException) { /* concurrent prune race; ignore */ }
        }
        return total;
    }

    private void EnforceQuotasBeforeAppendLocked(uint sessionId, ActiveSegment active,
        int recordBytes, long nowNanos)
    {
        bool overBytes = MaxBytesPerSession > 0
            && CurrentSessionBytesLocked(sessionId, active) + recordBytes > MaxBytesPerSession;
        bool overAge = IsRetentionExceededLocked(sessionId, nowNanos);

        if (!overBytes && !overAge)
        {
            ObserveSessionLocked(sessionId, active, nowNanos);
            return;
        }

        if (overBytes)
            RotateSafeSegmentsLocked(sessionId, active, nowNanos, "bytes");
        if (overAge)
            RotateSafeSegmentsLocked(sessionId, active, nowNanos, "age");

        ObserveSessionLocked(sessionId, active, nowNanos);
    }

    private bool IsRetentionExceededLocked(uint sessionId, long nowNanos)
    {
        if (_maxRetention <= TimeSpan.Zero) return false;
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return false;
        long oldest = 0;
        foreach (var (_, path) in EnumerateSegmentsOrdered(dir))
        {
            var info = ScanSegment(path);
            if (info.EntryCount == 0) continue;
            oldest = info.FirstTimestampNanos;
            break;
        }
        if (oldest == 0) return false;
        return AgeSeconds(nowNanos, oldest) > _maxRetention.TotalSeconds;
    }

    private void RotateSafeSegmentsLocked(uint sessionId, ActiveSegment active,
        long nowNanos, string reason)
    {
        uint watermark = _confirmedPeerAck.TryGetValue(sessionId, out var ack) ? ack : 0u;
        if (watermark == 0)
        {
            LogRotationBlocked(sessionId, reason, watermark, active, nowNanos);
            return;
        }

        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return;
        var segments = EnumerateSegmentsOrdered(dir);
        int deleted = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var (firstSeq, path) = segments[i];
            uint nextFirst = i + 1 < segments.Count ? segments[i + 1].firstSeq : uint.MaxValue;
            uint segmentLastSeq = nextFirst == uint.MaxValue
                ? ScanSegment(path).LastSeq
                : nextFirst - 1;
            if (segmentLastSeq == 0) continue;
            if (segmentLastSeq > watermark) break;
            if (string.Equals(path, active.Path, StringComparison.Ordinal)) continue;
            try
            {
                File.Delete(path);
                deleted++;
                _metrics?.IncRotation(sessionId, reason);
                _logger.LogInformation(
                    "fixp outbound journal: rotated segment {Path} (session=0x{SessionId:x8}, reason={Reason}, firstSeq={FirstSeq}, lastSeq={LastSeq}, peerAck={PeerAck})",
                    path, sessionId, reason, firstSeq, segmentLastSeq, watermark);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp outbound journal: failed to rotate segment {Path} (session=0x{SessionId:x8}, reason={Reason})",
                    path, sessionId, reason);
            }
        }

        bool stillOver = reason == "bytes"
            ? MaxBytesPerSession > 0 && CurrentSessionBytesLocked(sessionId, active) > MaxBytesPerSession
            : IsRetentionExceededLocked(sessionId, nowNanos);
        if (deleted == 0 || stillOver)
            LogRotationBlocked(sessionId, reason, watermark, active, nowNanos);
    }

    private void LogRotationBlocked(uint sessionId, string reason, uint watermark,
        ActiveSegment active, long nowNanos)
    {
        long bytes = CurrentSessionBytesLocked(sessionId, active);
        long oldestAge = OldestAgeSecondsLocked(sessionId, nowNanos);
        _metrics?.Observe(sessionId, bytes, oldestAge);
        _logger.LogWarning(
            "fixp outbound journal: {Reason} quota reached for session 0x{SessionId:x8}, but no segment is safe to delete (peerAck={PeerAck}, bytes={Bytes}, oldestAgeSeconds={OldestAgeSeconds}); allowing journal to grow",
            reason, sessionId, watermark, bytes, oldestAge);
    }

    private void ObserveSessionLocked(uint sessionId, ActiveSegment active, long nowNanos)
        => _metrics?.Observe(sessionId, CurrentSessionBytesLocked(sessionId, active),
            OldestAgeSecondsLocked(sessionId, nowNanos));

    private long OldestAgeSecondsLocked(uint sessionId, long nowNanos)
    {
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return 0;
        foreach (var (_, path) in EnumerateSegmentsOrdered(dir))
        {
            var info = ScanSegment(path);
            if (info.EntryCount == 0) continue;
            return AgeSeconds(nowNanos, info.FirstTimestampNanos);
        }
        return 0;
    }

    private static long AgeSeconds(long nowNanos, long timestampNanos)
    {
        if (nowNanos <= timestampNanos) return 0;
        return (nowNanos - timestampNanos) / 1_000_000_000L;
    }

    private static uint ComputeRecordCrc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> frame)
    {
        int total = header.Length + frame.Length;
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var rented = pool.Rent(total);
        try
        {
            header.CopyTo(rented);
            frame.CopyTo(rented.AsSpan(header.Length));
            return Crc32C.Compute(rented.AsSpan(0, total));
        }
        finally { pool.Return(rented); }
    }

    private ActiveSegment OpenOrCreateActiveSegment(uint sessionId, uint nextSeq)
    {
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        var segments = EnumerateSegmentsOrdered(dir);
        if (segments.Count == 0)
        {
            return CreateNewActiveSegment(sessionId, nextSeq);
        }
        var (firstSeq, path) = segments[^1];
        var info = ScanSegment(path);
        if (info.IsCorrupt)
        {
            TruncateAtCorruption(path, info.ValidBytes);
        }
        if (info.ValidBytes == 0 && info.LastSeq == 0)
        {
            try { File.Delete(path); } catch { }
            return CreateNewActiveSegment(sessionId, nextSeq);
        }
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.Read, bufferSize: 4096, useAsync: false);
        return new ActiveSegment(path, firstSeq, fs)
        {
            LastSeq = info.LastSeq,
            EntryCount = info.EntryCount,
        };
    }

    private ActiveSegment CreateNewActiveSegment(uint sessionId, uint firstSeq)
    {
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SegmentFileName(firstSeq));
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, bufferSize: 4096, useAsync: false);
        // Issue #405 (review finding): fsync the session directory so
        // the new segment's directory entry is durable before the first
        // frame written to it goes on-wire. Without this, a power-loss
        // immediately after CreateNew + Append + send could lose the
        // segment file entirely on next boot, even though its contents
        // had been flushed — the peer would see acks for seqs that
        // disappear from the journal.
        DirectorySync.Fsync(dir);
        return new ActiveSegment(path, firstSeq, fs);
    }

    public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
    {
        if (count <= 0) return Array.Empty<OutboundJournalEntry>();
        FlushActiveLocked(sessionId);
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return Array.Empty<OutboundJournalEntry>();
        var segments = EnumerateSegmentsOrdered(dir);
        if (segments.Count == 0) return Array.Empty<OutboundJournalEntry>();

        var result = new List<OutboundJournalEntry>(Math.Min(count, 256));
        uint expectedNext = fromSeq;
        bool started = false;
        foreach (var (firstSeq, path) in segments)
        {
            if (!started)
            {
                int idx = IndexOfStartSegment(segments, fromSeq);
                if (idx < 0) return result;
                if (firstSeq != segments[idx].firstSeq) continue;
                started = true;
            }
            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { continue; }

            int offset = 0;
            int recIdx = 0;
            while (offset < raw.Length && result.Count < count)
            {
                recIdx++;
                if (!TryDecodeRecord(raw, offset, path, recIdx, out var entry, out int recordLength))
                {
                    return result;
                }
                offset += recordLength;
                if (entry.Seq < expectedNext) continue;
                if (entry.Seq != expectedNext)
                {
                    return result;
                }
                result.Add(entry);
                expectedNext++;
            }
            if (result.Count >= count) break;
        }
        return result;
    }

    private static int IndexOfStartSegment(IReadOnlyList<(uint firstSeq, string path)> segments, uint fromSeq)
    {
        int candidate = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].firstSeq <= fromSeq) candidate = i;
            else break;
        }
        if (candidate < 0 && segments.Count > 0 && segments[0].firstSeq > fromSeq)
        {
            return 0;
        }
        return candidate;
    }

    public void ConfirmPeerAck(uint sessionId, uint uptoSeq)
    {
        if (uptoSeq == 0) return;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_confirmedPeerAck.TryGetValue(sessionId, out var existing) || uptoSeq > existing)
                _confirmedPeerAck[sessionId] = uptoSeq;
        }
    }

    private void FlushActiveLocked(uint sessionId)
    {
        ActiveSegment? active;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active.TryGetValue(sessionId, out active);
        }
        if (active is null) return;
        lock (active.WriteLock)
        {
            try { active.Stream.Flush(flushToDisk: true); }
            catch (ObjectDisposedException) { }
        }
    }

    public void PruneUpTo(uint sessionId, uint uptoSeq)
    {
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return;
        var segments = EnumerateSegmentsOrdered(dir);
        if (segments.Count == 0) return;

        FlushActiveLocked(sessionId);
        string? activePath = null;
        lock (_lock)
        {
            if (_active.TryGetValue(sessionId, out var active))
                activePath = active.Path;
        }
        for (int i = 0; i < segments.Count; i++)
        {
            var (firstSeq, path) = segments[i];
            uint nextFirst = i + 1 < segments.Count ? segments[i + 1].firstSeq : uint.MaxValue;
            uint segmentLastSeq = nextFirst == uint.MaxValue
                ? ScanSegment(path).LastSeq
                : nextFirst - 1;
            if (segmentLastSeq == 0) continue;
            if (segmentLastSeq > uptoSeq) break;
            if (string.Equals(path, activePath, StringComparison.Ordinal)) continue;
            try { File.Delete(path); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp outbound journal: failed to delete pruned segment {Path} (session=0x{SessionId:x8})",
                    path, sessionId);
            }
        }
    }

    public uint MaxSeq(uint sessionId)
    {
        FlushActiveLocked(sessionId);
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return 0;
        var segments = EnumerateSegmentsOrdered(dir);
        if (segments.Count == 0) return 0;
        var info = ScanSegment(segments[^1].path);
        return info.LastSeq;
    }

    public long EntryCount(uint sessionId)
    {
        FlushActiveLocked(sessionId);
        var dir = SessionDir(sessionId);
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var (_, path) in EnumerateSegmentsOrdered(dir))
        {
            total += ScanSegment(path).EntryCount;
        }
        return total;
    }

    public void Remove(uint sessionId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active.Remove(sessionId, out var active))
            {
                try { active.Dispose(); } catch { }
            }
        }
        var dir = SessionDir(sessionId);
        bool removed = !Directory.Exists(dir);
        if (!removed)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                removed = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp outbound journal: failed to delete session directory {Dir}",
                    dir);
            }
        }

        if (!removed) return;

        lock (_lock)
        {
            _confirmedPeerAck.Remove(sessionId);
        }
        _metrics?.Reset(sessionId);
    }

    public IReadOnlyCollection<uint> ListSessions()
    {
        if (!Directory.Exists(_journalDir)) return Array.Empty<uint>();
        var result = new List<uint>();
        foreach (var sub in Directory.EnumerateDirectories(_journalDir, "session-*"))
        {
            var name = Path.GetFileName(sub);
            if (TryParseSessionDirName(name, out var sessionId))
                result.Add(sessionId);
            else
                _logger.LogWarning(
                    "fixp outbound journal: skipping unparseable session directory {Dir}", sub);
        }
        return result;
    }

    private static bool TryParseSessionDirName(string dirName, out uint sessionId)
    {
        sessionId = 0;
        const string prefix = "session-";
        if (!dirName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return uint.TryParse(dirName.AsSpan(prefix.Length),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out sessionId);
    }

    private static bool TryParseSegmentFileName(string fileName, out uint firstSeq)
    {
        firstSeq = 0;
        const string prefix = "segment-";
        const string suffix = ".log";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var hex = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out firstSeq);
    }

    private List<(uint firstSeq, string path)> EnumerateSegmentsOrdered(string sessionDir)
    {
        var list = new List<(uint, string)>();
        foreach (var path in Directory.EnumerateFiles(sessionDir, "segment-*.log"))
        {
            var name = Path.GetFileName(path);
            if (TryParseSegmentFileName(name, out var firstSeq))
                list.Add((firstSeq, path));
            else
                _logger.LogWarning(
                    "fixp outbound journal: skipping unparseable segment file {Path}", path);
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    private readonly struct SegmentInfo
    {
        public SegmentInfo(uint lastSeq, long entryCount, long validBytes, bool corrupt,
            long firstTimestampNanos)
        {
            LastSeq = lastSeq;
            EntryCount = entryCount;
            ValidBytes = validBytes;
            IsCorrupt = corrupt;
            FirstTimestampNanos = firstTimestampNanos;
        }
        public uint LastSeq { get; }
        public long EntryCount { get; }
        public long ValidBytes { get; }
        public bool IsCorrupt { get; }
        public long FirstTimestampNanos { get; }
    }

    private SegmentInfo ScanSegment(string path)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "fixp outbound journal: failed to read segment {Path}; treating as empty",
                path);
            return new SegmentInfo(0, 0, 0, corrupt: true, firstTimestampNanos: 0);
        }
        int offset = 0;
        int recIdx = 0;
        long entryCount = 0;
        uint lastSeq = 0;
        long firstTimestampNanos = 0;
        while (offset < raw.Length)
        {
            recIdx++;
            if (!TryDecodeRecord(raw, offset, path, recIdx, out var entry, out int recordLength))
            {
                return new SegmentInfo(lastSeq, entryCount, offset, corrupt: true, firstTimestampNanos);
            }
            offset += recordLength;
            entryCount++;
            if (firstTimestampNanos == 0) firstTimestampNanos = entry.TimestampNanos;
            lastSeq = entry.Seq;
        }
        return new SegmentInfo(lastSeq, entryCount, offset, corrupt: false, firstTimestampNanos);
    }

    private bool TryDecodeRecord(byte[] raw, int offset, string path, int recIdx,
        out OutboundJournalEntry entry, out int recordLength)
    {
        entry = default;
        recordLength = 0;
        if (raw.Length - offset < RecordOverheadBytes)
        {
            _logger.LogWarning(
                "fixp outbound journal: torn-tail (got {Have} bytes, need >= {Need}) at record {Index} of {Path}; dropping rest",
                raw.Length - offset, RecordOverheadBytes, recIdx, path);
            return false;
        }
        var headerSpan = raw.AsSpan(offset, 16);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(headerSpan[..4]);
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(headerSpan.Slice(4, 4));
        long ts = BinaryPrimitives.ReadInt64LittleEndian(headerSpan.Slice(8, 8));
        int needed = RecordOverheadBytes + (int)length;
        if (length == 0 || length > int.MaxValue - RecordOverheadBytes || raw.Length - offset < needed)
        {
            _logger.LogWarning(
                "fixp outbound journal: torn-tail at record {Index} of {Path}; expected {Needed} bytes have {Have}; dropping rest",
                recIdx, path, needed, raw.Length - offset);
            return false;
        }
        var frame = raw.AsSpan(offset + 16, (int)length);
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            raw.AsSpan(offset + 16 + (int)length, 4));
        uint computed = ComputeRecordCrc(headerSpan, frame);
        if (computed != storedCrc)
        {
            _logger.LogCritical(
                "fixp outbound journal: CRC mismatch at record {Index} of {Path} (stored=0x{Stored:X8} computed=0x{Computed:X8}); dropping rest",
                recIdx, path, storedCrc, computed);
            return false;
        }
        entry = new OutboundJournalEntry(seq, ts, frame.ToArray());
        recordLength = needed;
        return true;
    }

    private void TruncateAtCorruption(string path, long validBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: false);
            fs.SetLength(validBytes);
            fs.Flush(flushToDisk: true);
            _logger.LogWarning(
                "fixp outbound journal: truncated corrupt segment {Path} to {ValidBytes} bytes",
                path, validBytes);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "fixp outbound journal: failed to truncate corrupt segment {Path}; subsequent reads will stop at the corruption",
                path);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var seg in _active.Values)
            {
                try { seg.Dispose(); } catch { }
            }
            _active.Clear();
        }
    }

    private sealed class ActiveSegment : IDisposable
    {
        public string Path { get; }
        public uint FirstSeq { get; }
        public FileStream Stream { get; }
        public object WriteLock { get; } = new();
        public uint LastSeq { get; set; }
        public long EntryCount { get; set; }

        public ActiveSegment(string path, uint firstSeq, FileStream stream)
        {
            Path = path;
            FirstSeq = firstSeq;
            Stream = stream;
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
        }
    }
}
