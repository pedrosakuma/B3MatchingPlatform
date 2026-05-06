using System.Text.Json;
using System.Text.Json.Serialization;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// File-system backed implementation of
/// <see cref="IChannelStatePersister"/> (issue #260). One snapshot file per
/// channel under <see cref="DataDirectory"/>:
/// <c>channel-{N}.snapshot</c>, written atomically via
/// tmp + fsync + rename so a crash mid-write can never leave a partial file.
///
/// <para>Serialization uses System.Text.Json with enums-as-strings for
/// debuggability — operators can <c>cat</c> a snapshot and read it. The
/// extra cost (vs binary) is bounded: typical snapshots are a few KB to a
/// few MB and serialization happens once per command flush, off the
/// hot UMDF send path.</para>
///
/// <para><b>Atomicity:</b> on POSIX <see cref="File.Move(string, string, bool)"/>
/// uses <c>rename(2)</c>, which is atomic on the same filesystem. We
/// fsync the tmp file's data before rename and fsync the parent directory
/// after rename so an abrupt power-loss is recoverable.</para>
/// </summary>
public sealed class FileChannelStatePersister : IChannelStatePersister
{
    private const string SnapshotFileNameFormat = "channel-{0}.snapshot";
    private const string TempFileNameFormat = "channel-{0}.snapshot.tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Easier to diff/read in incident review. The marginal size cost
        // is negligible for files of this scale.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private readonly string _dataDir;
    private readonly ILogger<FileChannelStatePersister> _logger;

    public string DataDirectory => _dataDir;

    public FileChannelStatePersister(string dataDirectory, ILogger<FileChannelStatePersister> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _dataDir = Path.GetFullPath(dataDirectory);
        _logger = logger;
        Directory.CreateDirectory(_dataDir);
    }

    public ChannelStateSnapshot? TryLoad(byte channelNumber)
    {
        var path = SnapshotPath(channelNumber);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var snapshot = JsonSerializer.Deserialize<ChannelStateSnapshot>(fs, JsonOptions);
            if (snapshot is null)
            {
                _logger.LogWarning("channel {ChannelNumber}: snapshot deserialized to null at {Path}", channelNumber, path);
                return null;
            }
            _logger.LogInformation("channel {ChannelNumber}: loaded snapshot from {Path} (seq={SequenceNumber}/{SequenceVersion}, owners={OwnerCount})",
                channelNumber, path, snapshot.SequenceNumber, snapshot.SequenceVersion, snapshot.Owners.Count);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber}: failed to load snapshot at {Path}; ignoring", channelNumber, path);
            return null;
        }
    }

    public void Save(ChannelStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = SnapshotPath(snapshot.ChannelNumber);
        var tmp = TempPath(snapshot.ChannelNumber);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, snapshot, JsonOptions);
                fs.Flush(flushToDisk: true);
            }
            // File.Move uses rename(2) on POSIX → atomic within the same
            // filesystem. overwrite=true so the previous snapshot is
            // replaced in place.
            File.Move(tmp, path, overwrite: true);
            FsyncDirectory(_dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber}: failed to persist snapshot to {Path}", snapshot.ChannelNumber, path);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    private string SnapshotPath(byte channelNumber)
        => Path.Combine(_dataDir, string.Format(System.Globalization.CultureInfo.InvariantCulture, SnapshotFileNameFormat, channelNumber));

    private string TempPath(byte channelNumber)
        => Path.Combine(_dataDir, string.Format(System.Globalization.CultureInfo.InvariantCulture, TempFileNameFormat, channelNumber));

    /// <summary>
    /// Ensures the parent directory entry for the renamed file is durable.
    /// On Linux the BCL exposes no portable directory-fsync; we fall back
    /// to opening the directory with <see cref="FileOptions.WriteThrough"/>
    /// semantics where supported. Failures are non-fatal — they only widen
    /// the crash-loss window for the very last rename.
    /// </summary>
    private static void FsyncDirectory(string dir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            // Open with O_DIRECTORY|O_RDONLY equivalent and call fsync.
            // SafeFileHandle path keeps us off P/Invoke for portability.
            using var dirHandle = File.OpenHandle(dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
            RandomAccess.FlushToDisk(dirHandle);
        }
        catch
        {
            // Best-effort. On filesystems that reject fsync(directory) this
            // throws EINVAL; the snapshot file itself is already durable.
        }
    }
}
