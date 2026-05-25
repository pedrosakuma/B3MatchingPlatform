using System.Text.Json.Nodes;
using B3.Exchange.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #272: schema evolution policy tests. Cover the happy paths
/// (forward compat via unknown-field ignore, backward compat via
/// registered migration) and rejection paths (no migration, future
/// version) of <see cref="SnapshotMigrationSet"/> as wired into
/// <see cref="FileChannelStatePersister"/>.
/// </summary>
public class SnapshotMigrationTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3-migration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteRawSnapshot(string dir, byte channelNumber, string json)
    {
        var path = Path.Combine(dir, $"channel-{channelNumber}.snapshot.0");
        File.WriteAllText(path, json);
        return path;
    }

    private const string MinimalV1Body =
        "\"ChannelNumber\":7," +
        "\"SequenceNumber\":42," +
        "\"SequenceVersion\":3," +
        "\"Engine\":{\"NextOrderId\":1,\"NextTradeId\":1,\"RptSeq\":0,\"Phases\":[],\"Books\":[]}," +
        "\"Owners\":[]," +
        "\"LastAppliedSeq\":0";

    [Fact]
    public void ForwardCompat_UnknownFieldIsIgnored()
    {
        // Snapshot at the current version with an extra unknown field
        // simulating a payload written by a NEWER host. Default STJ
        // behaviour ignores unknown properties on read.
        var dir = NewTempDir();
        try
        {
            var json = "{\"Version\":1," + MinimalV1Body + ",\"FuturisticField\":\"xyz\"}";
            WriteRawSnapshot(dir, 7, json);
            var p = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance);
            var loaded = p.TryLoad(7);
            Assert.NotNull(loaded);
            // Issue #453: chain now upgrades to v4, the new
            // CurrentVersion (was v3 in #322).
            Assert.Equal(4, loaded!.Version);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void BackwardCompat_MigratesV0ToV1ViaRegisteredShim()
    {
        var dir = NewTempDir();
        try
        {
            // Pretend a legacy v0 snapshot was on disk: NO Version
            // field at all (ReadVersion treats this as 0).
            var json = "{" + MinimalV1Body + "}";
            WriteRawSnapshot(dir, 7, json);

            var migrations = SnapshotMigrationSet.BuildDefault();
            migrations.Register(0, prev =>
            {
                // The "migration" stamps the new version onto the
                // existing tree — no other shape change is needed
                // because v1 just added Version.
                var obj = (JsonObject)prev;
                obj["Version"] = 1;
                return obj;
            });

            var p = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                migrations: migrations);
            var loaded = p.TryLoad(7);
            Assert.NotNull(loaded);
            // Issue #453: chain is 0→1 (registered here) followed by
            // 1→2, 2→3, and 3→4 (registered by BuildDefault), so the
            // loaded version reflects CurrentVersion = 4.
            Assert.Equal(4, loaded!.Version);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void MissingMigration_FailsLoadWithClearMessage()
    {
        var dir = NewTempDir();
        try
        {
            // v0 payload but the persister's migration set is empty.
            var json = "{\"Version\":0," + MinimalV1Body + "}";
            WriteRawSnapshot(dir, 7, json);

            var p = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance);
            // TryLoad swallows per-file errors and walks generations;
            // with only one (failing) candidate it returns null.
            var loaded = p.TryLoad(7);
            Assert.Null(loaded);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FutureVersion_IsRejected()
    {
        // Direct unit test against the migration set: a payload from a
        // host newer than us must be rejected even when no migration
        // exists for the gap.
        var migrations = SnapshotMigrationSet.BuildDefault();
        var futureRoot = JsonNode.Parse("{\"Version\":99}")!;
        var ex = Assert.Throws<InvalidOperationException>(
            () => migrations.MigrateToCurrent(futureRoot, ChannelStateSnapshot.CurrentVersion));
        Assert.Contains("newer than supported", ex.Message);
    }

    [Fact]
    public void Migration_ChainedAcrossMultipleVersions()
    {
        // Validates that the chain walks step-by-step (0 → 1 → 2).
        var migrations = SnapshotMigrationSet.BuildDefault();
        migrations.Register(0, prev =>
        {
            var obj = (JsonObject)prev;
            obj["Version"] = 1;
            obj["AddedAtV1"] = "first";
            return obj;
        });
        migrations.Register(1, prev =>
        {
            var obj = (JsonObject)prev;
            obj["Version"] = 2;
            obj["AddedAtV2"] = "second";
            return obj;
        });

        var root = JsonNode.Parse("{}")!;
        var migrated = migrations.MigrateToCurrent(root, targetVersion: 2);
        var obj = Assert.IsType<JsonObject>(migrated);
        Assert.Equal(2, obj["Version"]!.GetValue<int>());
        Assert.Equal("first", obj["AddedAtV1"]!.GetValue<string>());
        Assert.Equal("second", obj["AddedAtV2"]!.GetValue<string>());
    }

    [Fact]
    public void Migration_ThatForgetsToBumpVersion_IsRejected()
    {
        var migrations = SnapshotMigrationSet.BuildDefault();
        migrations.Register(0, prev => prev); // buggy: no version stamp

        var root = JsonNode.Parse("{\"Version\":0}")!;
        var ex = Assert.Throws<InvalidOperationException>(
            () => migrations.MigrateToCurrent(root, targetVersion: 1));
        Assert.Contains("must stamp the new version", ex.Message);
    }
}
