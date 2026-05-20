using System.Globalization;
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
///
/// <para>Issue #389: implementation is a thin facade composing three
/// internal collaborators: <see cref="WalReplay"/> (read/parse/CRC),
/// <see cref="WalDurabilityCoordinator"/> (pending/durable seq +
/// group-commit bg thread), and <see cref="WalAppender"/> (file
/// stream ownership, append, truncate, reset). Public API,
/// on-disk format and threading semantics are unchanged.</para>
/// </summary>
public sealed class FileChannelWriteAheadLog : IChannelWriteAheadLog, IDisposable
{
    private const string WalFileNameFormat = "channel-{0}.wal";

    private readonly ILogger<FileChannelWriteAheadLog> _logger;
    private readonly byte _channelNumber;
    private readonly WalAppender _appender;
    private readonly WalDurabilityCoordinator _durability;
    private int _lastReadCorruptCount;
    private int _lastReadLegacyCount;

    /// <summary>
    /// Number of records the most recent <see cref="ReadAll"/> call
    /// rejected because their stored Crc32C did not match the JSON
    /// payload (bit-rot). The dispatcher reads this after replay and
    /// bumps <c>exch_wal_record_corruption_total</c>.
    /// </summary>
    public int LastReadCorruptCount => _lastReadCorruptCount;

    /// <summary>
    /// Number of records the most recent <see cref="ReadAll"/> call
    /// accepted that had no Crc32C suffix (i.e. were written by a
    /// pre-#285 host). Useful for tracking the migration window
    /// after upgrading.
    /// </summary>
    public int LastReadLegacyCount => _lastReadLegacyCount;

    /// <summary>Issue #291: current on-disk size in bytes; surfaces
    /// <c>exch_wal_size_bytes</c> via <see cref="CurrentSizeBytes"/>.</summary>
    public long CurrentSizeBytes => _appender.CurrentSize;

    /// <summary>Issue #291: cumulative count of appends silently
    /// skipped under <see cref="WalSizeCapPolicy.Drop"/>.</summary>
    public long DropsOnFullCount => _appender.DropsOnFull;

    /// <summary>Issue #312: see
    /// <see cref="IChannelWriteAheadLog.PendingDurableSeqOrZero"/>.</summary>
    public long PendingDurableSeqOrZero => _durability.PendingSeq;

    /// <summary>Issue #312: see
    /// <see cref="IChannelWriteAheadLog.DurableSeqOrZero"/>.</summary>
    public long DurableSeqOrZero => _durability.DurableSeq;

    public string Path => _appender.Path;

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
        var dataDir = System.IO.Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(dataDir);
        _channelNumber = channelNumber;
        _logger = logger;
        var path = System.IO.Path.Combine(dataDir,
            string.Format(CultureInfo.InvariantCulture, WalFileNameFormat, channelNumber));
        long initialSize = 0;
        if (File.Exists(path))
        {
            try { initialSize = new FileInfo(path).Length; }
            catch { initialSize = 0; }
        }

        var resolvedMaxBytes = maxBytes > 0 ? maxBytes : 0;
        _appender = new WalAppender(
            path, dataDir, channelNumber, fsyncPerWrite,
            resolvedMaxBytes, onFull, initialSize, logger);
        bool groupCommit = fsyncMode == WalFsyncMode.GroupCommit;
        _durability = new WalDurabilityCoordinator(
            channelNumber, groupCommit, groupCommitInterval,
            _appender.FsyncToDisk, logger);
        _appender.AttachDurability(_durability);
    }

    public int Append(WalRecord record) => _appender.Append(record);

    public void WaitForDurable(long seq, CancellationToken cancellationToken = default)
        => _durability.WaitForDurable(seq, cancellationToken);

    public IReadOnlyList<WalRecord> ReadAll()
    {
        _appender.CloseAppendStream();
        var counters = new WalReplayCounters();
        try
        {
            var result = WalReplay.ReadAll(_appender.Path, _channelNumber, _logger, counters);
            _lastReadCorruptCount = result.CorruptCount;
            _lastReadLegacyCount = result.LegacyCount;
            return result.Records;
        }
        catch
        {
            _lastReadCorruptCount = counters.CorruptCount;
            _lastReadLegacyCount = counters.LegacyCount;
            throw;
        }
    }

    public void Truncate() => _appender.Truncate();

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
    /// </summary>
    public void TruncateThrough(long throughSeq) => _appender.TruncateThrough(throughSeq);

    public void Reset() => _appender.Reset();

    public void Dispose()
    {
        _durability.Dispose();
        _appender.Dispose();
    }
}
