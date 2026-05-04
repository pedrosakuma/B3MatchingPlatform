using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// One per UMDF channel. Owns:
///  - A <see cref="MatchingEngine"/> (single-threaded by construction).
///  - A bounded inbound queue of decoded EntryPoint commands tagged with the
///    originating session's <see cref="SessionId"/> + EnteringFirm — both
///    are <b>value types</b>; Core never holds a transport reference.
///  - An order-id → <see cref="SessionId"/> map (value-only) so PASSIVE-side
///    execution reports (e.g. a resting order is filled by a counterparty's
///    aggressor) get routed back to the correct session by the
///    <see cref="ICoreOutbound"/> implementation in the Gateway.
///  - A buffer that accumulates UMDF MBO/Trade frames emitted by the engine
///    during a single command's execution. The buffered events are then
///    flushed as one packet (with a packet-header + monotonic
///    <c>SequenceNumber</c>) to the <see cref="IUmdfPacketSink"/>.
///
/// Implements both <see cref="IInboundCommandSink"/> (commands in) and
/// <see cref="IMatchingEventSink"/> (engine events out). The dispatch loop
/// guarantees that the engine and the event-sink callbacks always run on the
/// dedicated dispatch thread — there is no cross-thread call into the engine.
///
/// <para><b>File layout (issue #168 split — partial class facets):</b></para>
/// <list type="bullet">
/// <item><description><c>ChannelDispatcher.cs</c> — class declaration,
/// fields, properties, constructor (this file).</description></item>
/// <item><description><c>ChannelDispatcher.Lifecycle.cs</c> — <see cref="Start"/>,
/// dispatch loop, heartbeat, single-thread invariant assert,
/// <see cref="DisposeAsync"/>, <c>TestProbe</c>.</description></item>
/// <item><description><c>ChannelDispatcher.Loop.cs</c> — <c>ProcessOne</c>
/// (per-work-item dispatch switch + tracing + metrics).</description></item>
/// <item><description><c>ChannelDispatcher.Inbox.cs</c> —
/// <c>IInboundCommandSink</c> producer methods + enqueue tracing
/// span.</description></item>
/// <item><description><c>ChannelDispatcher.Sinks.cs</c> —
/// <c>IMatchingEventSink</c> callbacks (UMDF frame buffering + ER
/// fan-out).</description></item>
/// <item><description><c>ChannelDispatcher.Operator.cs</c> — operator
/// commands (snapshot rotation, channel reset, trade-bust replay).</description></item>
/// <item><description><c>ChannelDispatcher.Packet.cs</c> — UMDF packet
/// buffer (<c>ReserveOrFlush</c> / <c>Commit</c> / <c>FlushPacket</c>).</description></item>
/// <item><description><c>ChannelDispatcher.WorkItem.cs</c> — internal
/// <c>WorkKind</c> / <c>WorkItem</c> records and source-gen
/// <c>LoggerMessage</c> declarations.</description></item>
/// </list>
///
/// <para><b>Threading contract (issue #138):</b></para>
/// <list type="bullet">
/// <item><description><b>Producers</b> (any thread) call only the
/// <c>Enqueue*</c> / <see cref="OnDecodeError"/> / <see cref="OnSessionClosed"/>
/// methods, which post a <c>WorkItem</c> to the bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> and return. No engine
/// or buffer state is mutated from the producer thread.</description></item>
/// <item><description><b>The dispatch loop thread</b> (the single
/// <see cref="Thread"/> spun up by <see cref="Start"/>) is the sole mutator of
/// engine state, the packet buffer, the per-channel <c>OrderRegistry</c>,
/// and the
/// <see cref="SequenceNumber"/> / <see cref="SequenceVersion"/> counters.
/// Every mutation path asserts <c>Thread.CurrentThread == _loopThread</c>
/// in DEBUG builds via <c>AssertOnLoopThread</c>.</description></item>
/// <item><description><b>External readers</b> (e.g. <c>HttpServer.RenderProm</c>
/// on an HTTP worker thread) read <see cref="SequenceNumber"/> /
/// <see cref="SequenceVersion"/> via the public getters, which use
/// <see cref="Volatile.Read(ref uint)"/> / <see cref="Volatile.Read(ref ushort)"/>
/// against the backing fields written with the corresponding
/// <see cref="Volatile.Write(ref uint, uint)"/> calls. This guarantees no
/// torn / hoisted reads on weak memory models (ARM64, AOT). All other
/// counters reachable by HTTP scrapes go through <see cref="MetricsRegistry"/>,
/// which already uses <see cref="Interlocked"/> primitives.</description></item>
/// </list>
/// </summary>
public sealed partial class ChannelDispatcher : IInboundCommandSink, IMatchingEventSink, IAsyncDisposable
{
    private const int DefaultInboundCapacity = 4096;
    private const int MaxPacketBytes = 1400;

