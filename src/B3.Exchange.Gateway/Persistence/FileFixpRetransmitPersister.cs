using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// File-backed implementation of <see cref="IFixpRetransmitPersister"/>.
/// One file per session under
/// <c>{dataDir}/sessions/session-{sessionId:x8}.ring</c>.
///
/// <para><b>Record layout</b>: each appended frame is written as
/// <c>[length:u4 LE | seq:u4 LE | frame_bytes:length | crc32c:u4 LE]</c>.
/// The Crc32C covers <c>length || seq || frame_bytes</c>, so a torn
/// tail (partial record at EOF) is detected on read by either an
/// incomplete record or a Crc32C mismatch and silently dropped.
/// </para>
///
/// <para><b>fsync policy</b>: today every <see cref="Append"/>
/// flushes via <c>FileStream.Flush(flushToDisk: true)</c>. That
/// matches the per-record durability the in-memory ring promises to
/// the FIXP peer, at the cost of one fsync per business frame —
/// acceptable for the simulator's target throughput. A windowed
/// fsync (e.g. flush every N records or every K ms) would be a
/// straightforward optimisation behind the same interface.</para>
///
/// <para><b>Compaction</b> writes to <c>session-{id:x8}.ring.tmp</c>
/// and atomically renames over the live file via
/// <c>File.Move(overwrite: true)</c>. A crash between writing the
/// tmp and renaming leaves the previous live file intact; a crash
/// during the rename leaves either the old or new file (atomic on
/// every supported FS).</para>
/// </summary>
public sealed class FileFixpRetransmitPersister : IFixpRetransmitPersister
{
    /// <summary>Subdirectory under the configured data directory.</summary>
    public const string SessionsSubdir = "sessions";

    private readonly string _sessionsDir;
    private readonly ILogger<FileFixpRetransmitPersister> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<uint, FileStream> _open = new();
    private bool _disposed;

