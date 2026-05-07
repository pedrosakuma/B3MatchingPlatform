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
/// two distinct failure modes: a torn final write fails the trailing
/// CRC and is dropped, while a bit-rot landing mid-file fails just
/// that record's CRC — replay logs the corruption and **continues
/// past it** so the surviving prefix and suffix remain replayable.
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
    private readonly ILogger<FileChannelWriteAheadLog> _logger;
    private readonly byte _channelNumber;
    private readonly object _writeLock = new();
    private FileStream? _appendStream;

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
            uint crc = Crc32C.Compute(json);
            // Layout: <json bytes> \t <8-hex Crc32C> \n. Tab is safe
            // because JsonSerializer escapes literal tabs to \t inside
            // strings, so the last \t in the line is always our
            // separator.
            Span<byte> crcBytes = stackalloc byte[8];
            WriteHexUtf8(crc, crcBytes);
            _appendStream.Write(json, 0, json.Length);
            _appendStream.WriteByte(CrcSeparator);
            _appendStream.Write(crcBytes);
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
                int tab = line.LastIndexOf('\t');
                if (tab >= 0 && line.Length - tab - 1 == 8)
                {
                    // Modern format: JSON \t crc8 .
                    var json = line.AsSpan(0, tab);
                    var crcHex = line.AsSpan(tab + 1, 8);
                    if (!TryParseHex32(crcHex, out uint storedCrc))
                    {
                        // Suffix isn't a valid 8-hex CRC — treat as
                        // legacy (no CRC) and fall through.
                        ConsumeLegacyLine(line, lineNumber, result);
                        continue;
                    }
                    var jsonBytes = Encoding.UTF8.GetBytes(line[..tab]);
                    uint computedCrc = Crc32C.Compute(jsonBytes);
                    if (computedCrc != storedCrc)
                    {
                        LastReadCorruptCount++;
                        _logger.LogWarning(
                            "channel {ChannelNumber}: WAL line {LineNumber} CRC mismatch (stored=0x{StoredCrc:X8} computed=0x{ComputedCrc:X8}); skipping record and continuing replay",
                            _channelNumber, lineNumber, storedCrc, computedCrc);
                        continue;
                    }
                    try
                    {
                        var rec = JsonSerializer.Deserialize<WalRecord>(jsonBytes, JsonOptions);
                        if (rec is not null) result.Add(rec);
                    }
                    catch (Exception ex)
                    {
                        // CRC matched but JSON failed — bizarre, treat
                        // as corrupt for metric purposes and continue.
                        LastReadCorruptCount++;
                        _logger.LogWarning(ex,
                            "channel {ChannelNumber}: WAL line {LineNumber} JSON parse failed despite CRC match; skipping",
                            _channelNumber, lineNumber);
                    }
                }
                else
                {
                    if (!ConsumeLegacyLine(line, lineNumber, result)) break;
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

    /// <summary>
    /// Handles a line written by a pre-#285 host (no <c>\t</c>+CRC
    /// suffix). Returns <c>false</c> to signal the caller to stop
    /// further line processing — preserving the original "torn-final
    /// stops replay" behavior for legacy files.
    /// </summary>
    private bool ConsumeLegacyLine(string line, int lineNumber, List<WalRecord> result)
    {
        try
        {
            var rec = JsonSerializer.Deserialize<WalRecord>(line, JsonOptions);
            if (rec is not null)
            {
                result.Add(rec);
                LastReadLegacyCount++;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "channel {ChannelNumber}: legacy WAL line {LineNumber} failed to parse; truncating replay set here",
                _channelNumber, lineNumber);
            return false;
        }
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
