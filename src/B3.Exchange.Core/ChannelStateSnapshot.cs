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
/// </summary>
public sealed record OrderOwnerSnapshot(
    long OrderId,
    string SessionValue,
    uint Firm,
    ulong ClOrdId,
    Side Side,
    long SecurityId);

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
    public const int CurrentVersion = 1;
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
}