    /// <summary>
    /// Maximum time the dispatch loop will block waiting for new work before
    /// emitting a liveness heartbeat. Kept short (1s) so the
    /// <c>/health/live</c> default threshold (5s) is comfortably exceeded
    /// only when the loop thread is actually wedged.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    public byte ChannelNumber { get; }

    // Backing fields for the cross-thread-readable counters. Written only on
    // the dispatch loop thread via Volatile.Write; read from any thread via
    // Volatile.Read (see SequenceVersion / SequenceNumber properties below).
    // Marked volatile so that ARM64 / AOT codegen cannot hoist or reorder
    // the load against neighbouring reads of the packet buffer (issue #138).
    private uint _sequenceNumber;
    private ushort _sequenceVersion;

    /// <summary>Monotonic per-channel UMDF packet sequence version. Bumped on
    /// channel-reset / counter rollover. Safe to read from any thread.</summary>
    public ushort SequenceVersion => Volatile.Read(ref _sequenceVersion);

    /// <summary>Monotonic UMDF packet sequence number within the current
    /// <see cref="SequenceVersion"/>. Safe to read from any thread.</summary>
    public uint SequenceNumber => Volatile.Read(ref _sequenceNumber);

    /// <summary>
    /// Pending work-items in the bounded inbound channel. Snapshot value;
    /// safe to read from any thread (the underlying <see cref="System.Threading.Channels.Channel{T}"/>
    /// reader supports counting). Used by the host's graceful-shutdown
    /// drain loop (issue #171) to wait until in-flight commands have been
    /// observed by the engine before broadcasting Terminate.
    /// </summary>
    public int InboundQueueDepth => _inbound.Reader.Count;

    private readonly System.Threading.Channels.Channel<WorkItem> _inbound;
    private readonly MatchingEngine _engine;
    private readonly IUmdfPacketSink _packetSink;
    private readonly ICoreOutbound _outbound;
    private readonly ILogger<ChannelDispatcher> _logger;
    private readonly Func<ulong> _nowNanos;
    private readonly ushort _tradeDate;
    private readonly ChannelMetrics? _metrics;
    private readonly BoundedSessionFirmCounters? _sessionFirmCounters;
    private readonly OrderRegistry _orders = new();

    private readonly byte[] _packetBuf = new byte[MaxPacketBytes];
    private int _packetWritten;
    private SessionId _currentSession;
    private uint _currentFirm;
    private bool _hasCurrentSession;
    private ulong _currentClOrdId;
    private ulong _currentOrigClOrdId;
    /// <summary>
    /// Ingress timestamp of the inbound command currently being dispatched
    /// (#GAP-11 / #49). Captured from the command's <c>EnteredAtNanos</c> at
    /// the start of <c>ProcessOne</c> and reset to
    /// <see cref="ulong.MaxValue"/> (the SBE null sentinel for
    /// <c>UTCTimestampNanosOptional</c>) in the <c>finally</c> so engine-
    /// originated events that fire outside command processing (iceberg
    /// restate, stop triggers, etc.) emit ER frames with the
    /// <c>receivedTime</c> field nulled.
    /// </summary>
    private ulong _currentReceivedTimeNanos = ulong.MaxValue;

    private SnapshotRotator? _snapshotRotator;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    // Captured on entry to RunLoopAsync; used by AssertOnLoopThread() to
    // enforce the dispatch-thread invariant in DEBUG builds.
    private Thread? _loopThread;

    /// <summary>
    /// The snapshot rotator bound to this dispatcher, if any. Always invoked
    /// on the dispatch thread via a <see cref="WorkKind.SnapshotRotation"/>
    /// work item so it observes a stable book.
    /// </summary>
    public SnapshotRotator? SnapshotRotator => _snapshotRotator;

    public ChannelDispatcher(byte channelNumber, Func<IMatchingEventSink, MatchingEngine> engineFactory, IUmdfPacketSink packetSink,
        ICoreOutbound outbound,
        ILogger<ChannelDispatcher> logger,
        Func<ulong>? nowNanos = null, ushort tradeDate = 0, int inboundCapacity = DefaultInboundCapacity,
        ChannelMetrics? metrics = null,
        BoundedSessionFirmCounters? sessionFirmCounters = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outbound);
        ChannelNumber = channelNumber;
        _packetSink = packetSink;
        _outbound = outbound;
        _logger = logger;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _tradeDate = tradeDate;
        _metrics = metrics;
        _sessionFirmCounters = sessionFirmCounters;
        // Direct field writes are safe here: ctor runs on the constructing
        // thread before Start() and before any other thread can observe the
        // instance. No memory barrier is needed.
        _sequenceVersion = 1;
        _sequenceNumber = 0;
        _inbound = System.Threading.Channels.Channel.CreateBounded<WorkItem>(
            new System.Threading.Channels.BoundedChannelOptions(inboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // Use Wait so that TryWrite returns false when the channel
                // is full (instead of silently dropping the item as DropWrite
                // does). Callers handle the false return by incrementing the
                // exch_dispatch_queue_full_total counter and logging.
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });
        _engine = engineFactory(this);
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
}
