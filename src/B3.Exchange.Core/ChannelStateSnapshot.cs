using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// Persistable view of a single per-channel order ownership tuple
/// (issue #260). One entry per resting order; the persister captures these
/// alongside the engine snapshot so <see cref="OrderRegistry"/> can be
/// rebuilt and PASSIVE-side execution reports keep routing to the original
/// session after a restart (assuming the same session reconnects).
///
/// <para>Issue #319: <see cref="OriginalQty"/> and <see cref="CumQty"/>
/// are persisted so multi-fill <c>cumQty</c>/<c>leavesQty</c> survives
/// restart. Defaulting them to 0 keeps construction sites backwards
/// compatible (legacy v1 snapshots roundtrip cleanly; the dispatcher's
/// restore path reconstructs <c>OriginalQty = engineRemainingQty + CumQty</c>
/// when the persisted value is 0).</para>
/// </summary>
public sealed record OrderOwnerSnapshot(
    long OrderId,
    string SessionValue,
    uint Firm,
    ulong ClOrdId,
    Side Side,
    long SecurityId)
{
    public long OriginalQty { get; init; }
    public long CumQty { get; init; }
}

/// <summary>
/// Per-channel persistable state captured by
/// <see cref="ChannelDispatcher.CaptureChannelState"/> and consumed by
/// <see cref="ChannelDispatcher.RestoreChannelState"/>. Wraps the
/// engine-owned <see cref="EngineStateSnapshot"/> and adds the
/// dispatcher-owned counters and per-order ownership index.
///
/// <para><b>Out of scope (v1):</b> the UMDF retransmit ring, snapshot
/// rotator cursor, and any in-flight buffered packet are NOT persisted
/// — restored channels emit fresh packets at <c>SequenceNumber+1</c> with
/// the previous <c>SequenceVersion</c>; consumers that miss the gap
/// recover via the snapshot feed.</para>
/// </summary>
public sealed record ChannelStateSnapshot(
    /// <summary>Snapshot schema version. Bumped on incompatible layout
    /// changes so old snapshots can be detected and rejected on load.</summary>
    int Version,
    byte ChannelNumber,
    uint SequenceNumber,
    ushort SequenceVersion,
    EngineStateSnapshot Engine,
    IReadOnlyList<OrderOwnerSnapshot> Owners)
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Issue #269: monotonic counter of commands the dispatcher has
    /// applied at the time this snapshot was captured. Used by the
    /// Write-Ahead Log replay path to skip entries already covered by
    /// the snapshot. Defaults to 0 for snapshots persisted by the
    /// pre-#269 dispatcher; the WAL replay treats 0 as "no prior
    /// applied seq" and replays everything in the log.
    /// </summary>
    public long LastAppliedSeq { get; init; }
}

/// <summary>
/// Pluggable persistence sink for per-channel matching state (issue #260).
/// Production wires <c>FileChannelStatePersister</c> from
/// <c>B3.Exchange.Persistence</c>; tests use in-memory fakes.
///
/// <para>All methods are invoked on the channel's dispatch thread (single
/// writer). Implementations MUST treat <see cref="Save"/> as best-effort
/// and never throw — exceptions are swallowed and logged by the dispatcher
/// so a transient disk error does not crash the engine.</para>
/// </summary>
public interface IChannelStatePersister
{
    /// <summary>Loads the most recent persisted snapshot for the
    /// channel, or <c>null</c> when none exists / load failed.</summary>
    ChannelStateSnapshot? TryLoad(byte channelNumber);

    /// <summary>Persists <paramref name="snapshot"/> atomically and
    /// returns the number of bytes written (or 0 if the implementation
    /// does not measure size). Implementations should perform tmp-write
    /// + fsync + rename so a crash mid-write never leaves a partial file
    /// on disk.</summary>
    long Save(ChannelStateSnapshot snapshot);

    /// <summary>
    /// Issue #271: deletes every on-disk snapshot artifact for the
    /// given channel (all rolling generations + any legacy files).
    /// Used by the admin "snapshot/reset" endpoint so an operator can
    /// force the next boot of this channel to start with no
    /// pre-existing state. Returns the number of files removed (0 when
    /// none existed). Default implementation is a no-op so in-memory
    /// fakes used by tests don't need to implement it.
    /// </summary>
    int DeleteAll(byte channelNumber) => 0;
}
