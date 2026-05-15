using System.Text.Json.Nodes;

namespace B3.Exchange.Core;

/// <summary>
/// Issue #272: per-version migration shim used by persister implementations
/// to upgrade an older on-disk <see cref="ChannelStateSnapshot"/> JSON
/// payload to <see cref="ChannelStateSnapshot.CurrentVersion"/> *before*
/// final deserialization into the strongly-typed record.
///
/// <para><b>Why JSON-level migration:</b> System.Text.Json deserializes
/// init-only records positionally — once a property is added or removed,
/// older payloads either fail to materialise the record or silently drop
/// fields that no longer match by name. Migrating the
/// <see cref="JsonNode"/> tree first lets us reshape the document to match
/// the current schema (rename, add defaults, recompute, drop) using a
/// single chain of small, composable, version-bound functions.</para>
///
/// <para><b>Forward compatibility:</b> System.Text.Json ignores unknown
/// JSON properties on read by default — a payload written by a NEWER
/// host therefore loads cleanly under an OLDER host as long as the
/// fields it ALREADY knows about retain their meaning. Best practice:
/// only add new optional fields with sensible defaults; never repurpose
/// or rename a field without a version bump + migration.</para>
///
/// <para><b>Backward compatibility:</b> a payload written by an OLDER
/// host requires a registered migration for every step from its
/// <c>Version</c> to <see cref="ChannelStateSnapshot.CurrentVersion"/>.
/// <see cref="MigrateToCurrent"/> applies the chain in order; missing
/// any step throws so the dispatcher fails closed (per issue #270's
/// fail-closed restore contract).</para>
///
/// <para><b>Forward-version rejection:</b> a payload whose
/// <c>Version</c> is higher than <see cref="ChannelStateSnapshot.CurrentVersion"/>
/// is rejected — the older host has no way to know whether the future
/// schema's invariants are compatible. Operators must run a host
/// version that supports the snapshot, or invoke
/// <c>POST /admin/channels/{ch}/snapshot/reset?force=true</c> to start
/// fresh.</para>
///
/// <para>Instances are mutable but registration is intended to be done
/// during host startup (or in test setup) before any
/// <see cref="MigrateToCurrent"/> call. Concurrent registration is
/// guarded internally so misuse from background threads still yields a
/// consistent table.</para>
/// </summary>
public sealed class SnapshotMigrationSet
{
    /// <summary>Migration delegate: takes the JSON tree of a snapshot at
    /// version <c>fromVersion</c>, returns the JSON tree shaped as
    /// <c>fromVersion + 1</c>. Implementations must mutate-and-return or
    /// build a new node — the returned node replaces the input in the
    /// chain.</summary>
    public delegate JsonNode Migration(JsonNode previous);

    private readonly object _lock = new();
    private readonly SortedDictionary<int, Migration> _migrations = new();

    /// <summary>
    /// Builds the default migration set used by the file persister.
    /// Registers the <c>1 → 2</c> migration (issue #319) which bumps
    /// the version stamp. The new schema only adds two optional fields
    /// (<see cref="OrderOwnerSnapshot.OriginalQty"/> and
    /// <see cref="OrderOwnerSnapshot.CumQty"/>) that default to 0;
    /// the dispatcher's <c>RestoreChannelState</c> reconstructs a
    /// sensible <c>OrderQty</c> from the engine's remaining quantity
    /// when both are 0.
    /// </summary>
    public static SnapshotMigrationSet BuildDefault()
    {
        var set = new SnapshotMigrationSet();
        set.Register(1, MigrateV1ToV2);
        // Issue #322: v3 only adds an optional `Halts` collection on the
        // engine snapshot. v2 trees are upgraded by stamping the version;
        // STJ's missing-property tolerance leaves Halts at the default
        // (null) on deserialise.
        set.Register(2, MigrateV2ToV3);
        return set;
    }

    private static JsonNode MigrateV1ToV2(JsonNode previous)
    {
        if (previous is JsonObject obj)
            obj["Version"] = 2;
        return previous;
    }

    private static JsonNode MigrateV2ToV3(JsonNode previous)
    {
        if (previous is JsonObject obj)
            obj["Version"] = 3;
        return previous;
    }

    /// <summary>
    /// Registers a migration that upgrades a snapshot tree from
    /// <paramref name="fromVersion"/> to <c>fromVersion + 1</c>. The
    /// delegate must produce a tree whose <c>Version</c> field equals
    /// <c>fromVersion + 1</c>; <see cref="MigrateToCurrent"/> verifies
    /// this after each step and throws on mismatch.
    /// </summary>
    public void Register(int fromVersion, Migration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        if (fromVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(fromVersion),
                "fromVersion must be >= 0");
        lock (_lock)
        {
            _migrations[fromVersion] = migration;
        }
    }

    /// <summary>
    /// Walks the registered chain from the source <paramref name="root"/>'s
    /// <c>Version</c> field up to <paramref name="targetVersion"/>. Returns
    /// the migrated root. Throws when the source version is greater than
    /// the target (forward-version rejection) or when a step on the path
    /// is missing.
    /// </summary>
    public JsonNode MigrateToCurrent(JsonNode root, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(root);
        int current = ReadVersion(root);
        if (current == targetVersion) return root;
        if (current > targetVersion)
            throw new InvalidOperationException(
                $"snapshot version {current} is newer than supported version {targetVersion}; " +
                "upgrade the host or reset the snapshot via POST /admin/channels/{ch}/snapshot/reset?force=true");

        var node = root;
        while (current < targetVersion)
        {
            Migration? step;
            lock (_lock)
            {
                _migrations.TryGetValue(current, out step);
            }
            if (step is null)
                throw new InvalidOperationException(
                    $"no registered migration from snapshot version {current} to {current + 1} " +
                    $"(target {targetVersion}); the on-disk snapshot cannot be upgraded by this host");
            node = step(node);
            int next = ReadVersion(node);
            int expected = current + 1;
            if (next != expected)
                throw new InvalidOperationException(
                    $"migration {current}->{expected} produced a tree with version {next}; " +
                    "migration delegates must stamp the new version on their output");
            current = next;
        }
        return node;
    }

    /// <summary>
    /// Reads the <c>Version</c> field from a snapshot JSON tree. Treats
    /// a missing or null field as version <c>0</c> so legacy payloads
    /// that predate explicit versioning can be migrated by registering
    /// a <c>0 → 1</c> shim.
    /// </summary>
    private static int ReadVersion(JsonNode root)
    {
        if (root is not JsonObject obj) return 0;
        if (!obj.TryGetPropertyValue("Version", out var v) || v is null)
            return 0;
        try { return v.GetValue<int>(); }
        catch { return 0; }
    }
}
