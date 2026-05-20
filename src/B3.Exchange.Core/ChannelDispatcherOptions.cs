using B3.Exchange.Contracts;
using B3.Exchange.Contracts.Time;
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

    public INanosTimeSource? TimeSource { get; init; }
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
    /// <summary>
    /// Optional bust-orchestration port (issue #380 / ADR 0010). When
    /// supplied, the dispatcher delegates bust validation, audit / dedup
    /// writes and amendments republish to this orchestrator instead of
    /// composing them inline from <see cref="PostTradeSink"/>,
    /// <see cref="AuditRootDir"/>, <see cref="BustDedup"/>,
    /// <see cref="DropRootDir"/> and <see cref="AmendmentsPublisher"/>.
    /// When <c>null</c> and the inline deps are sufficient
    /// (<see cref="PostTradeSink"/>, <see cref="AuditRootDir"/> and
    /// <see cref="BustDedup"/> all non-null), the dispatcher constructs
    /// a default <see cref="B3.Exchange.PostTrade.PostTradeOrchestrator"/>
    /// internally — every pre-existing call site continues to work
    /// unchanged.
    /// </summary>
    public IPostTradeOrchestrator? PostTradeOrchestrator { get; init; }
}
