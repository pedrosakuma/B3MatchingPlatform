using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// File-system backed implementation of
/// <see cref="IChannelStatePersister"/> (issue #260) with N rolling
/// generations (issue #264). Snapshots are written to round-robin slots
/// <c>channel-{N}.snapshot.{slot}</c> where <c>slot</c> is in
/// <c>0..Generations-1</c>; the persister picks the newest valid slot
/// on load and writes to <c>(lastUsed + 1) mod Generations</c> on save.
///
/// <para>Load semantics: enumerate all per-channel files, sort by
/// modification time descending, return the first that deserializes.
/// A corrupted newest file therefore falls back transparently to the
/// previous generation. <c>ValidateSnapshotStructure</c> failures still
/// fail-closed in the dispatcher — generations defend against I/O-level
/// corruption (truncated writes, bad JSON), not against semantically
/// inconsistent snapshots.</para>
///
/// <para>Atomicity per slot remains the PR #261 contract: write to
/// <c>.tmp</c>, fsync the data, rename, fsync the directory entry. The
/// <c>rename(2)</c> is atomic on POSIX within the same filesystem.</para>
///
/// <para>Backward compatibility: a pre-#264 single file
/// <c>channel-{N}.snapshot</c> (no slot suffix) is still considered on
/// load. After the first successful generational write the legacy file
/// is removed so subsequent loads pick from the rolling slots only.</para>
/// </summary>
public sealed class FileChannelStatePersister : IChannelStatePersister
{
    public const int DefaultGenerations = 3;

    private const string LegacyFileNameFormat = "channel-{0}.snapshot";
    private const string SlotFileNameFormat = "channel-{0}.snapshot.{1}";
    private const string TempFileNameFormat = "channel-{0}.snapshot.{1}.tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private readonly string _dataDir;
    private readonly int _generations;
    private readonly ILogger<FileChannelStatePersister> _logger;

    // Per-channel last-used slot, -1 = unknown (derive from filesystem).
    // Mutated only inside Save under the per-channel lock.
    private readonly Dictionary<byte, int> _lastUsedSlot = new();
    private readonly object _slotLock = new();

    public string DataDirectory => _dataDir;
    public int Generations => _generations;

    public FileChannelStatePersister(
        string dataDirectory,
        ILogger<FileChannelStatePersister> logger,
        int generations = DefaultGenerations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        if (generations < 1)
            throw new ArgumentOutOfRangeException(nameof(generations),
                "generations must be >= 1");
        _dataDir = Path.GetFullPath(dataDirectory);
        _logger = logger;
        _generations = generations;
        Directory.CreateDirectory(_dataDir);
    }

    public ChannelStateSnapshot? TryLoad(byte channelNumber)
    {
        // Enumerate every generation slot + legacy file for this channel,
        // newest mtime first, returning the first that deserializes
        // successfully. This makes a corrupted newest slot transparently
        // fall back to the previous generation.
        var candidates = EnumerateCandidateFiles(channelNumber);
        if (candidates.Count == 0) return null;

        foreach (var (path, _) in candidates)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var snapshot = JsonSerializer.Deserialize<ChannelStateSnapshot>(fs, JsonOptions);
                if (snapshot is null)
                {
                    _logger.LogWarning(
                        "channel {ChannelNumber}: snapshot deserialized to null at {Path}; trying older generation",
                        channelNumber, path);
                    continue;
                }
                _logger.LogInformation(
                    "channel {ChannelNumber}: loaded snapshot from {Path} (seq={SequenceNumber}/{SequenceVersion}, owners={OwnerCount})",
                    channelNumber, path, snapshot.SequenceNumber, snapshot.SequenceVersion, snapshot.Owners.Count);
                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "channel {ChannelNumber}: failed to load snapshot at {Path}; trying older generation",
                    channelNumber, path);
            }
        }
        _logger.LogError(
            "channel {ChannelNumber}: all {Count} candidate snapshot files failed to load",
            channelNumber, candidates.Count);
        return null;
    }

    public long Save(ChannelStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int slot = NextSlot(snapshot.ChannelNumber);
        var path = SlotPath(snapshot.ChannelNumber, slot);
        var tmp = TempPath(snapshot.ChannelNumber, slot);
        long bytesWritten;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, snapshot, JsonOptions);
                fs.Flush(flushToDisk: true);
                bytesWritten = fs.Length;
            }
            File.Move(tmp, path, overwrite: true);
            FsyncDirectory(_dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "channel {ChannelNumber}: failed to persist snapshot slot {Slot} to {Path}",
                snapshot.ChannelNumber, slot, path);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
        // Best-effort: drop the legacy single-file once a generational
        // write succeeded, so future loads stop considering it.
        try
        {
            var legacy = LegacyPath(snapshot.ChannelNumber);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "channel {ChannelNumber}: failed to remove legacy snapshot file (non-fatal)",
                snapshot.ChannelNumber);
        }
        return bytesWritten;
    }

    /// <summary>
    /// Picks the next round-robin slot for the channel, lazily deriving
    /// the starting point from the on-disk newest file the first time
    /// a channel is observed (so a host restart does not reuse the slot
    /// that already contains the most recent snapshot).
    /// </summary>
    private int NextSlot(byte channelNumber)
    {
        lock (_slotLock)
        {
            if (!_lastUsedSlot.TryGetValue(channelNumber, out var last))
            {
                last = DiscoverNewestSlot(channelNumber);
                _lastUsedSlot[channelNumber] = last;
            }
            int next = (last + 1) % _generations;
            _lastUsedSlot[channelNumber] = next;
            return next;
        }
    }

    private int DiscoverNewestSlot(byte channelNumber)
    {
        int newest = -1;
        DateTime newestMtime = DateTime.MinValue;
        for (int i = 0; i < _generations; i++)
        {
            var p = SlotPath(channelNumber, i);
            if (!File.Exists(p)) continue;
            var mt = File.GetLastWriteTimeUtc(p);
            if (mt > newestMtime)
            {
                newestMtime = mt;
                newest = i;
            }
        }
        return newest;
    }

    /// <summary>
    /// Returns existing snapshot file paths for the channel (slot files
    /// + legacy single-file if present), sorted by mtime descending.
    /// </summary>
    private List<(string Path, DateTime Mtime)> EnumerateCandidateFiles(byte channelNumber)
    {
        var list = new List<(string, DateTime)>(_generations + 1);
        for (int i = 0; i < _generations; i++)
        {
            var p = SlotPath(channelNumber, i);
            if (File.Exists(p)) list.Add((p, File.GetLastWriteTimeUtc(p)));
        }
        var legacy = LegacyPath(channelNumber);
        if (File.Exists(legacy)) list.Add((legacy, File.GetLastWriteTimeUtc(legacy)));
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return list;
    }

    private string LegacyPath(byte channelNumber)
        => Path.Combine(_dataDir,
            string.Format(CultureInfo.InvariantCulture, LegacyFileNameFormat, channelNumber));

    private string SlotPath(byte channelNumber, int slot)
        => Path.Combine(_dataDir,
            string.Format(CultureInfo.InvariantCulture, SlotFileNameFormat, channelNumber, slot));

    private string TempPath(byte channelNumber, int slot)
        => Path.Combine(_dataDir,
            string.Format(CultureInfo.InvariantCulture, TempFileNameFormat, channelNumber, slot));

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
            // Best-effort; some FSes reject directory fsync with EINVAL.
        }
    }
}