    public FileFixpRetransmitPersister(string dataDir,
        ILogger<FileFixpRetransmitPersister> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDir);
        ArgumentNullException.ThrowIfNull(logger);
        _sessionsDir = Path.Combine(dataDir, SessionsSubdir);
        _logger = logger;
        Directory.CreateDirectory(_sessionsDir);
    }

    private string PathFor(uint sessionId)
        => Path.Combine(_sessionsDir, $"session-{sessionId:x8}.ring");

    public void Append(uint sessionId, uint seq, ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
            throw new ArgumentException("frame must not be empty", nameof(frame));
        FileStream fs;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_open.TryGetValue(sessionId, out fs!))
            {
                fs = new FileStream(PathFor(sessionId), FileMode.Append, FileAccess.Write,
                    FileShare.Read, bufferSize: 4096, useAsync: false);
                _open[sessionId] = fs;
            }
        }
        // Build [length | seq | frame | crc32c] in a stack buffer +
        // direct write of the frame body to avoid a large allocation.
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], seq);
        // Crc32C(length || seq || frame).
        uint crc = ComputeCrc(header, frame);
        Span<byte> footer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, crc);
        lock (fs)
        {
            fs.Write(header);
            fs.Write(frame);
            fs.Write(footer);
            fs.Flush(flushToDisk: true);
        }
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> frame)
    {
        // We need a single Crc32C over header || frame. Crc32C exposes
        // only a single-buffer API; build a small pooled buffer to
        // concatenate. Frames are typically a few hundred bytes —
        // well under any sensible stack budget but we still rent to
        // avoid a per-append GC.
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

    public void Compact(uint sessionId, IReadOnlyList<RetransmitRingEntry> liveFrames)
    {
        ArgumentNullException.ThrowIfNull(liveFrames);
        var live = PathFor(sessionId);
        var tmp = live + ".tmp";
        // Close any open append handle so the rename can proceed
        // (Linux is permissive but macOS/Windows are not).
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_open.Remove(sessionId, out var existing))
            {
                try { existing.Dispose(); } catch { }
            }
        }
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 4096, useAsync: false))
        {
            Span<byte> header = stackalloc byte[8];
            Span<byte> footer = stackalloc byte[4];
            foreach (var entry in liveFrames)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)entry.Frame.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], entry.Seq);
                uint crc = ComputeCrc(header, entry.Frame);
                BinaryPrimitives.WriteUInt32LittleEndian(footer, crc);
                fs.Write(header);
                fs.Write(entry.Frame);
                fs.Write(footer);
            }
            fs.Flush(flushToDisk: true);
        }
        // Atomic rename over the live file.
        File.Move(tmp, live, overwrite: true);
    }

    public void Remove(uint sessionId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_open.Remove(sessionId, out var existing))
            {
                try { existing.Dispose(); } catch { }
            }
        }
        var p = PathFor(sessionId);
        try
        {
            if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp retransmit persister: failed to remove session file {Path}; will retry on next Remove",
                p);
        }
    }

    public IReadOnlyDictionary<uint, IReadOnlyList<RetransmitRingEntry>> LoadAll()
    {
        var result = new Dictionary<uint, IReadOnlyList<RetransmitRingEntry>>();
        if (!Directory.Exists(_sessionsDir)) return result;
        foreach (var path in Directory.EnumerateFiles(_sessionsDir, "session-*.ring"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!TryParseSessionId(name, out var sessionId))
            {
                _logger.LogWarning(
                    "fixp retransmit persister: skipping unparseable session file {Path}", path);
                continue;
            }
            var entries = LoadOne(path, sessionId);
            if (entries.Count > 0) result[sessionId] = entries;
        }
        return result;
    }

    private static bool TryParseSessionId(string fileName, out uint sessionId)
    {
        sessionId = 0;
        const string prefix = "session-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var hex = fileName.AsSpan(prefix.Length);
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out sessionId);
    }

    private List<RetransmitRingEntry> LoadOne(string path, uint sessionId)
    {
        var entries = new List<RetransmitRingEntry>();
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "fixp retransmit persister: failed to read session file {Path}; treating as empty (session={SessionId:x8})",
                path, sessionId);
            return entries;
        }
        int offset = 0;
        int recordIdx = 0;
        while (offset < raw.Length)
        {
            recordIdx++;
            // Need at least 8 (header) + 0 (frame) + 4 (crc) = 12 bytes for a record;
            // a torn tail shorter than that is dropped silently.
            if (raw.Length - offset < 12)
            {
                _logger.LogWarning(
                    "fixp retransmit persister: torn-tail (got {Have} bytes, need >=12) at record {Index} of {Path}; dropping",
                    raw.Length - offset, recordIdx, path);
                break;
            }
            var header = raw.AsSpan(offset, 8);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            int needed = 8 + (int)length + 4;
            if (length > int.MaxValue - 12 || raw.Length - offset < needed)
            {
                _logger.LogWarning(
                    "fixp retransmit persister: torn-tail at record {Index} of {Path}; expected {Needed} bytes have {Have}; dropping",
                    recordIdx, path, needed, raw.Length - offset);
                break;
            }
            var frame = raw.AsSpan(offset + 8, (int)length);
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                raw.AsSpan(offset + 8 + (int)length, 4));
            uint computed = Crc32C.Compute(raw.AsSpan(offset, 8 + (int)length));
            if (computed != storedCrc)
            {
                _logger.LogWarning(
                    "fixp retransmit persister: CRC mismatch at record {Index} of {Path} (stored=0x{Stored:X8} computed=0x{Computed:X8}); dropping rest",
                    recordIdx, path, storedCrc, computed);
                break;
            }
            entries.Add(new RetransmitRingEntry(seq, frame.ToArray()));
            offset += needed;
        }
        return entries;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var fs in _open.Values)
            {
                try { fs.Dispose(); } catch { }
            }
            _open.Clear();
        }
    }
}
