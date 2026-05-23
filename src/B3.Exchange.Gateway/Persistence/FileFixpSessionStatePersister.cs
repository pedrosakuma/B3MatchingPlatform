using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// File-backed implementation of <see cref="IFixpSessionStatePersister"/>.
/// One small fixed-layout file per session at
/// <c>{dataDir}/state/session-{sessionId:x8}.state</c>.
///
/// <para><b>Atomic write</b>: <see cref="Save"/> writes the new
/// payload to a <c>.tmp</c> sibling, fsyncs it, then
/// <c>File.Move(..., overwrite: true)</c> on top of the live file.
/// A crash mid-write leaves either the pre-existing file untouched
/// or the new one fully written — never a torn record. The tmp file
/// from a crashed previous run is reclaimed by <see cref="LoadAll"/>
/// (best-effort: it is deleted on sight).</para>
///
/// <para><b>Record layout</b>:
/// <c>[magic:u4 LE | version:u2 LE | reserved:u2 | sessionId:u4 LE | sessionVerId:u8 LE
///  | outboundMsgSeqNum:u4 LE | lastIncomingSeqNo:u4 LE | enteringFirm:u4 LE
///  | updatedAtNanos:i8 LE | crc32c:u4 LE]</c> — 44 bytes total. A CRC mismatch
/// at load time logs at Critical and the file is skipped (treated
/// as "no snapshot for this session", forcing the peer through a
/// fresh Negotiate just as if the snapshot had never been written).</para>
/// </summary>
public sealed class FileFixpSessionStatePersister : IFixpSessionStatePersister
{
    /// <summary>Subdirectory under the configured data directory.</summary>
    public const string StateSubdir = "state";

    private const uint Magic = 0x53455355u; // 'USES' in little-endian: u-s-e-s
    private const ushort Version = 1;
    private const int RecordBytes = 44;

    private readonly string _stateDir;
    private readonly ILogger<FileFixpSessionStatePersister> _logger;
    private readonly object _lock = new();
    private bool _disposed;

    public FileFixpSessionStatePersister(string dataDir,
        ILogger<FileFixpSessionStatePersister> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDir);
        ArgumentNullException.ThrowIfNull(logger);
        _stateDir = Path.Combine(dataDir, StateSubdir);
        _logger = logger;
        Directory.CreateDirectory(_stateDir);
        ReapStaleTempFiles();
    }

    private void ReapStaleTempFiles()
    {
        foreach (var tmp in Directory.EnumerateFiles(_stateDir, "*.tmp"))
        {
            try
            {
                File.Delete(tmp);
                _logger.LogWarning(
                    "fixp session state: removed stale tmp file {Path} from a previous crash",
                    tmp);
            }
            catch { /* best-effort */ }
        }
    }

    private string PathFor(uint sessionId)
        => Path.Combine(_stateDir, $"session-{sessionId:x8}.state");

    public void Save(in FixpSessionStateSnapshot snapshot)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Span<byte> buf = stackalloc byte[RecordBytes];
            EncodeRecord(buf, in snapshot);
            var finalPath = PathFor(snapshot.SessionId);
            var tmpPath = finalPath + ".tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: false))
            {
                fs.Write(buf);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, finalPath, overwrite: true);
        }
    }

    private static void EncodeRecord(Span<byte> buf, in FixpSessionStateSnapshot s)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(4, 2), Version);
        buf[6] = 0; buf[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(8, 4), s.SessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(12, 8), s.SessionVerId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(20, 4), s.OutboundMsgSeqNum);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(24, 4), s.LastIncomingSeqNo);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(28, 4), s.EnteringFirm);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(32, 8), s.UpdatedAtNanos);
        uint crc = Crc32C.Compute(buf[..(RecordBytes - 4)]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(RecordBytes - 4, 4), crc);
    }

    public FixpSessionStateSnapshot? Load(uint sessionId)
    {
        var path = PathFor(sessionId);
        return TryLoadFile(path);
    }

    public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
    {
        if (!Directory.Exists(_stateDir)) return Array.Empty<FixpSessionStateSnapshot>();
        var result = new List<FixpSessionStateSnapshot>();
        foreach (var path in Directory.EnumerateFiles(_stateDir, "session-*.state"))
        {
            var s = TryLoadFile(path);
            if (s.HasValue) result.Add(s.Value);
        }
        return result;
    }

    private FixpSessionStateSnapshot? TryLoadFile(string path)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "fixp session state: failed to read {Path}; ignoring",
                path);
            return null;
        }
        if (raw.Length != RecordBytes)
        {
            _logger.LogCritical(
                "fixp session state: {Path} has unexpected length {Length} (expected {Expected}); ignoring",
                path, raw.Length, RecordBytes);
            return null;
        }
        var span = raw.AsSpan();
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2));
        if (magic != Magic)
        {
            _logger.LogCritical(
                "fixp session state: {Path} magic mismatch (got 0x{Magic:X8}); ignoring",
                path, magic);
            return null;
        }
        if (version != Version)
        {
            _logger.LogCritical(
                "fixp session state: {Path} unsupported version {Version}; ignoring",
                path, version);
            return null;
        }
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            span.Slice(RecordBytes - 4, 4));
        uint computed = Crc32C.Compute(span[..(RecordBytes - 4)]);
        if (storedCrc != computed)
        {
            _logger.LogCritical(
                "fixp session state: {Path} CRC mismatch (stored=0x{Stored:X8} computed=0x{Computed:X8}); ignoring",
                path, storedCrc, computed);
            return null;
        }
        return new FixpSessionStateSnapshot(
            SessionId: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)),
            SessionVerId: BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(12, 8)),
            OutboundMsgSeqNum: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4)),
            LastIncomingSeqNo: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4)),
            EnteringFirm: BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(28, 4)),
            UpdatedAtNanos: BinaryPrimitives.ReadInt64LittleEndian(span.Slice(32, 8)));
    }

    public void Remove(uint sessionId)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = PathFor(sessionId);
            try { File.Delete(path); }
            catch (FileNotFoundException) { /* idempotent */ }
            catch (DirectoryNotFoundException) { /* idempotent */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "fixp session state: failed to delete {Path}", path);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    /// <summary>
    /// Internal helper used by tests / the future boot path:
    /// enumerate session ids from on-disk filenames without parsing
    /// the records.
    /// </summary>
    internal IReadOnlyCollection<uint> ListSessions()
    {
        if (!Directory.Exists(_stateDir)) return Array.Empty<uint>();
        var result = new List<uint>();
        const string prefix = "session-";
        const string suffix = ".state";
        foreach (var path in Directory.EnumerateFiles(_stateDir, "session-*.state"))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var hex = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
            if (uint.TryParse(hex, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var sid))
                result.Add(sid);
        }
        return result;
    }
}
