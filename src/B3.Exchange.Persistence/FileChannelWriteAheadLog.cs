using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// File-system backed implementation of
/// <see cref="IChannelWriteAheadLog"/> (issue #269). One WAL file per
/// channel under <c>DataDirectory</c>:
/// <c>channel-{N}.wal</c>, written as JSON-Lines (one
/// <see cref="WalRecord"/> per line, terminated by <c>\n</c>).
///
/// <para>Append semantics: each record is JSON-serialized, suffixed
/// with a <c>\t</c>-separated 8-hex-digit Crc32C of the JSON bytes
/// (issue #285), terminated by a newline, and the implementation
/// optionally <c>fsync</c>s the file before returning. The
/// JSON-Lines + per-record CRC framing makes the file robust against
/// two distinct failure modes that demand different responses:
/// <list type="bullet">
///   <item><b>Torn final write:</b> the host crashed mid-fsync and
///   the last physical line is incomplete. The trailing CRC fails (or
///   the line has no CRC suffix and JSON parse fails), and the loader
///   tolerates that by dropping the final record and stopping
///   replay. The surviving prefix is fully replayable.</item>
///   <item><b>Mid-stream corruption:</b> a CRC mismatch, parse
///   failure, or sequence gap on any record that has at least one
///   well-formed record after it. Continuing past the bad record
///   would apply state transitions out of order and produce a book
///   state that never existed, so the loader raises a
///   <see cref="WalCorruptionException"/> (issue #285 follow-up after
///   gpt-5.5 review of PR #293). The dispatcher catches this and
///   marks the channel WAL-halted (issue #286 path).</item>
/// </list>
/// Per-write fsync gives zero-RPO recovery; the caller can opt for
/// batched fsync (lower latency, RPO bounded by batch flush) via the
/// <c>fsyncPerWrite=false</c> ctor argument.</para>
///
/// <para>Backwards compatibility: lines without the <c>\t</c>+CRC
/// suffix (i.e. files written by pre-#285 hosts) are accepted as
/// "legacy" records and counted via <see cref="LastReadLegacyCount"/>
/// so operators can monitor migration. A legacy line that fails to
/// JSON-parse is treated as torn-final and stops replay (preserving
/// the original behavior).</para>
///
/// <para>Truncation rewrites the file to an empty version atomically
/// (tmp + rename + dir fsync) so a crash during truncate either keeps
/// the previous WAL intact or atomically swaps it for an empty file —
/// never leaves a partial truncation visible.</para>
/// </summary>
public sealed class FileChannelWriteAheadLog : IChannelWriteAheadLog, IDisposable
{
    private const string WalFileNameFormat = "channel-{0}.wal";
    private const string WalTempFileNameFormat = "channel-{0}.wal.tmp";
    private const byte CrcSeparator = (byte)'\t';

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private readonly string _path;
    private readonly string _dataDir;
    private readonly bool _fsyncPerWrite;
    private readonly long _maxBytes;
    private readonly WalSizeCapPolicy _onFull;
    private readonly ILogger<FileChannelWriteAheadLog> _logger;
    private readonly byte _channelNumber;
    private readonly object _writeLock = new();
    private FileStream? _appendStream;
    private long _currentSize;
    private long _dropsOnFull;

    // Issue #312 (Tier-2 perf): GroupCommit mode infrastructure.
    // _pendingSeq advances in Append (under _writeLock); _durableSeq
    // advances after the background fsync completes. Waiters block on
    // _durabilityLock and are pulsed when _durableSeq advances.
    private readonly bool _groupCommit;
    private readonly TimeSpan _groupCommitInterval;
    private readonly object _durabilityLock = new();
    private readonly ManualResetEventSlim _fsyncSignal;
    private readonly CancellationTokenSource? _stopFsyncThread;
    private readonly Thread? _fsyncThread;
    private long _pendingSeq;
    private long _durableSeq;

    /// <summary>
    /// Number of records the most recent <see cref="ReadAll"/> call
    /// rejected because their stored Crc32C did not match the JSON
    /// payload (bit-rot). The dispatcher reads this after replay and
    /// bumps <c>exch_wal_record_corruption_total</c>.
    /// </summary>
    public int LastReadCorruptCount { get; private set; }

