namespace B3.Exchange.PostTrade;

/// <summary>
/// Append-only file writer for post-trade audit records (issue #329 PR-2).
///
/// Layout: one file per channel per UTC business date, located at
/// <c>{rootDir}/{channelNumber}/fills-YYYY-MM-DD.log</c>. The date is
/// derived from each record's <see cref="PostTradeRecord.TransactTimeNanos"/>
/// rather than wall-clock so the rollover is deterministic across replay
/// and across time-warped scenario tests.
///
/// Threading model: OnTrade and OnCommandBoundary are called exclusively on
/// the <c>ChannelDispatcher</c>'s dispatch thread. Checkpoint MAY be called
/// from another thread (the async snapshot writer's onSaved callback runs on
/// the writer thread). A single private lock serializes Checkpoint against
/// OnTrade/OnCommandBoundary; the lock is uncontended in steady state since
/// Checkpoint fires at snapshot intervals.
///
/// Durability watermark (issue #329 PR-4): each <see cref="OnCommandBoundary"/>
/// records the engine's <c>commandSeq</c> as the highest seq covered by the
/// records OnTrade'd so far. <see cref="Checkpoint"/> fsyncs both files and
/// promotes that pending value into <see cref="DurableThroughCommandSeq"/>,
/// which the WAL truncation gate reads before dropping WAL records.
/// </summary>
public sealed class FileAuditLogWriter : IPostTradeSink, IDisposable
{
    private readonly string _rootDir;
    private readonly byte _channelNumber;
    private readonly int _indexBlockRecords;
    private readonly byte[] _scratch;
    private readonly byte[] _indexScratch;
    private readonly object _checkpointLock = new();

    private FileStream? _stream;
    private FileStream? _indexStream;
    private DateOnly _currentDate;
    private long _recordsWritten;

    // Durability watermark (issue #329 PR-4). _pendingCommandSeq tracks the
    // highest commandSeq tagged via OnCommandBoundary since the last Checkpoint;
    // _durableCommandSeq is advanced to it on every successful Checkpoint.
    // Read cross-thread by ChannelDispatcher's WAL truncation gate, hence
    // Volatile/Interlocked access.
    private long _pendingCommandSeq;
    private long _durableCommandSeq;

    // Issue #329 PR-4 (HIGH review finding): once an OnTrade write throws,
    // the audit log has a known hole — every subsequent OnCommandBoundary
    // and Checkpoint MUST refuse to advance the watermark so the WAL
    // truncation gate stays closed and operators can recover from the
    // on-disk WAL. Mirrors the WalAppendFailurePolicy.Halt model in
    // ChannelDispatcher (issue #286). Sticky for the lifetime of this
    // writer instance — only a restart/rebuild clears it.
    private bool _writeFault;

    // Current in-progress index block. _blockFirmIds is kept sorted+distinct
    // (linear insertion — typical session has well below 64 firms per block,
    // so this is cheaper than a HashSet allocation per block).
    private long _blockStartOffset;
    private int _blockRecordCount;
    private readonly List<uint> _blockFirmIds = new(capacity: 16);

