using B3.Exchange.Matching.Threading;
using Microsoft.Win32.SafeHandles;

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
/// Threading model: mutable file/index/counter state is owned by the
/// <c>ChannelDispatcher</c>'s dispatch thread and guarded by
/// <see cref="SingleWriterGuard"/> (ADR 0009). Async snapshot callbacks
/// marshal checkpoint preparation back to the dispatcher; the returned
/// operation performs slow <c>fsync</c> on the snapshot writer thread
/// (issue #349).
///
/// Durability watermark (issue #329 PR-4): each <see cref="OnCommandBoundary"/>
/// records the engine's <c>commandSeq</c> as the highest seq covered by the
/// records OnTrade'd so far. <see cref="Checkpoint"/> fsyncs both files and
/// promotes that pending value into <see cref="DurableThroughCommandSeq"/>,
/// which the WAL truncation gate reads before dropping WAL records.
/// </summary>
public sealed class FileAuditLogWriter : IDispatchThreadCheckpointSink, IDisposable
{
    private readonly string _rootDir;
    private readonly byte _channelNumber;
    private readonly int _indexBlockRecords;
    private readonly byte[] _scratch;
    private readonly byte[] _indexScratch;
    private readonly SingleWriterGuard _writerGuard;

    private FileStream? _stream;
    private FileStream? _indexStream;
    private SafeFileHandle? _streamHandle;
    private SafeFileHandle? _indexStreamHandle;
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
    private int _writeFault;

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
        _writerGuard = new SingleWriterGuard($"FileAuditLogWriter[channel={channelNumber}]");
        _indexBlockRecords = indexBlockRecords;
        _scratch = new byte[Math.Max(AuditRecordCodec.RecordSize, AuditRecordCodec.FileHeaderSize)];
        // Worst-case index entry: prefix + indexBlockRecords distinct firm IDs
        // (the block can record at most 2*indexBlockRecords distinct firms but
        // realistic concurrency stays well below that — clamp at a safe upper
        // bound to avoid an OOB write in pathological cases).
        int maxFirmsPerBlock = Math.Min(indexBlockRecords * 2, ushort.MaxValue);
        _indexScratch = new byte[Math.Max(AuditIndexCodec.FileHeaderSize, AuditIndexCodec.BlockEntryFixedPrefix + (maxFirmsPerBlock * 4))];

