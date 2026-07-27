using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// File-system backed implementation of
/// <see cref="IChannelStatePersister"/> (issue #260) with N rolling
/// generations (issue #264). Snapshots are written to round-robin slots
/// <c>channel-{N}.snapshot.{slot}</c> where <c>slot</c> is in
/// <c>0..Generations-1</c>; the persister picks the valid slot with the
/// greatest <see cref="ChannelStateSnapshot.LastAppliedSeq"/> on load and
/// writes to <c>(lastUsed + 1) mod Generations</c> on save.
///
/// <para>Load semantics: enumerate and deserialize every per-channel file,
/// then return the valid snapshot with the greatest
/// <c>LastAppliedSeq</c> (modification time breaks ties). This monotonic
/// ordering prevents a delayed asynchronous save from superseding a newer
/// synchronous durability save merely because it landed later. A corrupted
/// file falls back transparently to the best remaining generation.
/// <c>ValidateSnapshotStructure</c> failures still fail-closed in the
/// dispatcher — generations defend against I/O-level corruption (truncated
/// writes, bad JSON), not against semantically inconsistent snapshots.</para>
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
    private readonly SnapshotMigrationSet _migrations;
    private readonly SnapshotFileFormat _writeFormat;

    private sealed class ChannelPersistenceState
    {
        public object Gate { get; } = new();
        public bool Initialized { get; set; }
        public long HighestSavedSeq { get; set; }
        public int LastUsedSlot { get; set; } = -1;
        public long SaveGeneration;
    }

    private readonly ChannelPersistenceState[] _channelStates = CreateChannelStates();

    public string DataDirectory => _dataDir;
    public int Generations => _generations;
    public SnapshotMigrationSet Migrations => _migrations;
    public SnapshotFileFormat WriteFormat => _writeFormat;
    internal Action? BeforeSaveForTesting { get; set; }

    public FileChannelStatePersister(
        string dataDirectory,
        ILogger<FileChannelStatePersister> logger,
        int generations = DefaultGenerations,
        SnapshotMigrationSet? migrations = null,
        SnapshotFileFormat writeFormat = SnapshotFileFormat.Json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        if (generations < 1)
            throw new ArgumentOutOfRangeException(nameof(generations),
                "generations must be >= 1");
        _dataDir = Path.GetFullPath(dataDirectory);
        _logger = logger;
        _generations = generations;
        _migrations = migrations ?? SnapshotMigrationSet.BuildDefault();
        _writeFormat = writeFormat;
        Directory.CreateDirectory(_dataDir);
    }

    public ChannelStateSnapshot? TryLoad(byte channelNumber)
    {
        var state = _channelStates[channelNumber];
        lock (state.Gate)
        {
            return TryLoadCore(channelNumber, state, logLoaded: true);
        }
    }

    public long Save(ChannelStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = _channelStates[snapshot.ChannelNumber];
        lock (state.Gate)
        {
            if (!state.Initialized)
                _ = TryLoadCore(snapshot.ChannelNumber, state, logLoaded: false);
            return SaveLocked(snapshot, state);
        }
    }

    public long CaptureSaveGeneration(byte channelNumber)
        => Volatile.Read(ref _channelStates[channelNumber].SaveGeneration);

    public bool TrySave(
        ChannelStateSnapshot snapshot,
        long saveGeneration,
        out long bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = _channelStates[snapshot.ChannelNumber];
        lock (state.Gate)
        {
            long currentGeneration = Volatile.Read(ref state.SaveGeneration);
            if (saveGeneration != currentGeneration)
            {
                bytesWritten = 0;
                _logger.LogWarning(
                    "channel {ChannelNumber}: skipped snapshot from reset generation {SnapshotGeneration}; current generation is {CurrentGeneration}",
                    snapshot.ChannelNumber, saveGeneration,
                    currentGeneration);
                return false;
            }
            if (!state.Initialized)
                _ = TryLoadCore(snapshot.ChannelNumber, state, logLoaded: false);
            bytesWritten = SaveLocked(snapshot, state);
            return true;
        }
    }

    /// <summary>
    /// Issue #271: removes every on-disk snapshot artifact for the
    /// channel — all rolling generations, the legacy single-file
    /// snapshot if present, plus any leftover tmp files. Returns the
    /// count of deleted files. The internal slot-rotation cursor is
    /// reset so the next <see cref="Save"/> starts from slot 0.
    /// </summary>
    public int DeleteAll(byte channelNumber)
    {
        var state = _channelStates[channelNumber];
        lock (state.Gate)
        {
            Interlocked.Increment(ref state.SaveGeneration);
            int removed = 0;
            for (int i = 0; i < _generations; i++)
            {
                removed += TryDelete(SlotPath(channelNumber, i));
                removed += TryDelete(TempPath(channelNumber, i));
            }
            removed += TryDelete(LegacyPath(channelNumber));
            state.Initialized = true;
            state.HighestSavedSeq = 0;
            state.LastUsedSlot = -1;
            if (removed > 0)
            {
                try { FsyncDirectory(_dataDir); }
                catch { /* best effort */ }
                _logger.LogInformation(
                    "channel {ChannelNumber}: admin DeleteAll removed {Removed} snapshot file(s)",
                    channelNumber, removed);
            }
            return removed;
        }
    }

    private long SaveLocked(
        ChannelStateSnapshot snapshot,
        ChannelPersistenceState state)
    {
        BeforeSaveForTesting?.Invoke();
        if (snapshot.LastAppliedSeq < state.HighestSavedSeq)
        {
            _logger.LogWarning(
                "channel {ChannelNumber}: skipped stale snapshot save at LastAppliedSeq={SnapshotSeq}; durable high-water mark is {DurableSeq}",
                snapshot.ChannelNumber, snapshot.LastAppliedSeq, state.HighestSavedSeq);
            return 0;
        }

        int slot = (state.LastUsedSlot + 1) % _generations;
        var path = SlotPath(snapshot.ChannelNumber, slot);
        var tmp = TempPath(snapshot.ChannelNumber, slot);
        long bytesWritten;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (_writeFormat == SnapshotFileFormat.Binary)
                {
                    var bytes = BinaryChannelStateSnapshotCodec.Encode(snapshot);
                    fs.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    JsonSerializer.Serialize(fs, snapshot, JsonOptions);
                }
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

        state.LastUsedSlot = slot;
        state.HighestSavedSeq = Math.Max(
            state.HighestSavedSeq, snapshot.LastAppliedSeq);
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

    private static int TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return 1;
            }
        }
        catch { /* best effort — admin path */ }
        return 0;
    }

    private ChannelStateSnapshot? TryLoadCore(
        byte channelNumber,
        ChannelPersistenceState state,
        bool logLoaded)
    {
        var candidates = EnumerateCandidateFiles(channelNumber);
        if (candidates.Count == 0)
        {
            state.Initialized = true;
            state.HighestSavedSeq = 0;
            state.LastUsedSlot = -1;
            return null;
        }

        ChannelStateSnapshot? bestSnapshot = null;
        SnapshotCandidate bestCandidate = default;
        foreach (var candidate in candidates)
        {
            try
            {
                byte[] payload = File.ReadAllBytes(candidate.Path);
                ChannelStateSnapshot? snapshot;
                if (BinaryChannelStateSnapshotCodec.LooksLikeBinarySnapshot(payload))
                {
                    snapshot = BinaryChannelStateSnapshotCodec.Decode(payload);
                }
                else
                {
                    var root = JsonNode.Parse(payload);
                    if (root is null)
                    {
                        _logger.LogWarning(
                            "channel {ChannelNumber}: snapshot at {Path} parsed to null; ignoring generation",
                            channelNumber, candidate.Path);
                        continue;
                    }
                    var migrated = _migrations.MigrateToCurrent(
                        root, ChannelStateSnapshot.CurrentVersion);
                    snapshot = JsonSerializer.Deserialize<ChannelStateSnapshot>(
                        migrated.ToJsonString(), JsonOptions);
                }
                if (snapshot is null)
                {
                    _logger.LogWarning(
                        "channel {ChannelNumber}: snapshot deserialized to null at {Path}; ignoring generation",
                        channelNumber, candidate.Path);
                    continue;
                }
                if (bestSnapshot is null
                    || snapshot.LastAppliedSeq > bestSnapshot.LastAppliedSeq
                    || (snapshot.LastAppliedSeq == bestSnapshot.LastAppliedSeq
                        && candidate.Mtime > bestCandidate.Mtime))
                {
                    bestSnapshot = snapshot;
                    bestCandidate = candidate;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "channel {ChannelNumber}: failed to load snapshot at {Path}; ignoring generation",
                    channelNumber, candidate.Path);
            }
        }

        state.Initialized = true;
        if (bestSnapshot is null)
        {
            state.HighestSavedSeq = 0;
            state.LastUsedSlot = -1;
            foreach (var candidate in candidates)
            {
                if (candidate.Slot < 0) continue;
                state.LastUsedSlot = candidate.Slot;
                break;
            }
            _logger.LogError(
                "channel {ChannelNumber}: all {Count} candidate snapshot files failed to load",
                channelNumber, candidates.Count);
            return null;
        }

        state.HighestSavedSeq = bestSnapshot.LastAppliedSeq;
        state.LastUsedSlot = bestCandidate.Slot;
        if (logLoaded)
        {
            _logger.LogInformation(
                "channel {ChannelNumber}: loaded snapshot from {Path} (lastAppliedSeq={LastAppliedSeq}, seq={SequenceNumber}/{SequenceVersion}, owners={OwnerCount})",
                channelNumber, bestCandidate.Path, bestSnapshot.LastAppliedSeq,
                bestSnapshot.SequenceNumber, bestSnapshot.SequenceVersion,
                bestSnapshot.Owners.Count);
        }
        return bestSnapshot;
    }

    /// <summary>
    /// Returns existing snapshot files for the channel (slot files +
    /// legacy single-file if present), sorted by mtime descending for
    /// deterministic tie-breaking.
    /// </summary>
    private List<SnapshotCandidate> EnumerateCandidateFiles(byte channelNumber)
    {
        var list = new List<SnapshotCandidate>(_generations + 1);
        for (int i = 0; i < _generations; i++)
        {
            var p = SlotPath(channelNumber, i);
            if (File.Exists(p))
                list.Add(new SnapshotCandidate(p, File.GetLastWriteTimeUtc(p), i));
        }
        var legacy = LegacyPath(channelNumber);
        if (File.Exists(legacy))
            list.Add(new SnapshotCandidate(
                legacy, File.GetLastWriteTimeUtc(legacy), Slot: -1));
        list.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
        return list;
    }

    private static ChannelPersistenceState[] CreateChannelStates()
    {
        var states = new ChannelPersistenceState[byte.MaxValue + 1];
        for (int i = 0; i < states.Length; i++)
            states[i] = new ChannelPersistenceState();
        return states;
    }

    private readonly record struct SnapshotCandidate(
        string Path,
        DateTime Mtime,
        int Slot);

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