    /// <summary>
    /// Number of records the most recent <see cref="ReadAll"/> call
    /// accepted that had no Crc32C suffix (i.e. were written by a
    /// pre-#285 host). Useful for tracking the migration window
    /// after upgrading.
    /// </summary>
    public int LastReadLegacyCount { get; private set; }

    /// <summary>Issue #291: current on-disk size in bytes; surfaces
    /// <c>exch_wal_size_bytes</c> via <see cref="CurrentSizeBytes"/>.</summary>
    public long CurrentSizeBytes => Interlocked.Read(ref _currentSize);

    /// <summary>Issue #291: cumulative count of appends silently
    /// skipped under <see cref="WalSizeCapPolicy.Drop"/>.</summary>
    public long DropsOnFullCount => Interlocked.Read(ref _dropsOnFull);

    /// <summary>Issue #312: see
    /// <see cref="IChannelWriteAheadLog.PendingDurableSeqOrZero"/>.</summary>
    public long PendingDurableSeqOrZero => Interlocked.Read(ref _pendingSeq);

    /// <summary>Issue #312: see
    /// <see cref="IChannelWriteAheadLog.DurableSeqOrZero"/>.</summary>
    public long DurableSeqOrZero => Interlocked.Read(ref _durableSeq);

    public string Path => _path;

    public FileChannelWriteAheadLog(
        string dataDirectory,
        byte channelNumber,
        ILogger<FileChannelWriteAheadLog> logger,
        bool fsyncPerWrite = true)
        : this(dataDirectory, channelNumber, logger, fsyncPerWrite,
            maxBytes: 0, onFull: WalSizeCapPolicy.Halt)
    {
    }

    /// <summary>
    /// Issue #291 overload accepting an on-disk size cap and the
    /// policy applied when the next append would exceed it.
    /// <paramref name="maxBytes"/> &lt;= 0 disables the cap (legacy
    /// unbounded behaviour). When the cap is positive,
    /// <paramref name="onFull"/>:
    /// <list type="bullet">
    ///   <item><see cref="WalSizeCapPolicy.Halt"/> (default) throws
    ///   <see cref="WalSizeCapExceededException"/> from
    ///   <see cref="Append"/>. The dispatcher recognises this
    ///   exception specifically and marks the channel WAL-halted
    ///   regardless of <see cref="WalAppendFailurePolicy"/>.</item>
    ///   <item><see cref="WalSizeCapPolicy.Drop"/> silently skips the
    ///   write, logs at Warning, and increments
    ///   <see cref="DropsOnFullCount"/> (surfaced as
    ///   <c>exch_wal_drops_on_full_total</c>).</item>
    /// </list>
    /// </summary>
    public FileChannelWriteAheadLog(
        string dataDirectory,
        byte channelNumber,
        ILogger<FileChannelWriteAheadLog> logger,
        bool fsyncPerWrite,
        long maxBytes,
        WalSizeCapPolicy onFull)
        : this(dataDirectory, channelNumber, logger, fsyncPerWrite, maxBytes, onFull,
            fsyncMode: WalFsyncMode.PerWrite, groupCommitInterval: default)
    {
    }

    /// <summary>
    /// Issue #312 (Tier-2 perf): full constructor accepting the
    /// <see cref="WalFsyncMode"/> selector and the
    /// <paramref name="groupCommitInterval"/> used in
    /// <see cref="WalFsyncMode.GroupCommit"/> mode as the maximum
    /// time between background fsync passes (i.e. the worst-case
    /// RPO when no caller blocks on
    /// <see cref="WaitForDurable"/>). When the interval is
    /// <c>default</c> a 1ms default is applied — small enough to
    /// bound RPO and large enough to amortise fsync syscall cost
    /// across multiple appends under load.
    ///
    /// <para><see cref="WalFsyncMode.GroupCommit"/> requires
    /// <paramref name="fsyncPerWrite"/> = <c>false</c>; passing
    /// the conflicting combination throws
    /// <see cref="ArgumentException"/>. Both legacy fsync flags
    /// (<paramref name="fsyncPerWrite"/>) and the new mode are
    /// kept on the surface so existing call sites need not
    /// change.</para>
    /// </summary>
    public FileChannelWriteAheadLog(
        string dataDirectory,
        byte channelNumber,
        ILogger<FileChannelWriteAheadLog> logger,
        bool fsyncPerWrite,
        long maxBytes,
        WalSizeCapPolicy onFull,
        WalFsyncMode fsyncMode,
        TimeSpan groupCommitInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        if (fsyncMode == WalFsyncMode.GroupCommit && fsyncPerWrite)
        {
            throw new ArgumentException(
                "WalFsyncMode.GroupCommit is incompatible with fsyncPerWrite=true; choose one durability strategy.",
                nameof(fsyncMode));
        }
        _dataDir = System.IO.Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDir);
        _channelNumber = channelNumber;
        _fsyncPerWrite = fsyncPerWrite;
        _maxBytes = maxBytes > 0 ? maxBytes : 0;
        _onFull = onFull;
        _logger = logger;
        _path = System.IO.Path.Combine(_dataDir,
            string.Format(CultureInfo.InvariantCulture, WalFileNameFormat, channelNumber));
        // Initialise the size counter from any existing on-disk file
        // so a host restart inherits the pre-restart usage instead of
        // claiming the WAL is empty until the next truncation.
        if (File.Exists(_path))
        {
            try { _currentSize = new FileInfo(_path).Length; }
            catch { _currentSize = 0; }
        }