        // Issue #329 PR-5: recover the durability watermark from the sidecar
        // so the dispatcher can gate WAL-replay OnTrade emissions (skip every
        // trade whose producing command was already fsync'd to the audit log
        // pre-crash). A missing/torn sidecar is treated as "watermark unknown
        // = 0", forcing the replay path to re-emit conservatively. Any
        // resulting duplicates are bounded by the [snapshot, crash] window.
        long recovered = TryReadWatermarkSidecar();
        _pendingCommandSeq = recovered;
        _durableCommandSeq = recovered;
    }

    private string WatermarkSidecarPath
        => Path.Combine(_rootDir, _channelNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), "audit-watermark.bin");

    private long TryReadWatermarkSidecar()
    {
        var path = WatermarkSidecarPath;
        if (!File.Exists(path)) return 0;
        try
        {
            Span<byte> buf = stackalloc byte[AuditWatermarkCodec.FileSize];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read = fs.Read(buf);
            if (read != buf.Length) return 0;
            return AuditWatermarkCodec.TryDecode(buf, out var seq) ? seq : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void WriteWatermarkSidecar(long lastDurableCommandSeq)
    {
        var path = WatermarkSidecarPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        Span<byte> buf = stackalloc byte[AuditWatermarkCodec.FileSize];
        AuditWatermarkCodec.Encode(buf, lastDurableCommandSeq);
        // Atomic update: write to tmp + fsync + rename. POSIX rename is
        // atomic; Windows File.Move(overwrite: true) is too. A crash mid-
        // write leaves the prior good sidecar (or no sidecar) intact.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(buf);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Total records successfully appended since construction. Useful
    /// for tests and for PR-4's durability watermark accounting.</summary>
    public long RecordsWritten => _recordsWritten;

    /// <summary>UTC date of the currently-open log file, or <c>null</c> when
    /// no record has been written yet.</summary>
    public DateOnly? CurrentTradeDate => _stream is null ? null : _currentDate;

    public SingleWriterGuard Guard => _writerGuard;

    public void OnTrade(in PostTradeRecord record)
    {
        _writerGuard.AssertOwnedByCurrentThread();
        // If a prior write already failed, refuse silently — the audit
        // log is in a known-broken state and further writes would only
        // deepen the hole. ChannelDispatcher's OnTrade catches and
        // swallows post-trade sink exceptions, so throwing here would
        // not even reach the operator; the deferred-truncate metric
        // (bumped on every Checkpoint attempt below) is the alert path.
        if (Volatile.Read(ref _writeFault) != 0) return;
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
            Volatile.Write(ref _writeFault, 1);
            throw;
        }
    }

    /// <summary>Tags every record OnTrade'd since the previous boundary as
    /// "produced by <paramref name="commandSeq"/>". The watermark advances
    /// from pending to durable on the next <see cref="Checkpoint"/> call.
    /// Dispatch-thread-only; cheap (single monotonic store).
    /// No-op when the writer is in write-fault state.</summary>
    public void OnCommandBoundary(long commandSeq)
    {
        _writerGuard.AssertOwnedByCurrentThread();
        if (Volatile.Read(ref _writeFault) != 0) return;
        // Monotonic: tolerate replay paths that resubmit lower seqs.
        if (commandSeq > _pendingCommandSeq) _pendingCommandSeq = commandSeq;
    }

    /// <summary>ADR 0008 PR-2: appends a bust audit record. When
    /// <paramref name="tradeDate"/> matches the currently-open log day the
    /// write is inline against <c>_stream</c>; otherwise a transient handle
    /// is opened to that day's <c>fills-YYYY-MM-DD.log</c>, the record is
    /// appended, fsynced, and the handle disposed. The .idx sidecar is
    /// NOT updated (only fill records contribute to the firm-sparse index;
    /// busts are queried via the v2 ReadAllEntries path).</summary>
    public void OnBust(in BustRecord record, DateOnly tradeDate)
    {
        _writerGuard.AssertOwnedByCurrentThread();
        if (Volatile.Read(ref _writeFault) != 0) return;
        try
        {
            Span<byte> buf = stackalloc byte[AuditRecordCodec.BustRecordSize];
            int n = AuditRecordCodec.EncodeBust(buf, in record);
            if (_stream is not null && tradeDate == _currentDate)
            {
                // Same-day inline: end any open fill block first so the
                // .idx invariant ("entries cover contiguous fill runs")
                // is preserved across the non-fill insertion.
                FlushCurrentBlock();
                _stream.Write(buf.Slice(0, n));
                _stream.Flush(flushToDisk: false);
                return;
            }
            // Cross-day write: open/create, fsync, close.
            AppendNonFillToOtherDay(tradeDate, buf.Slice(0, n));
        }
        catch
        {
            Volatile.Write(ref _writeFault, 1);
            throw;
        }
    }

    /// <summary>ADR 0008 PR-2 / §2.5: appends a reject-attempt audit
    /// record to TODAY's log (the day of <c>AttemptTransactTimeNanos</c>)
    /// so the audit trail explains the corresponding 4xx HTTP reply.
    /// </summary>
    public void OnRejectAttempt(in RejectAttemptRecord record)
    {
        _writerGuard.AssertOwnedByCurrentThread();
        if (Volatile.Read(ref _writeFault) != 0) return;
        try
        {
            var recordDate = ToUtcDate(record.AttemptTransactTimeNanos);
            Span<byte> buf = stackalloc byte[AuditRecordCodec.RejectAttemptRecordSize];
            int n = AuditRecordCodec.EncodeRejectAttempt(buf, in record);
            if (_stream is null || recordDate != _currentDate)
            {
                // OnRejectAttempt is dispatched on the dispatch thread,
                // so a rotation to today's log is the natural action:
                // future fills land on the same day. Mirrors OnTrade's
                // RotateTo path.
                RotateTo(recordDate);
            }
            FlushCurrentBlock();
            _stream!.Write(buf.Slice(0, n));
            _stream.Flush(flushToDisk: false);
        }
        catch
        {
            Volatile.Write(ref _writeFault, 1);
            throw;
        }
    }

    private void AppendNonFillToOtherDay(DateOnly tradeDate, ReadOnlySpan<byte> recordBytes)
    {
        var channelDir = Path.Combine(_rootDir, _channelNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(channelDir);
        var logPath = Path.Combine(channelDir, $"fills-{tradeDate:yyyy-MM-dd}.log");
        using var fs = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length == 0)
        {
            Span<byte> header = stackalloc byte[AuditRecordCodec.FileHeaderSize];
            AuditRecordCodec.WriteFileHeader(header, _channelNumber, tradeDate);
            fs.Write(header);
        }
        else
        {
            long goodEnd = ScanLastGoodEndOffset(fs);
            if (goodEnd != fs.Length) fs.SetLength(goodEnd);
        }
        fs.Seek(0, SeekOrigin.End);
        fs.Write(recordBytes);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Dispatch-thread prepare half of an audit checkpoint. It
    /// snapshots the pending boundary, flushes user-space buffers, and pins
    /// any open file handles. The returned operation owns the slow durable
    /// flush and watermark promotion.</summary>
    public IAuditCheckpointOperation BeginCheckpointOnDispatchThread()
    {
        _writerGuard.AssertOwnedByCurrentThread();
        if (Volatile.Read(ref _writeFault) != 0)
        {
            throw new IOException(
                "audit log writer in write-fault state — refusing to advance durability watermark; restart the host after investigating the prior OnTrade failure");
        }

        long pending = _pendingCommandSeq;
        FlushCurrentBlock();
        _stream?.Flush(flushToDisk: false);
        _indexStream?.Flush(flushToDisk: false);

        SafeFileHandle? streamHandle = _streamHandle;
        SafeFileHandle? indexHandle = _indexStreamHandle;
        bool streamRef = false;
        bool indexRef = false;
        try
        {
            if (streamHandle is not null) streamHandle.DangerousAddRef(ref streamRef);
            if (indexHandle is not null) indexHandle.DangerousAddRef(ref indexRef);
            return new CheckpointOperation(this, pending, streamHandle, indexHandle, streamRef, indexRef);
        }
        catch
        {
            if (streamRef) streamHandle!.DangerousRelease();
            if (indexRef) indexHandle!.DangerousRelease();
            throw;
        }
    }

    /// <summary>Flushes OS buffers, forces <c>fsync</c> on both files, and
    /// advances <see cref="DurableThroughCommandSeq"/> to the most recent
    /// <see cref="OnCommandBoundary"/> value. Must be entered from the
    /// dispatch thread; async snapshot mode calls
    /// <see cref="BeginCheckpointOnDispatchThread"/> on the dispatcher and
    /// runs the returned operation on the snapshot writer thread.</summary>
    public void Checkpoint()
    {
        using var checkpoint = BeginCheckpointOnDispatchThread();
        checkpoint.FlushToDiskAndCommit();
    }

    /// <summary>Highest commandSeq whose OnTrade records are guaranteed
    /// fsync'd to disk. Read cross-thread by the WAL truncation gate;
    /// uses Volatile.Read for a torn-safe 64-bit read on ARM.</summary>
    public long DurableThroughCommandSeq => Volatile.Read(ref _durableCommandSeq);

    /// <summary>True once an OnTrade write has thrown; the writer is
    /// permanently broken and the watermark will not advance. Exposed
    /// for regression tests of the issue #329 PR-4 write-fault contract.</summary>
    internal bool WriteFault => Volatile.Read(ref _writeFault) != 0;

    /// <summary>Test-only hook to force the writer into write-fault state
    /// without engineering a real I/O failure. Exercises the watermark
    /// poisoning contract.</summary>
    internal void ForceWriteFaultForTests()
    {
        _writerGuard.AssertOwnedByCurrentThread();
        Volatile.Write(ref _writeFault, 1);
    }

    private sealed class CheckpointOperation : IAuditCheckpointOperation
    {
        private readonly FileAuditLogWriter _owner;
        private readonly long _pending;
        private readonly SafeFileHandle? _streamHandle;
        private readonly SafeFileHandle? _indexHandle;
        private readonly bool _streamRef;
        private readonly bool _indexRef;
        private int _disposed;

        public CheckpointOperation(
            FileAuditLogWriter owner,
            long pending,
            SafeFileHandle? streamHandle,
            SafeFileHandle? indexHandle,
            bool streamRef,
            bool indexRef)
        {
            _owner = owner;
            _pending = pending;
            _streamHandle = streamHandle;
            _indexHandle = indexHandle;
            _streamRef = streamRef;
            _indexRef = indexRef;
        }

        public void FlushToDiskAndCommit()
        {
            try
            {
                if (_streamRef) RandomAccess.FlushToDisk(_streamHandle!);
                if (_indexRef) RandomAccess.FlushToDisk(_indexHandle!);
                // Issue #329 PR-5: persist the watermark sidecar after the
                // .log/.idx fsyncs, never before.
                _owner.WriteWatermarkSidecar(_pending);
                if (Volatile.Read(ref _owner._writeFault) == 0)
                    Volatile.Write(ref _owner._durableCommandSeq, _pending);
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_streamRef) _streamHandle!.DangerousRelease();
            if (_indexRef) _indexHandle!.DangerousRelease();
        }
    }

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
            _streamHandle = null;
        }
        if (_indexStream is not null)
        {
            _indexStream.Flush(flushToDisk: true);
            _indexStream.Dispose();
            _indexStream = null;
            _indexStreamHandle = null;
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
        // Cache the underlying SafeFileHandle so checkpoint operations can
        // call RandomAccess.FlushToDisk off the dispatch thread (issue #349).
        // The handle is owned by the FileStream; Dispose releases it.
        _streamHandle = fs.SafeFileHandle;
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
        _indexStreamHandle = ix.SafeFileHandle;
        ix.Flush(flushToDisk: false);
        if (logStream.Length <= AuditRecordCodec.FileHeaderSize) return;

        // Schema-aware rebuild (ADR 0008 PR-2): on v2 files the stream is
        // heterogeneous (fills + bust + reject-attempt records); only fills
        // contribute to the firm-sparse index. A non-fill record at any
        // position simply terminates the current fill block (writer
        // invariant: non-fill records are written outside any open block).
        long savedPos = logStream.Position;
        try
        {
            logStream.Seek(0, SeekOrigin.Begin);
            Span<byte> hdr = stackalloc byte[AuditRecordCodec.FileHeaderSize];
            if (logStream.Read(hdr) != hdr.Length) return;
            var (_, _, schemaVersion) = AuditRecordCodec.ReadFileHeader(hdr);
            bool schemaIsHeterogeneous = schemaVersion == AuditRecordCodec.SchemaVersionV2
                || schemaVersion == AuditRecordCodec.SchemaVersionV3;

            Span<byte> buffer = stackalloc byte[AuditRecordCodec.MaxRecordSize];
            Span<byte> lenPrefix = stackalloc byte[4];
            long blockStart = logStream.Position;
            int recsInBlock = 0;
            var firmsInBlock = new List<uint>();

            void FlushBlock()
            {
                if (recsInBlock == 0) return;
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(firmsInBlock);
                int n = AuditIndexCodec.EncodeBlockEntry(_indexScratch, (ulong)blockStart, (ushort)recsInBlock, span);
                ix.Write(_indexScratch, 0, n);
                recsInBlock = 0;
                firmsInBlock.Clear();
            }

            while (true)
            {
                long recordStart = logStream.Position;
                int recordSize;
                bool isFill;
                if (!schemaIsHeterogeneous)
                {
                    int read = logStream.Read(buffer.Slice(0, AuditRecordCodec.RecordSize));
                    if (read != AuditRecordCodec.RecordSize) break;
                    if (!AuditRecordCodec.TryDecode(buffer.Slice(0, AuditRecordCodec.RecordSize), out var decodedV1)) break;
                    if (recsInBlock == 0) blockStart = recordStart;
                    int idx;
                    idx = firmsInBlock.BinarySearch(decodedV1.BuyFirm);
                    if (idx < 0) firmsInBlock.Insert(~idx, decodedV1.BuyFirm);
                    idx = firmsInBlock.BinarySearch(decodedV1.SellFirm);
                    if (idx < 0) firmsInBlock.Insert(~idx, decodedV1.SellFirm);
                    recsInBlock++;
                    if (recsInBlock >= _indexBlockRecords) FlushBlock();
                    continue;
                }
                // v2/v3 dispatch
                int peeked = logStream.Read(lenPrefix);
                if (peeked == 0) break;
                if (peeked < 4) break;
                if (!AuditRecordCodec.TryPeekRecordLen(lenPrefix, out uint recordLen)) break;
                if (!AuditRecordCodec.TryGetRecordSize(recordLen, out recordSize, out var kind)) break;
                lenPrefix.CopyTo(buffer);
                int bodyRead = logStream.Read(buffer.Slice(4, recordSize - 4));
                if (bodyRead != recordSize - 4) break;
                isFill = kind == AuditRecordKind.Fill;
                if (!isFill)
                {
                    bool ok = kind switch
                    {
                        AuditRecordKind.Bust => AuditRecordCodec.TryDecodeBust(buffer.Slice(0, recordSize), out _),
                        AuditRecordKind.RejectAttempt => AuditRecordCodec.TryDecodeRejectAttempt(buffer.Slice(0, recordSize), out _),
                        _ => false,
                    };
                    if (!ok) break;
                    // Non-fill record ends any open block (writer invariant
                    // guarantees this didn't happen mid-block, but flush
                    // defensively to keep rebuild idempotent).
                    FlushBlock();
                    continue;
                }
                if (!AuditRecordCodec.TryDecode(buffer.Slice(0, recordSize), out var decoded)) break;
                if (recsInBlock == 0) blockStart = recordStart;
                int idxV2;
                idxV2 = firmsInBlock.BinarySearch(decoded.BuyFirm);
                if (idxV2 < 0) firmsInBlock.Insert(~idxV2, decoded.BuyFirm);
                idxV2 = firmsInBlock.BinarySearch(decoded.SellFirm);
                if (idxV2 < 0) firmsInBlock.Insert(~idxV2, decoded.SellFirm);
                recsInBlock++;
                if (recsInBlock >= _indexBlockRecords) FlushBlock();
            }
            FlushBlock();
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
        var (channel, _, schemaVersion) = AuditRecordCodec.ReadFileHeader(hdr);
        if (channel != _channelNumber)
            throw new InvalidDataException($"audit file '{fs.Name}' channel mismatch (file={channel}, writer={_channelNumber})");
        // ADR 0008 §1 per-day schema view: writer accepts files of any
        // schema it understands (V1 from pre-PR-2 days, V2 from PR-2,
        // V3 from PR-4 onward). The recovery scan dispatches by
        // recordLen so v2/v3 files with mixed fill/bust/reject-attempt
        // records are scanned correctly. Refuse anything we don't
        // recognise.
        if (schemaVersion != AuditRecordCodec.SchemaVersionV1
            && schemaVersion != AuditRecordCodec.SchemaVersionV2
            && schemaVersion != AuditRecordCodec.SchemaVersionV3)
            throw new InvalidDataException(
                $"audit file '{fs.Name}' unsupported schema v{schemaVersion} "
                + $"(writer understands v{AuditRecordCodec.SchemaVersionV1}, v{AuditRecordCodec.SchemaVersionV2}, v{AuditRecordCodec.SchemaVersionV3})");

        long goodEnd = AuditRecordCodec.FileHeaderSize;
        Span<byte> buffer = stackalloc byte[AuditRecordCodec.MaxRecordSize];
        Span<byte> lenPrefix = stackalloc byte[4];
        while (true)
        {
            if (schemaVersion == AuditRecordCodec.SchemaVersionV1)
            {
                int read = fs.Read(buffer.Slice(0, AuditRecordCodec.RecordSize));
                if (read != AuditRecordCodec.RecordSize) break;
                if (!AuditRecordCodec.TryDecode(buffer.Slice(0, AuditRecordCodec.RecordSize), out _)) break;
                goodEnd += AuditRecordCodec.RecordSize;
                continue;
            }
            // v2: peek 4-byte recordLen, dispatch by kind, validate via
            // type-specific TryDecode so a corrupt entry stops the scan.
            int peeked = fs.Read(lenPrefix);
            if (peeked == 0) break;
            if (peeked < 4) break;
            if (!AuditRecordCodec.TryPeekRecordLen(lenPrefix, out uint recordLen)) break;
            if (!AuditRecordCodec.TryGetRecordSize(recordLen, out int onDiskSize, out var kind)) break;
            lenPrefix.CopyTo(buffer);
            int bodyRead = fs.Read(buffer.Slice(4, onDiskSize - 4));
            if (bodyRead != onDiskSize - 4) break;
            bool ok = kind switch
            {
                AuditRecordKind.Fill => AuditRecordCodec.TryDecode(buffer.Slice(0, onDiskSize), out _),
                AuditRecordKind.Bust => AuditRecordCodec.TryDecodeBust(buffer.Slice(0, onDiskSize), out _),
                AuditRecordKind.RejectAttempt => AuditRecordCodec.TryDecodeRejectAttempt(buffer.Slice(0, onDiskSize), out _),
                _ => false,
            };
            if (!ok) break;
            goodEnd += onDiskSize;
        }
        return goodEnd;
    }

    private static DateOnly ToUtcDate(ulong nanosSinceUnixEpoch)
    {
        long ticks = (long)(nanosSinceUnixEpoch / 100UL);
        var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
        return DateOnly.FromDateTime(dt);
    }

    /// <summary>
    /// Issue #329 PR-6: delete on-disk audit files for UTC business
    /// dates older than <c>todayUtc - retentionDays</c>. Files matching
    /// the pattern <c>fills-YYYY-MM-DD.log</c> / <c>fills-YYYY-MM-DD.idx</c>
    /// under the channel directory are removed in pairs; malformed or
    /// non-date filenames are ignored. The currently-open day (i.e. the
    /// one this writer is actively appending to) is never pruned even if
    /// the calendar has rolled — operators must let the writer rotate first.
    /// The watermark sidecar (<c>audit-watermark.bin</c>) is per-channel,
    /// not per-day, and is intentionally NOT pruned.
    ///
    /// <para><paramref name="retentionDays"/> must be &gt;= 1. Pass the
    /// compliance-mandated horizon (e.g. 5 years for B3 fills) or a
    /// shorter operator value during development. Returns the number of
    /// (.log, .idx) pairs deleted; partial pairs (an orphan .log or
    /// .idx) are still counted toward the deletion total per file.</para>
    ///
    /// <para>Best-effort operator maintenance hook. It does not mutate the
    /// active stream; it snapshots the currently-open day and never prunes
    /// that day.</para>
    /// </summary>
    public int PruneOldDays(DateOnly todayUtc, int retentionDays)
    {
        if (retentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "retentionDays must be >= 1");
        var channelDir = Path.Combine(_rootDir, _channelNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(channelDir)) return 0;
        var cutoff = todayUtc.AddDays(-retentionDays);
        int deleted = 0;
        DateOnly? activeDate = _stream is null ? null : _currentDate;
        foreach (var path in Directory.EnumerateFiles(channelDir, "fills-*.*"))
        {
            var name = Path.GetFileName(path);
            if (!TryParseFillsDate(name, out var date, out var ext)) continue;
            if (ext != ".log" && ext != ".idx") continue;
            // Never delete the currently-open day even if it falls
            // before the cutoff (e.g. a clock skew or aggressive
            // retentionDays=1 during development).
            if (activeDate is { } current && date == current) continue;
            if (date >= cutoff) continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (IOException)
            {
                // Best-effort: file in use or transient FS error;
                // the next prune pass will retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Permissions issue — leave for the operator.
            }
        }
        return deleted;
    }

    private static bool TryParseFillsDate(string fileName, out DateOnly date, out string ext)
    {
        date = default;
        ext = string.Empty;
        // Expected: fills-YYYY-MM-DD.log | fills-YYYY-MM-DD.idx
        const string prefix = "fills-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int dot = fileName.LastIndexOf('.');
        if (dot <= prefix.Length) return false;
        var dateSpan = fileName.AsSpan(prefix.Length, dot - prefix.Length);
        if (!DateOnly.TryParseExact(dateSpan, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            return false;
        ext = fileName[dot..];
        return true;
    }

    public void Dispose()
    {
        if (_stream is not null)
        {
            FlushCurrentBlock();
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
            _streamHandle = null;
        }
        if (_indexStream is not null)
        {
            _indexStream.Flush(flushToDisk: true);
            _indexStream.Dispose();
            _indexStream = null;
            _indexStreamHandle = null;
        }
    }
}
