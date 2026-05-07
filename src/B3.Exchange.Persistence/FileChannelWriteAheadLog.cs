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
/// <para>Append semantics: each record is JSON-serialized, written
/// with a trailing newline, and the implementation optionally
/// <c>fsync</c>s the file before returning. The JSON-Lines format makes
/// partial / torn writes self-detecting on read — a record without a
/// terminating newline (or that fails to deserialize) is dropped,
/// halting replay at the last whole record. Combined with per-write
/// fsync this gives zero-RPO recovery; the caller can opt for batched
/// fsync (lower latency, RPO bounded by batch flush) via the
/// <c>fsyncPerWrite=false</c> ctor argument.</para>
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
    private readonly ILogger<FileChannelWriteAheadLog> _logger;
    private readonly byte _channelNumber;
    private readonly object _writeLock = new();
    private FileStream? _appendStream;

    public string Path => _path;

    public FileChannelWriteAheadLog(
        string dataDirectory,
        byte channelNumber,
        ILogger<FileChannelWriteAheadLog> logger,
        bool fsyncPerWrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _dataDir = System.IO.Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(_dataDir);
        _channelNumber = channelNumber;
        _fsyncPerWrite = fsyncPerWrite;
        _logger = logger;
        _path = System.IO.Path.Combine(_dataDir,
            string.Format(CultureInfo.InvariantCulture, WalFileNameFormat, channelNumber));
    }

    public void Append(WalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_writeLock)
        {
            _appendStream ??= new FileStream(_path,
                FileMode.Append, FileAccess.Write, FileShare.Read);
            var json = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            _appendStream.Write(json, 0, json.Length);
            _appendStream.WriteByte((byte)'\n');
            if (_fsyncPerWrite)
            {
                _appendStream.Flush(flushToDisk: true);
            }
            else
            {
                _appendStream.Flush();
            }
        }
    }

    public IReadOnlyList<WalRecord> ReadAll()
    {
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
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            int lineNumber = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                if (line.Length == 0) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<WalRecord>(line, JsonOptions);
                    if (rec is not null) result.Add(rec);
                }
                catch (Exception ex)
                {
                    // Torn final line or otherwise corrupt entry.
                    // JSON-Lines lets us stop here cleanly: every prior
                    // line is fully framed by its own newline so the
                    // surviving prefix is a valid replay set.
                    _logger.LogWarning(ex,
                        "channel {ChannelNumber}: WAL line {LineNumber} failed to parse; truncating replay set here",
                        _channelNumber, lineNumber);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: failed to read WAL at {Path}; treating as empty",
                _channelNumber, _path);
            return Array.Empty<WalRecord>();
        }
        return result;
    }

    public void Truncate()
    {
        lock (_writeLock)
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
                _logger.LogInformation(
                    "channel {ChannelNumber}: admin Reset removed WAL at {Path}",
                    _channelNumber, _path);
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
