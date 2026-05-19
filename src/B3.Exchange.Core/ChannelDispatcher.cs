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

    /// <summary>Issue #216 (Onda L · L3a): exposes the per-channel UMDF
    /// retransmit ring (or <c>null</c> when buffering is disabled). The
    /// future TCP retransmit responder (L3b) reads through this; tests
    /// inspect it to verify <c>FlushPacket</c> appends.</summary>
    public UmdfPacketRetransmitBuffer? RetransmitBuffer => _retxBuffer;

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
    private IUmdfPacketSink _packetSink;
    private ICoreOutbound _outbound;
    private readonly IUmdfPacketSink _liveSink;
    private readonly ICoreOutbound _liveOutbound;
    private readonly ILogger<ChannelDispatcher> _logger;
    private readonly Func<ulong> _nowNanos;
    private readonly ushort _tradeDate;
    private readonly ChannelMetrics? _metrics;
    private readonly BoundedSessionFirmCounters? _sessionFirmCounters;
    private readonly OrderRegistry _orders = new();
    /// <summary>Issue #216 (Onda L · L3a): per-channel UMDF retransmit
    /// ring. Populated on each <c>FlushPacket</c> for the incremental
    /// feed only. <c>null</c> when retransmit buffering is disabled
    /// (host config <c>umdfRetransmit.bufferSize=0</c>).</summary>
    private readonly UmdfPacketRetransmitBuffer? _retxBuffer;

    /// <summary>
    /// Issue #269: per-channel Write-Ahead Log. When non-null, the
    /// dispatcher appends one record per state-mutating command before
    /// invoking the engine; on cold start the loop loads the snapshot
    /// then replays surviving WAL records to recover from an unclean
    /// shutdown. <c>null</c> ⇒ pre-#269 behaviour (snapshot only,
    /// commands processed since the last snapshot are lost).
    /// </summary>
    private readonly IChannelWriteAheadLog? _wal;
    /// <summary>
    /// Issue #286: behaviour of <see cref="WalAppendIfEnabled"/> when
    /// <see cref="IChannelWriteAheadLog.Append"/> throws. Captured from
    /// the ctor; immutable for the dispatcher's lifetime.
    /// </summary>
    private readonly WalAppendFailurePolicy _walAppendFailurePolicy;
    /// <summary>
    /// Issue #286: sticky flag set on the first WAL append failure when
    /// the policy is <see cref="WalAppendFailurePolicy.Halt"/>. Read from
    /// any thread (Inbox short-circuit + readiness probe) — accessed via
    /// <see cref="Volatile"/> for cross-thread visibility. Cleared only
    /// by host restart.
    /// </summary>
    private int _walHalted;
    /// <summary>Monotonic per-channel command counter for the WAL
    /// (issue #269). Mutated only on the dispatch thread; persisted into
    /// <see cref="ChannelStateSnapshot.LastAppliedSeq"/> so the replay
    /// path can skip records already covered by the snapshot.</summary>
    private long _lastAppliedSeq;
    /// <summary>Set during <see cref="LoadPersistedStateOnLoopThread"/>
    /// for the duration of WAL replay so <see cref="FlushPacket"/> and
    /// the outbound ER calls become no-ops — engine state still mutates
    /// (and seq counters still advance) but no UMDF packet hits the
    /// wire and no ExecutionReport is sent to a (potentially long-gone)
    /// session.</summary>
    private bool _replayMode;

    /// <summary>
    /// Issue #329 PR-5: snapshot of the audit sink's persisted durability
    /// watermark, captured BEFORE WAL replay rebuilds engine state.
    /// During replay (and only during replay), <see cref="ChannelDispatcher.OnTrade"/>
    /// skips <c>_postTradeSink.OnTrade</c> for any trade whose owning
    /// command's seq is &lt;= this value — those trades are already
    /// fsync'd to the audit log, so re-emitting them would produce
    /// duplicates. Trades from commands with seq strictly greater than
    /// this watermark fall through to the sink as normal, re-recording
    /// any tail lost between the last audit fsync and the crash.
    /// Reset to <see cref="long.MaxValue"/> outside replay so the gate
    /// has no effect on the live path (the comparison <c>_lastAppliedSeq
    /// &lt;= MaxValue</c> is short-circuited by the <c>_replayMode</c>
    /// check at the call site).
    /// </summary>
    private long _bootAuditDurableSeq = long.MaxValue;

    /// <summary>
    /// Issue #312: snapshot of the durability barrier the outbound ER
    /// for the currently-dispatching command must wait on. Captured
    /// after <see cref="WalAppendIfEnabled"/> stamps <c>_lastAppliedSeq</c>
    /// so every ER triggered by the same command awaits the SAME WAL
    /// seq, regardless of how many engine events fire. Returns
    /// <see cref="DurabilityHandle.None"/> when WAL is disabled or
    /// during replay so the send loop fast-paths through.
    /// </summary>
    private DurabilityHandle CurrentDurability =>
        (_wal is null || _replayMode) ? DurabilityHandle.None
            : new DurabilityHandle(_wal, _lastAppliedSeq);

    private static readonly IUmdfPacketSink NoOpPacketSink = new NoOpPacketSinkImpl();
    private static readonly ICoreOutbound NoOpOutbound = new NoOpCoreOutboundImpl();
    /// <summary>
    /// Optional state persister (issue #260). When non-null, the dispatcher
    /// loads any persisted snapshot at loop entry and writes a fresh
    /// snapshot after every command flush. <c>null</c> ⇒ stateless boot
    /// (legacy behaviour).
    /// </summary>
    private readonly IChannelStatePersister? _persister;
    private readonly SnapshotThrottlePolicy _snapshotThrottle;
    private readonly BackgroundSnapshotWriter? _asyncSnapshotWriter;
    /// <summary>
    /// Issue #270: predicate the restore path uses to detect orphan
    /// <see cref="OrderOwnerSnapshot.SessionValue"/> entries — owners
    /// whose original session is no longer present in the host's
    /// session/firm registry. <c>null</c> ⇒ no check (legacy
    /// behaviour, all owners restored).
    /// </summary>
    private readonly Func<string, bool>? _sessionExists;
    private readonly OrphanSessionPolicy _orphanPolicy;
    // Per-channel post-trade audit sink (#329 PR-1). Defaults to
    // NullPostTradeSink.Instance so existing callers see no behaviour
    // change; subsequent PRs install a real append-only writer.
    private readonly B3.Exchange.PostTrade.IPostTradeSink _postTradeSink;
    private readonly string? _auditRootDir;
    private readonly B3.Exchange.PostTrade.BustDedupIndex? _bustDedup;
    private readonly string? _dropRootDir;
    private readonly B3.Exchange.PostTrade.IAmendmentsPublisher? _amendmentsPublisher;
    /// <summary>
    /// ADR 0008 §3 routing race-window closure: shared lock between
    /// the dispatch thread (which checks fills.csv.done existence and
    /// writes the bust record) and the EOD exporter (which scans the
    /// audit log for cancelled fills then renames its .done sidecar).
    /// Exposed so <see cref="ExchangeHost.TriggerEodExport"/> can take
    /// the same monitor across the scan→publish window.
    /// </summary>
    public object PostTradeRoutingLock { get; } = new();
    // Throttle bookkeeping (issue #267). Mutated only on the dispatch
    // loop thread → no Interlocked needed.
    private long _commandsSincePersist;
    private long _lastPersistUnixMs;
    private bool _pendingDirty;

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

    /// <summary>
    /// When non-null, the dispatcher is processing the sweep phase of a
    /// <see cref="WorkKind.Cross"/> with <c>CrossType=AgainstBook</c>: each
    /// <see cref="OnTrade"/> on this aggressor's side accrues into the
    /// captured value so the loop can compute the residual qty for the
    /// internal print phase. Issue #218 (Onda L · L5).
    /// </summary>
    private long? _crossSweepFilledQty;

    private SnapshotRotator? _snapshotRotator;

    /// <summary>
    /// Issue #319: outermost-command aggressor cumulative tracking. Set at
    /// the start of each engine submit (<c>WorkKind.New</c> /
    /// <c>WorkKind.Cross</c> legs) by <see cref="BeginAggressor"/> and
    /// reset in the <c>finally</c> of <c>ProcessOne</c>. Used by
    /// <c>OnTrade</c> to emit monotonically-cumulative
    /// <c>cumQty</c>/<c>leavesQty</c> on <c>ER_Trade</c> for the
    /// aggressor side; for the resting side the per-order tracking lives
    /// on <see cref="OrderRegistry"/> instead.
    /// </summary>
    private long _aggressorOrigQty;
    private long _aggressorCumQty;

    /// <summary>
    /// Issue #321: per-securityId snapshot of the most-recently observed
    /// trading phase. Written only on the dispatch loop thread inside
    /// <c>OnTradingPhaseChanged</c>; seeded in the ctor from the engine's
    /// initial phase map for each <c>seedSecurityIds</c> entry. Read from
    /// any thread by <see cref="TryGetPhaseSnapshot"/> so the HTTP admin
    /// endpoint can decide between SetPhase / UncrossAuction without
    /// queueing a synchronous query into the engine.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, B3.Exchange.Matching.TradingPhase> _phaseSnapshot
        = new();

    /// <summary>
    /// Issue #322: per-securityId snapshot of the most-recently observed
    /// administrative halt overlay. Written only on the dispatch loop
    /// thread inside <see cref="ProcessHalt"/> / <see cref="ProcessResume"/>;
    /// re-seeded by <see cref="RestoreChannelState"/>. Read from any
    /// thread by <see cref="TryGetHaltSnapshot"/> so the HTTP admin
    /// endpoint can resolve the current halt state without queueing a
    /// synchronous query into the engine.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, HaltSnapshot> _haltSnapshot
        = new();

    /// <summary>
    /// Issue #321: latest <see cref="AuctionPrintInfo"/> captured by the
    /// <c>OnAuctionPrint</c> sink for the command currently being
    /// processed. Reset to <c>null</c> at the start of each Process*
    /// method so a subsequent uncross with no print yields
    /// <c>UncrossPrint = null</c> in the outcome.
    /// </summary>
    private AuctionPrintInfo? _pendingAuctionPrint;

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

    /// <summary>
    /// Issue #286: <c>false</c> once the channel has refused a WAL
    /// append under <see cref="WalAppendFailurePolicy.Halt"/>. The
    /// host's WAL readiness probe AND the producer-side
    /// <c>Enqueue*</c> short-circuit consume this flag; both consumers
    /// run off the dispatch thread, hence the
    /// <see cref="Volatile.Read{T}"/>.
    /// </summary>
    public bool IsWalHealthy => Volatile.Read(ref _walHalted) == 0;

    public ChannelDispatcher(byte channelNumber, Func<IMatchingEventSink, MatchingEngine> engineFactory, IUmdfPacketSink packetSink,
        ICoreOutbound outbound,
        ILogger<ChannelDispatcher> logger,
        Func<ulong>? nowNanos = null, ushort tradeDate = 0, int inboundCapacity = DefaultInboundCapacity,
        ChannelMetrics? metrics = null,
        BoundedSessionFirmCounters? sessionFirmCounters = null,
        UmdfPacketRetransmitBuffer? retxBuffer = null,
        IChannelStatePersister? persister = null,
        SnapshotThrottlePolicy? snapshotThrottle = null,
        bool useAsyncSnapshotWriter = false,
        IChannelWriteAheadLog? wal = null,
        WalAppendFailurePolicy walAppendFailurePolicy = WalAppendFailurePolicy.Continue,
        Func<string, bool>? sessionExists = null,
        OrphanSessionPolicy orphanPolicy = OrphanSessionPolicy.Drop,
        IReadOnlyList<long>? seedSecurityIds = null,
        B3.Exchange.PostTrade.IPostTradeSink? postTradeSink = null,
        string? auditRootDir = null,
        B3.Exchange.PostTrade.BustDedupIndex? bustDedup = null,
        string? dropRootDir = null,
        B3.Exchange.PostTrade.IAmendmentsPublisher? amendmentsPublisher = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outbound);
        ChannelNumber = channelNumber;
        _liveSink = packetSink;
        _liveOutbound = outbound;
        _packetSink = packetSink;
        _outbound = outbound;
        _logger = logger;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _tradeDate = tradeDate;
        _metrics = metrics;
        _sessionFirmCounters = sessionFirmCounters;
        _retxBuffer = retxBuffer;
        _persister = persister;
        _wal = wal;
        _walAppendFailurePolicy = walAppendFailurePolicy;
        _sessionExists = sessionExists;
        _orphanPolicy = orphanPolicy;
        _postTradeSink = postTradeSink ?? B3.Exchange.PostTrade.NullPostTradeSink.Instance;
        _auditRootDir = auditRootDir;
        _bustDedup = bustDedup;
        _dropRootDir = dropRootDir;
        _amendmentsPublisher = amendmentsPublisher;
        _snapshotThrottle = snapshotThrottle ?? SnapshotThrottlePolicy.AlwaysPersist;
        // Issue #268: opt-in async snapshot writer. Off by default so
        // pre-existing deployments keep the synchronous in-loop persist
        // (zero-RPO). Enabled per channel via host config.
        // Issue #269: when WAL is also enabled, the writer notifies us
        // via the post-save callback so we can truncate the WAL on the
        // writer thread — guaranteeing the WAL is only ever truncated
        // after the matching snapshot has reached the disk.
        _asyncSnapshotWriter = (useAsyncSnapshotWriter && persister is not null)
            ? new BackgroundSnapshotWriter(channelNumber, persister, logger, metrics,
                onSaved: _wal is null ? null : OnAsyncSnapshotSaved)
            : null;
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
        if (seedSecurityIds is not null)
        {
            foreach (var secId in seedSecurityIds)
            {
                try { _phaseSnapshot[secId] = _engine.GetTradingPhase(secId); }
                catch (KeyNotFoundException) { /* engine doesn't know this id; skip */ }
            }
        }
    }

    /// <summary>
    /// Issue #321: thread-safe snapshot of the most-recently observed
    /// trading phase for <paramref name="securityId"/>. Returns
    /// <c>true</c> with the phase out value if the dispatcher has ever
    /// observed (or been seeded with) a phase for the security; returns
    /// <c>false</c> when the security is unknown to this channel. Safe
    /// to call from any thread.
    /// </summary>
    public bool TryGetPhaseSnapshot(long securityId, out B3.Exchange.Matching.TradingPhase phase)
        => _phaseSnapshot.TryGetValue(securityId, out phase);

    /// <summary>
    /// Issue #322: thread-safe snapshot of the most-recent administrative
    /// halt state for <paramref name="securityId"/>. Returns <c>true</c>
    /// when the instrument is currently halted; <c>false</c> when it is
    /// either not halted or unknown to this channel. Safe to call from
    /// any thread.
    /// </summary>
    public bool TryGetHaltSnapshot(long securityId, out HaltSnapshot state)
        => _haltSnapshot.TryGetValue(securityId, out state);

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
}
