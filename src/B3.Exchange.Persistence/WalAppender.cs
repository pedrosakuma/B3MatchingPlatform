using System.Globalization;
using System.Text.Json;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// Owns the WAL file's append stream + write lock and implements
/// append / truncate / reset. Extracted from
/// <see cref="FileChannelWriteAheadLog"/>; threading and on-disk
/// semantics are unchanged.
/// </summary>
internal sealed class WalAppender : IDisposable
{
    private const string WalTempFileNameFormat = "channel-{0}.wal.tmp";
    private const byte CrcSeparator = (byte)'\t';

    private readonly string _path;
    private readonly string _dataDir;
    private readonly byte _channelNumber;
    private readonly bool _fsyncPerWrite;
    private readonly long _maxBytes;
    private readonly WalSizeCapPolicy _onFull;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private FileStream? _appendStream;
    private long _currentSize;
    private long _dropsOnFull;
    private WalDurabilityCoordinator? _durability;

    public WalAppender(
        string path,
        string dataDir,
        byte channelNumber,
        bool fsyncPerWrite,
        long maxBytes,
        WalSizeCapPolicy onFull,
        long initialSize,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentNullException.ThrowIfNull(logger);
        _path = path;
        _dataDir = dataDir;
        _channelNumber = channelNumber;
        _fsyncPerWrite = fsyncPerWrite;
        _maxBytes = maxBytes > 0 ? maxBytes : 0;
        _onFull = onFull;
        _logger = logger;
        _currentSize = initialSize > 0 ? initialSize : 0;
    }

    public string Path => _path;
    public long CurrentSize => Interlocked.Read(ref _currentSize);
    public long DropsOnFull => Interlocked.Read(ref _dropsOnFull);

    public void AttachDurability(WalDurabilityCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _durability = coordinator;
    }

    public int Append(WalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_writeLock)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(record, WalJsonContext.Default.WalRecord);
            int recordBytes = json.Length + 1 /* \t */ + 8 /* hex crc */ + 1 /* \n */;
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
            if (_fsyncPerWrite)
            {
                _appendStream.Flush(flushToDisk: true);
                _durability!.RecordPendingAndDurable(record.Seq);
            }
            else
            {
                _appendStream.Flush();
                _durability!.RecordPending(record.Seq);
            }
            Interlocked.Add(ref _currentSize, recordBytes);
            return recordBytes;
        }
    }

    public long FsyncToDisk()
    {
        lock (_writeLock)
        {
            if (_appendStream is null) return 0;
            long pending = _durability!.PendingSeq;
            if (pending <= _durability.DurableSeq) return 0;
            try
            {
                _appendStream.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "channel {ChannelNumber}: WAL background fsync failed; durable seq stays at {Durable}",
                    _channelNumber, _durability.DurableSeq);
                return 0;
            }
            return pending;
        }
    }

    public void CloseAppendStream()
    {
        lock (_writeLock)
        {
            if (_appendStream is not null)
            {
                _appendStream.Flush(flushToDisk: true);
                _appendStream.Dispose();
                _appendStream = null;
            }
        }
    }

    public void Truncate()
    {
        lock (_writeLock)
        {
            TruncateLocked();
        }
    }

    public void TruncateThrough(long throughSeq)
    {
        lock (_writeLock)
        {
            // Close the append stream BEFORE reading so reads see all
            // flushed bytes and the rename below can succeed on Windows.
            if (_appendStream is not null)
            {
                _appendStream.Flush(flushToDisk: true);
                _appendStream.Dispose();
                _appendStream = null;
            }
            var replay = WalReplay.ReadAll(_path, _channelNumber, _logger);
            var all = replay.Records;
            var kept = new List<WalRecord>(all.Count);
            foreach (var rec in all)
            {
                if (rec.Seq > throughSeq) kept.Add(rec);
            }
            if (kept.Count == all.Count)
            {
                return;
            }
            if (kept.Count == 0)
            {
                TruncateLocked();
                return;
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
                _durability!.AdvanceDurableSeqOnTruncate();
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
                _durability!.AdvanceDurableSeqOnTruncate();
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
        lock (_writeLock)
        {
            _appendStream?.Dispose();
            _appendStream = null;
        }
    }

    private void TruncateLocked()
    {
        if (_appendStream is not null)
        {
            _appendStream.Dispose();
            _appendStream = null;
        }
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
            _durability!.AdvanceDurableSeqOnTruncate();
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

    private static void WriteHexUtf8(uint value, Span<byte> dst)
    {
        const string Hex = "0123456789ABCDEF";
        for (int i = 7; i >= 0; i--)
        {
            dst[i] = (byte)Hex[(int)(value & 0xF)];
            value >>= 4;
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
