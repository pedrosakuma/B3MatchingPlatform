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
/// Threading model: not thread-safe. Designed to be installed as the
/// per-channel <see cref="IPostTradeSink"/> and invoked exclusively on
/// the <c>ChannelDispatcher</c>'s single dispatch thread.
///
/// Durability: writes are flushed to the OS buffer on every record but
/// NOT fsynced. <see cref="Checkpoint"/> calls <c>FileStream.Flush(true)</c>
/// — wire it from PR-4's interval-bounded watermark loop. PR-2 leaves the
/// dispatcher's hot path free of fsync to avoid a regression before the
/// durability infrastructure exists.
/// </summary>
public sealed class FileAuditLogWriter : IPostTradeSink, IDisposable
{
    private readonly string _rootDir;
    private readonly byte _channelNumber;
    private readonly byte[] _scratch = new byte[Math.Max(AuditRecordCodec.RecordSize, AuditRecordCodec.FileHeaderSize)];

    private FileStream? _stream;
    private DateOnly _currentDate;
    private long _recordsWritten;

    public FileAuditLogWriter(string rootDir, byte channelNumber)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        _rootDir = rootDir;
        _channelNumber = channelNumber;
    }

    /// <summary>Total records successfully appended since construction. Useful
    /// for tests and for PR-4's durability watermark accounting.</summary>
    public long RecordsWritten => _recordsWritten;

    /// <summary>UTC date of the currently-open log file, or <c>null</c> when
    /// no record has been written yet.</summary>
    public DateOnly? CurrentTradeDate => _stream is null ? null : _currentDate;

    public void OnTrade(in PostTradeRecord record)
    {
        var recordDate = ToUtcDate(record.TransactTimeNanos);
        if (_stream is null || recordDate != _currentDate)
        {
            RotateTo(recordDate);
        }
        int n = AuditRecordCodec.Encode(_scratch, in record);
        _stream!.Write(_scratch, 0, n);
        // Flush the managed FileStream buffer to the OS on every record so
        // the documented contract holds: post-trade records are at minimum
        // OS-buffer-visible immediately, with fsync deferred to rotation /
        // Checkpoint() / PR-4's durability loop.
        _stream.Flush(flushToDisk: false);
        _recordsWritten++;
    }

    /// <summary>Flushes OS buffers and forces <c>fsync</c> on the current
    /// file (if any). PR-4 will call this from the durability-watermark
    /// loop and from the operator-triggered checkpoint endpoint.</summary>
    public void Checkpoint()
    {
        _stream?.Flush(flushToDisk: true);
    }

    private void RotateTo(DateOnly newDate)
    {
        if (_stream is not null)
        {
            // Force an fsync on the closing file so the day's records are
            // durable before subsequent records land in a new file — without
            // this, a crash mid-rotation could leave both files with
            // unflushed tails.
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
        }
        var channelDir = Path.Combine(_rootDir, _channelNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(channelDir);
        var path = Path.Combine(channelDir, $"fills-{newDate:yyyy-MM-dd}.log");
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length == 0)
        {
            AuditRecordCodec.WriteFileHeader(_scratch, _channelNumber, newDate);
            fs.Write(_scratch, 0, AuditRecordCodec.FileHeaderSize);
        }
        else
        {
            // Recovery on append: validate the header, then scan forward to
            // the last good record offset and truncate any torn tail before
            // appending. Without this step, records appended after a torn
            // tail would be permanently invisible to AuditLogReader, which
            // (correctly) treats the first failed TryDecode as end-of-log.
            long goodEnd = ScanLastGoodEndOffset(fs);
            if (goodEnd != fs.Length)
                fs.SetLength(goodEnd);
        }
        fs.Seek(0, SeekOrigin.End);
        _stream = fs;
        _currentDate = newDate;
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
        if (_stream is not null)
        {
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
        }
    }
}