    public FileAuditLogWriter(string rootDir, byte channelNumber, int indexBlockRecords = AuditIndexCodec.DefaultBlockRecords)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        if (indexBlockRecords <= 0 || indexBlockRecords > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(indexBlockRecords), "block size must be in [1, 65535]");
        _rootDir = rootDir;
        _channelNumber = channelNumber;
        _indexBlockRecords = indexBlockRecords;
        _scratch = new byte[Math.Max(AuditRecordCodec.RecordSize, AuditRecordCodec.FileHeaderSize)];
        // Worst-case index entry: prefix + indexBlockRecords distinct firm IDs
        // (the block can record at most 2*indexBlockRecords distinct firms but
        // realistic concurrency stays well below that — clamp at a safe upper
        // bound to avoid an OOB write in pathological cases).
        int maxFirmsPerBlock = Math.Min(indexBlockRecords * 2, ushort.MaxValue);
        _indexScratch = new byte[Math.Max(AuditIndexCodec.FileHeaderSize, AuditIndexCodec.BlockEntryFixedPrefix + (maxFirmsPerBlock * 4))];
    }

    /// <summary>Total records successfully appended since construction. Useful
    /// for tests and for PR-4's durability watermark accounting.</summary>
    public long RecordsWritten => _recordsWritten;

    /// <summary>UTC date of the currently-open log file, or <c>null</c> when
    /// no record has been written yet.</summary>
    public DateOnly? CurrentTradeDate => _stream is null ? null : _currentDate;

    public void OnTrade(in PostTradeRecord record)
    {
        lock (_checkpointLock)
        {
            // If a prior write already failed, refuse silently — the audit
            // log is in a known-broken state and further writes would only
            // deepen the hole. ChannelDispatcher's OnTrade catches and
            // swallows post-trade sink exceptions, so throwing here would
            // not even reach the operator; the deferred-truncate metric
            // (bumped on every Checkpoint attempt below) is the alert path.
            if (_writeFault) return;
            try
            {
                var recordDate = ToUtcDate(record.TransactTimeNanos);
                if (_stream is null || recordDate != _currentDate)
                {
                    RotateTo(recordDate);
                }
                if (_blockRecordCount == 0)
                    _blockStartOffset = _stream!.Position;

                int n = AuditRecordCodec.Encode(_scratch, in record);
                _stream!.Write(_scratch, 0, n);
                _stream.Flush(flushToDisk: false);
                _recordsWritten++;

                TrackFirm(record.BuyFirm);
                TrackFirm(record.SellFirm);
                _blockRecordCount++;
                if (_blockRecordCount >= _indexBlockRecords)
                    FlushCurrentBlock();
            }
            catch
            {
                // Poison the watermark for the rest of this writer's life.
                // Any subsequent OnCommandBoundary/Checkpoint will refuse to
                // advance _durableCommandSeq, keeping the WAL truncation
                // gate permanently closed until the operator restarts.
                _writeFault = true;
                throw;
            }
        }
    }

    /// <summary>Tags every record OnTrade'd since the previous boundary as
    /// "produced by <paramref name="commandSeq"/>". The watermark advances
    /// from pending to durable on the next <see cref="Checkpoint"/> call.
    /// Dispatch-thread-only; cheap (single store under the shared lock).
    /// No-op when the writer is in write-fault state.</summary>
    public void OnCommandBoundary(long commandSeq)
    {
        lock (_checkpointLock)
        {
            if (_writeFault) return;
            // Monotonic: tolerate replay paths that resubmit lower seqs.
            if (commandSeq > _pendingCommandSeq) _pendingCommandSeq = commandSeq;
        }
    }

    /// <summary>Flushes OS buffers, forces <c>fsync</c> on both files, and
    /// advances <see cref="DurableThroughCommandSeq"/> to the most recent
    /// <see cref="OnCommandBoundary"/> value. Safe to call from any thread
    /// (the lock serializes against OnTrade/OnCommandBoundary on the
    /// dispatch thread). If the fsync throws the watermark is NOT advanced
    /// — callers must treat that as "audit not durable; defer truncation".
    /// Throws when the writer is in write-fault state so the WAL truncation
    /// gate stays closed.</summary>
    public void Checkpoint()
    {
        lock (_checkpointLock)
        {
            if (_writeFault)
            {
                throw new IOException(
                    "audit log writer in write-fault state — refusing to advance durability watermark; restart the host after investigating the prior OnTrade failure");
            }
            // Snapshot the pending boundary BEFORE the fsync — any
            // concurrent OnCommandBoundary call would have to acquire
            // this lock so the value is stable while we fsync.
            long pending = _pendingCommandSeq;
            FlushCurrentBlock();
            _stream?.Flush(flushToDisk: true);
            _indexStream?.Flush(flushToDisk: true);
            // Advance only on success; if any Flush threw we propagate
            // without touching _durableCommandSeq so the gate stays closed.
            Volatile.Write(ref _durableCommandSeq, pending);
        }
    }

    /// <summary>Highest commandSeq whose OnTrade records are guaranteed
    /// fsync'd to disk. Read cross-thread by the WAL truncation gate;
    /// uses Volatile.Read for a torn-safe 64-bit read on ARM.</summary>
    public long DurableThroughCommandSeq => Volatile.Read(ref _durableCommandSeq);

    /// <summary>True once an OnTrade write has thrown; the writer is
    /// permanently broken and the watermark will not advance. Exposed
    /// for regression tests of the issue #329 PR-4 write-fault contract.</summary>
    internal bool WriteFault => _writeFault;

    /// <summary>Test-only hook to force the writer into write-fault state
    /// without engineering a real I/O failure. Exercises the watermark
    /// poisoning contract.</summary>
    internal void ForceWriteFaultForTests() { lock (_checkpointLock) { _writeFault = true; } }

    private void TrackFirm(uint firmId)
    {
        // Keep _blockFirmIds sorted + distinct via binary insertion. The list
        // is bounded by the firm-population in one block, which is small.
        int idx = _blockFirmIds.BinarySearch(firmId);
        if (idx < 0) _blockFirmIds.Insert(~idx, firmId);
    }

    private void FlushCurrentBlock()
    {
        if (_blockRecordCount == 0 || _indexStream is null) return;
        Span<uint> firms = _blockFirmIds.Count == 0 ? Span<uint>.Empty : System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_blockFirmIds);
        int n = AuditIndexCodec.EncodeBlockEntry(
            _indexScratch,
            (ulong)_blockStartOffset,
            checked((ushort)_blockRecordCount),
            firms);
        _indexStream.Write(_indexScratch, 0, n);
        _indexStream.Flush(flushToDisk: false);
        _blockRecordCount = 0;
        _blockFirmIds.Clear();
    }

    private void RotateTo(DateOnly newDate)
    {
        if (_stream is not null)
        {
            FlushCurrentBlock();
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
        }
        if (_indexStream is not null)
        {
            _indexStream.Flush(flushToDisk: true);
            _indexStream.Dispose();
            _indexStream = null;
        }
        var channelDir = Path.Combine(_rootDir, _channelNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(channelDir);
        var logPath = Path.Combine(channelDir, $"fills-{newDate:yyyy-MM-dd}.log");
        var idxPath = Path.Combine(channelDir, $"fills-{newDate:yyyy-MM-dd}.idx");
        var fs = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length == 0)
        {
            AuditRecordCodec.WriteFileHeader(_scratch, _channelNumber, newDate);
            fs.Write(_scratch, 0, AuditRecordCodec.FileHeaderSize);
        }
        else
        {
            long goodEnd = ScanLastGoodEndOffset(fs);
            if (goodEnd != fs.Length)
                fs.SetLength(goodEnd);
        }
        fs.Seek(0, SeekOrigin.End);
        _stream = fs;
        _currentDate = newDate;
        _blockRecordCount = 0;
        _blockFirmIds.Clear();

        // Always rebuild the .idx from scratch on (re)open. A partial block
        // at end-of-log invalidates the trailing index entry anyway, and
        // re-scanning is O(records) which the operator pays once per restart.
        OpenAndRebuildIndex(idxPath, fs, newDate);
    }

    private void OpenAndRebuildIndex(string idxPath, FileStream logStream, DateOnly newDate)
    {
        if (File.Exists(idxPath)) File.Delete(idxPath);
        var ix = new FileStream(idxPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        AuditIndexCodec.WriteFileHeader(_indexScratch, _channelNumber, newDate);
        ix.Write(_indexScratch, 0, AuditIndexCodec.FileHeaderSize);
        _indexStream = ix;
        ix.Flush(flushToDisk: false);
        if (logStream.Length <= AuditRecordCodec.FileHeaderSize) return;

        long savedPos = logStream.Position;
        try
        {
            logStream.Seek(AuditRecordCodec.FileHeaderSize, SeekOrigin.Begin);
            Span<byte> rec = stackalloc byte[AuditRecordCodec.RecordSize];
            long blockStart = logStream.Position;
            int recsInBlock = 0;
            var firmsInBlock = new List<uint>();
            while (true)
            {
                if (recsInBlock == 0) blockStart = logStream.Position;
                int read = logStream.Read(rec);
                if (read != rec.Length) break;
                if (!AuditRecordCodec.TryDecode(rec, out var decoded)) break;
                int idx;
                idx = firmsInBlock.BinarySearch(decoded.BuyFirm);
                if (idx < 0) firmsInBlock.Insert(~idx, decoded.BuyFirm);
                idx = firmsInBlock.BinarySearch(decoded.SellFirm);
                if (idx < 0) firmsInBlock.Insert(~idx, decoded.SellFirm);
                recsInBlock++;
                if (recsInBlock >= _indexBlockRecords)
                {
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(firmsInBlock);
                    int n = AuditIndexCodec.EncodeBlockEntry(_indexScratch, (ulong)blockStart, (ushort)recsInBlock, span);
                    ix.Write(_indexScratch, 0, n);
                    recsInBlock = 0;
                    firmsInBlock.Clear();
                }
            }
            if (recsInBlock > 0)
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(firmsInBlock);
                int n = AuditIndexCodec.EncodeBlockEntry(_indexScratch, (ulong)blockStart, (ushort)recsInBlock, span);
                ix.Write(_indexScratch, 0, n);
            }
            ix.Flush(flushToDisk: false);
        }
        finally
        {
            logStream.Seek(savedPos, SeekOrigin.Begin);
        }
    }

    private long ScanLastGoodEndOffset(FileStream fs)
    {
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> hdr = stackalloc byte[AuditRecordCodec.FileHeaderSize];
        if (fs.Read(hdr) != hdr.Length)
            throw new InvalidDataException($"audit file '{fs.Name}' truncated in header");
        var (channel, _) = AuditRecordCodec.ReadFileHeader(hdr);
        if (channel != _channelNumber)
            throw new InvalidDataException($"audit file '{fs.Name}' channel mismatch (file={channel}, writer={_channelNumber})");

        long goodEnd = AuditRecordCodec.FileHeaderSize;
        Span<byte> rec = stackalloc byte[AuditRecordCodec.RecordSize];
        while (true)
        {
            int read = fs.Read(rec);
            if (read != rec.Length) break;
            if (!AuditRecordCodec.TryDecode(rec, out _)) break;
            goodEnd += rec.Length;
        }
        return goodEnd;
    }

    private static DateOnly ToUtcDate(ulong nanosSinceUnixEpoch)
    {
        long ticks = (long)(nanosSinceUnixEpoch / 100UL);
        var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
        return DateOnly.FromDateTime(dt);
    }

    public void Dispose()
    {
        lock (_checkpointLock)
        {
            if (_stream is not null)
            {
                FlushCurrentBlock();
                _stream.Flush(flushToDisk: true);
                _stream.Dispose();
                _stream = null;
            }
            if (_indexStream is not null)
            {
                _indexStream.Flush(flushToDisk: true);
                _indexStream.Dispose();
                _indexStream = null;
            }
        }
    }
}
