using B3.Exchange.Contracts;
using B3.Exchange.PostTrade;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Options bag for constructing a <see cref="ChannelDispatcher"/>.
///
/// Introduced for issue #381 to replace the previous 18-parameter
/// constructor with a single options record. Init-only by design; the
/// dispatcher snapshots every value at construction time.
///
/// Required properties (<c>PacketSink</c>, <c>Outbound</c>, <c>Logger</c>)
/// are enforced by the C# 11 <c>required</c> modifier — missing them
/// produces a compile-time error at the call site.
/// </summary>
public sealed record ChannelDispatcherOptions
{
    public required IUmdfPacketSink PacketSink { get; init; }
    public required ICoreOutbound Outbound { get; init; }
    public required ILogger<ChannelDispatcher> Logger { get; init; }

    public Func<ulong>? NowNanos { get; init; }
    public ushort TradeDate { get; init; }
    public int InboundCapacity { get; init; } = ChannelDispatcher.DefaultInboundCapacity;
    public ChannelMetrics? Metrics { get; init; }
    public BoundedSessionFirmCounters? SessionFirmCounters { get; init; }
    public UmdfPacketRetransmitBuffer? RetxBuffer { get; init; }
    public IChannelStatePersister? Persister { get; init; }
    public SnapshotThrottlePolicy? SnapshotThrottle { get; init; }
    public bool UseAsyncSnapshotWriter { get; init; }
    public IChannelWriteAheadLog? Wal { get; init; }
    public WalAppendFailurePolicy WalAppendFailurePolicy { get; init; } = WalAppendFailurePolicy.Continue;
    public Func<string, bool>? SessionExists { get; init; }
    public OrphanSessionPolicy OrphanPolicy { get; init; } = OrphanSessionPolicy.Drop;
    public IReadOnlyList<long>? SeedSecurityIds { get; init; }
    public IPostTradeSink? PostTradeSink { get; init; }
    public string? AuditRootDir { get; init; }
    public BustDedupIndex? BustDedup { get; init; }
    public string? DropRootDir { get; init; }
    public IAmendmentsPublisher? AmendmentsPublisher { get; init; }
}