        _groupCommit = fsyncMode == WalFsyncMode.GroupCommit;
        _groupCommitInterval = _groupCommit
            ? (groupCommitInterval > TimeSpan.Zero ? groupCommitInterval : TimeSpan.FromMilliseconds(1))
            : TimeSpan.Zero;
        _fsyncSignal = new ManualResetEventSlim(initialState: false);
        if (_groupCommit)
        {
            _stopFsyncThread = new CancellationTokenSource();
            _fsyncThread = new Thread(GroupCommitFsyncLoop)
            {
                IsBackground = true,
                Name = $"wal-fsync-ch{channelNumber}",
            };
            _fsyncThread.Start();
        }
    }

    public int Append(WalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_writeLock)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(record, WalJsonContext.Default.WalRecord);
            // Layout: <json bytes> \t <8-hex Crc32C> \n
            int recordBytes = json.Length + 1 /* \t */ + 8 /* hex crc */ + 1 /* \n */;
            // Issue #291: enforce on-disk size cap before opening the
            // append stream so a Drop policy never even creates the
            // file when the cap is already exceeded by replay state.
            if (_maxBytes > 0 && _currentSize + recordBytes > _maxBytes)
            {
                if (_onFull == WalSizeCapPolicy.Drop)
                {
                    Interlocked.Increment(ref _dropsOnFull);
                    _logger.LogWarning(
                        "WAL channel-{Channel}: size cap reached (current={Current}B, incoming={Incoming}B, max={Max}B); dropping record (onFull=drop)",
                        _channelNumber, _currentSize, recordBytes, _maxBytes);
                    return 0;
                }
                throw new WalSizeCapExceededException(_currentSize, _maxBytes, recordBytes);
            }
            _appendStream ??= new FileStream(_path,
                FileMode.Append, FileAccess.Write, FileShare.Read);
            uint crc = Crc32C.Compute(json);
            Span<byte> crcBytes = stackalloc byte[8];
            WriteHexUtf8(crc, crcBytes);
            _appendStream.Write(json, 0, json.Length);
            _appendStream.WriteByte(CrcSeparator);
            _appendStream.Write(crcBytes);
            _appendStream.WriteByte((byte)'\n');
            if (_groupCommit)
            {
                // Push bytes to the OS so the background fsync thread
                // (which doesn't take _writeLock) sees a consistent
                // snapshot of the file contents. The disk-level sync
                // is deferred to GroupCommitFsyncLoop.
                _appendStream.Flush();
                Interlocked.Exchange(ref _pendingSeq, record.Seq);
                _fsyncSignal.Set();
            }
            else if (_fsyncPerWrite)
            {
                _appendStream.Flush(flushToDisk: true);
                Interlocked.Exchange(ref _pendingSeq, record.Seq);
                Interlocked.Exchange(ref _durableSeq, record.Seq);
            }
            else
            {
                _appendStream.Flush();
                Interlocked.Exchange(ref _pendingSeq, record.Seq);
            }
            Interlocked.Add(ref _currentSize, recordBytes);
            return recordBytes;
        }
    }

    public void WaitForDurable(long seq, CancellationToken cancellationToken = default)
    {
        if (seq <= 0) return;
        if (Interlocked.Read(ref _durableSeq) >= seq) return;
        if (!_groupCommit)
        {
            // PerWrite / fire-and-forget Flush modes advance _durableSeq
            // (or skip durability entirely) inside Append itself; if we
            // haven't reached `seq` yet it never will via this WAL.
            return;
        }
        // Nudge the bg thread in case the signal was missed (e.g. a
        // caller blocked between Append and WaitForDurable while the
        // bg thread was already sleeping).
        _fsyncSignal.Set();
        lock (_durabilityLock)
        {
            while (Interlocked.Read(ref _durableSeq) < seq)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_durabilityLock, _groupCommitInterval);
            }
        }
    }

    private void GroupCommitFsyncLoop()
    {
        var stopToken = _stopFsyncThread!.Token;
        while (!stopToken.IsCancellationRequested)
        {
            try { _fsyncSignal.Wait(_groupCommitInterval, stopToken); }
            catch (OperationCanceledException) { break; }
            _fsyncSignal.Reset();
            FsyncOnce();
        }
        // Final pass on shutdown so anything appended between the last
        // signal and the cancellation request still hits the disk.
        FsyncOnce();
    }

    private void FsyncOnce()
    {
        long pending;
        lock (_writeLock)
        {
            if (_appendStream is null) return;
            pending = Interlocked.Read(ref _pendingSeq);
            if (pending <= Interlocked.Read(ref _durableSeq)) return;
            try
            {
                _appendStream.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: WAL background fsync failed; durable seq stays at {Durable}",
                    _channelNumber, Interlocked.Read(ref _durableSeq));
                return;
            }
        }
        // Publish the new durability watermark and pulse waiters
        // outside the write lock so an Append on the dispatch thread
        // never contends with a wakeup.
        lock (_durabilityLock)
        {
            // Use Math.Max because pending may have advanced again
            // between our snapshot above and the lock acquisition; we
            // only ever want to MOVE FORWARD here.
            long current = Interlocked.Read(ref _durableSeq);
            if (pending > current)
            {
                Interlocked.Exchange(ref _durableSeq, pending);
            }
            Monitor.PulseAll(_durabilityLock);
        }
    }

    public IReadOnlyList<WalRecord> ReadAll()
    {
        LastReadCorruptCount = 0;
        LastReadLegacyCount = 0;
        if (!File.Exists(_path)) return Array.Empty<WalRecord>();
        // Close any open append handle so reads see all flushed bytes
        // and the read can re-open the file with shared access.
        lock (_writeLock)
        {
            if (_appendStream is not null)
            {
                _appendStream.Flush(flushToDisk: true);
                _appendStream.Dispose();
                _appendStream = null;
            }
        }
        var result = new List<WalRecord>();
        // Buffer all physical lines first so we can distinguish a
        // torn final write (tolerable) from mid-stream corruption
        // (must halt). The legacy "skip and keep going" path was
        // unsafe — see WalCorruptionException for the rationale.
        List<string> rawLines;
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            rawLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                rawLines.Add(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: failed to read WAL at {Path}; treating as empty",
                _channelNumber, _path);
            return Array.Empty<WalRecord>();
        }

        // Find the index of the LAST non-empty line — only that line
        // is allowed to fail integrity (torn-tail tolerance). Lines
        // beyond it must be empty (a partial trailing newline is fine).
        int lastNonEmptyIdx = -1;
        for (int i = rawLines.Count - 1; i >= 0; i--)
        {
            if (rawLines[i].Length > 0) { lastNonEmptyIdx = i; break; }
        }

        long? prevSeq = null;
        for (int i = 0; i <= lastNonEmptyIdx; i++)
        {
            var line = rawLines[i];
            int lineNumber = i + 1;
            bool isLastNonEmpty = i == lastNonEmptyIdx;
            if (line.Length == 0) continue;

            int tab = line.LastIndexOf('\t');
            if (tab >= 0 && line.Length - tab - 1 == 8)
            {
                // Modern format: JSON \t crc8 .
                var crcHex = line.AsSpan(tab + 1, 8);
                if (!TryParseHex32(crcHex, out uint storedCrc))
                {
                    // Suffix isn't a valid 8-hex CRC — treat as legacy
                    // (no CRC) and fall through.
                    if (!ConsumeLegacyLine(line, lineNumber, isLastNonEmpty, result, ref prevSeq))
                        break;
                    continue;
                }
                var jsonBytes = Encoding.UTF8.GetBytes(line[..tab]);
                uint computedCrc = Crc32C.Compute(jsonBytes);
                if (computedCrc != storedCrc)
                {
                    LastReadCorruptCount++;
                    if (isLastNonEmpty)
                    {
                        // Torn final write — tolerate and stop.
                        _logger.LogWarning(
                            "channel {ChannelNumber}: WAL line {LineNumber} CRC mismatch on the FINAL record (stored=0x{StoredCrc:X8} computed=0x{ComputedCrc:X8}); treating as torn-tail and truncating replay here",
                            _channelNumber, lineNumber, storedCrc, computedCrc);
                        break;
                    }
                    // Mid-stream corruption — refuse to fabricate a
                    // book state by skipping past the gap.
                    var msg = $"channel {_channelNumber}: WAL line {lineNumber} CRC mismatch (stored=0x{storedCrc:X8} computed=0x{computedCrc:X8}) and a well-formed record exists after it; refusing to replay around mid-stream corruption";
                    _logger.LogCritical("{Message}", msg);
                    throw new WalCorruptionException(_channelNumber, lineNumber, msg);
                }
                WalRecord? rec;
                try
                {
                    rec = JsonSerializer.Deserialize<WalRecord>(jsonBytes, JsonOptions);
                }
                catch (Exception ex)
                {
                    // CRC matched but JSON failed — the on-disk bytes
                    // are coherent w.r.t. their checksum but the
                    // schema is wrong (e.g. an enum value the loader
                    // does not recognize). That is a persistent
                    // corruption-equivalent condition, never a torn
                    // write; halt regardless of position.
                    LastReadCorruptCount++;
                    var msg = $"channel {_channelNumber}: WAL line {lineNumber} JSON parse failed despite CRC match: {ex.Message}";
                    _logger.LogCritical(ex, "{Message}", msg);
                    throw new WalCorruptionException(_channelNumber, lineNumber, msg, ex);
                }
                if (rec is null)
                {
                    var msg = $"channel {_channelNumber}: WAL line {lineNumber} deserialized to null";
                    _logger.LogCritical("{Message}", msg);
                    throw new WalCorruptionException(_channelNumber, lineNumber, msg);
                }
                CheckSeqContiguity(rec.Seq, lineNumber, ref prevSeq);
                result.Add(rec);
            }
            else
            {
                if (!ConsumeLegacyLine(line, lineNumber, isLastNonEmpty, result, ref prevSeq))
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Enforces strict monotonic-by-1 ordering on WAL record
    /// sequences. A gap means we lost a write between the surviving
    /// records — replaying the records on either side of the gap
    /// applies state transitions out of order, so we treat any
    /// non-contiguous seq as mid-stream corruption.
    /// </summary>
    private void CheckSeqContiguity(long currentSeq, int lineNumber, ref long? prevSeq)
    {
        if (prevSeq.HasValue && currentSeq != prevSeq.Value + 1)
        {
            var msg = $"channel {_channelNumber}: WAL line {lineNumber} seq={currentSeq} is not contiguous after seq={prevSeq.Value}; refusing to replay around the gap";
            _logger.LogCritical("{Message}", msg);
            throw new WalCorruptionException(_channelNumber, lineNumber, msg);
        }
        prevSeq = currentSeq;
    }

    /// <summary>
    /// Handles a line written by a pre-#285 host (no <c>\t</c>+CRC
    /// suffix). Returns <c>false</c> to signal the caller to stop
    /// further line processing — preserving the original "torn-final
    /// stops replay" behavior for legacy files when the failure is on
    /// the final line. A parse failure on a NON-final legacy line is
    /// promoted to a <see cref="WalCorruptionException"/> for the
    /// same reason mid-stream modern corruption is fatal: skipping
    /// past it would replay state transitions out of order.
    /// </summary>
    private bool ConsumeLegacyLine(string line, int lineNumber, bool isLastNonEmpty, List<WalRecord> result, ref long? prevSeq)
    {
        WalRecord? rec;
        try
        {
            rec = JsonSerializer.Deserialize<WalRecord>(line, JsonOptions);
        }
        catch (Exception ex)
        {
            if (isLastNonEmpty)
            {
                _logger.LogWarning(ex,
                    "channel {ChannelNumber}: legacy WAL line {LineNumber} failed to parse on the FINAL record; treating as torn-tail and truncating replay here",
                    _channelNumber, lineNumber);
                return false;
            }
            var msg = $"channel {_channelNumber}: legacy WAL line {lineNumber} failed to parse and a well-formed record exists after it; refusing to replay around mid-stream corruption";
            _logger.LogCritical(ex, "{Message}", msg);
            throw new WalCorruptionException(_channelNumber, lineNumber, msg, ex);
        }
        if (rec is null)
        {
            if (isLastNonEmpty) return false;
            var msg = $"channel {_channelNumber}: legacy WAL line {lineNumber} deserialized to null mid-stream";
            _logger.LogCritical("{Message}", msg);
            throw new WalCorruptionException(_channelNumber, lineNumber, msg);
        }
        CheckSeqContiguity(rec.Seq, lineNumber, ref prevSeq);
        result.Add(rec);
        LastReadLegacyCount++;
        return true;
    }

    private static void WriteHexUtf8(uint value, Span<byte> dst)
    {
        // dst is exactly 8 bytes; write big-endian-nibble hex digits.
        const string Hex = "0123456789ABCDEF";
        for (int i = 7; i >= 0; i--)
        {
            dst[i] = (byte)Hex[(int)(value & 0xF)];
            value >>= 4;
        }
    }

    private static bool TryParseHex32(ReadOnlySpan<char> chars, out uint value)
    {
        value = 0;
        if (chars.Length != 8) return false;
        for (int i = 0; i < 8; i++)
        {
            int d = HexDigit(chars[i]);
            if (d < 0) return false;
            value = (value << 4) | (uint)d;
        }
        return true;

        static int HexDigit(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    public void Truncate()
    {
        lock (_writeLock)
        {
            TruncateLocked();
        }
    }

    private void TruncateLocked()
    {
        if (_appendStream is not null)
        {
            _appendStream.Dispose();
            _appendStream = null;
        }
        // Atomic truncate: write empty file to tmp, rename over.
        var tmp = System.IO.Path.Combine(_dataDir,
            string.Format(CultureInfo.InvariantCulture, WalTempFileNameFormat, _channelNumber));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
            FsyncDirectory(_dataDir);
            Interlocked.Exchange(ref _currentSize, 0);
            // Issue #312: every previously-appended record is now
            // either durable (it was fsynced into the old file
            // before snapshot persist asked us to truncate) or
            // intentionally discarded — either way it can't be
            // replayed any more, so it counts as "durable" for
            // any caller still waiting on its seq. Advancing
            // _durableSeq here unblocks them instead of letting
            // them spin until the fsync thread notices the empty
            // file.
            AdvanceDurableSeqOnTruncate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: failed to truncate WAL at {Path}",
                _channelNumber, _path);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Issue #348: atomic prefix truncate — drops every record with
    /// <c>Seq &lt;= throughSeq</c> and KEEPS every record with
    /// <c>Seq &gt; throughSeq</c>. Closes the async-snapshot race
    /// where the dispatch thread could append a record past the
    /// snapshot's <c>LastAppliedSeq</c> between
    /// <see cref="BackgroundSnapshotWriter"/>'s <c>Submit</c> and
    /// <c>onSaved</c> callback firing; a full <see cref="Truncate"/>
    /// in that window would silently drop the tail and a subsequent
    /// crash would lose it.
    ///
    /// <para>Implementation: under <c>_writeLock</c>, reads the
    /// surviving tail via <see cref="ReadAll"/>, writes a fresh tmp
    /// file containing only the kept records, atomically renames
    /// over the live WAL, fsyncs the directory. Equivalent to
    /// <see cref="Truncate"/> when no records survive (kept=0); a
    /// no-op when every record is above the cutoff.</para>
    /// </summary>
    public void TruncateThrough(long throughSeq)
    {
        lock (_writeLock)
        {
            // Snapshot the surviving tail BEFORE we touch the file —
            // ReadAll opens its own FileStream with FileShare.Read, so
            // this is safe even while _appendStream is still open. We
            // close _appendStream below so the rename can succeed on
            // Windows (Move-with-overwrite refuses an open file).
            var all = ReadAll();
            var kept = new List<WalRecord>(all.Count);
            foreach (var rec in all)
            {
                if (rec.Seq > throughSeq) kept.Add(rec);
            }
            if (kept.Count == all.Count)
            {
                // Cutoff is below every record on disk → nothing to do.
                return;
            }
            if (kept.Count == 0)
            {
                // Equivalent to a full truncate.
                TruncateLocked();
                return;
            }
            if (_appendStream is not null)
            {
                _appendStream.Dispose();
                _appendStream = null;
            }
            var tmp = System.IO.Path.Combine(_dataDir,
                string.Format(CultureInfo.InvariantCulture, WalTempFileNameFormat, _channelNumber));
            try
            {
                long newSize = 0;
                Span<byte> crcBytes = stackalloc byte[8];
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var rec in kept)
                    {
                        var json = JsonSerializer.SerializeToUtf8Bytes(rec, WalJsonContext.Default.WalRecord);
                        uint crc = Crc32C.Compute(json);
                        WriteHexUtf8(crc, crcBytes);
                        fs.Write(json, 0, json.Length);
                        fs.WriteByte(CrcSeparator);
                        fs.Write(crcBytes);
                        fs.WriteByte((byte)'\n');
                        newSize += json.Length + 1 + 8 + 1;
                    }
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, _path, overwrite: true);
                FsyncDirectory(_dataDir);
                Interlocked.Exchange(ref _currentSize, newSize);
                // The kept tail records were already durable before
                // this call (they were fsynced on Append), so the
                // post-rewrite durable seq is the highest seq in the
                // surviving tail — which is also _pendingSeq's value
                // (or higher) for any caller still parked.
                AdvanceDurableSeqOnTruncate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: failed to prefix-truncate WAL at {Path} (throughSeq={ThroughSeq}, kept={Kept})",
                    _channelNumber, _path, throughSeq, kept.Count);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                throw;
            }
        }
    }

    public void Reset()
    {
        lock (_writeLock)
        {
            if (_appendStream is not null)
            {
                _appendStream.Dispose();
                _appendStream = null;
            }
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                var tmp = System.IO.Path.Combine(_dataDir,
                    string.Format(CultureInfo.InvariantCulture, WalTempFileNameFormat, _channelNumber));
                if (File.Exists(tmp)) File.Delete(tmp);
                FsyncDirectory(_dataDir);
                Interlocked.Exchange(ref _currentSize, 0);
                _logger.LogInformation(
                    "channel {ChannelNumber}: admin Reset removed WAL at {Path}",
                    _channelNumber, _path);
                AdvanceDurableSeqOnTruncate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: failed to reset WAL at {Path}",
                    _channelNumber, _path);
                throw;
            }
        }
    }

    public void Dispose()
    {
        // Issue #312: stop the bg fsync thread before tearing down the
        // append stream so its final flush sees a live FileStream.
        // Order matters: cancel + signal + join, then dispose stream.
        if (_groupCommit && _stopFsyncThread is not null && _fsyncThread is not null)
        {
            try { _stopFsyncThread.Cancel(); } catch { /* ignore */ }
            _fsyncSignal.Set();
            try { _fsyncThread.Join(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            _stopFsyncThread.Dispose();
        }
        _fsyncSignal.Dispose();
        lock (_writeLock)
        {
            _appendStream?.Dispose();
            _appendStream = null;
        }
        // Anyone still parked in WaitForDurable on a disposed log
        // should bail; pulse the monitor so the loop wakes and re-checks
        // (cancellation token is the contractual exit, but disposing
        // the WAL while someone waits is undefined and we'd rather
        // unblock than deadlock).
        lock (_durabilityLock)
        {
            Monitor.PulseAll(_durabilityLock);
        }
    }

    private void AdvanceDurableSeqOnTruncate()
    {
        long pending = Interlocked.Read(ref _pendingSeq);
        long durable = Interlocked.Read(ref _durableSeq);
        if (pending > durable)
        {
            Interlocked.Exchange(ref _durableSeq, pending);
            lock (_durabilityLock) Monitor.PulseAll(_durabilityLock);
        }
    }

    private static void FsyncDirectory(string dir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            using var dirHandle = File.OpenHandle(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
            RandomAccess.FlushToDisk(dirHandle);
        }
        catch
        {
            // Best-effort.
        }
    }
}
