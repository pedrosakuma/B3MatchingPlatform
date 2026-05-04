using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using Side = B3.Exchange.Matching.Side;

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
/// <para><b>Threading contract (issue #138):</b></para>
/// <list type="bullet">
/// <item><description><b>Producers</b> (any thread) call only the
/// <c>Enqueue*</c> / <see cref="OnDecodeError"/> / <see cref="OnSessionClosed"/>
/// methods, which post a <c>WorkItem</c> to the bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> and return. No engine
/// or buffer state is mutated from the producer thread.</description></item>
/// <item><description><b>The dispatch loop thread</b> (the single
/// <see cref="Thread"/> spun up by <see cref="Start"/>) is the sole mutator of
/// engine state, the packet buffer, the <c>_clOrdId</c> index, and the
/// <see cref="SequenceNumber"/> / <see cref="SequenceVersion"/> counters.
/// Every mutation path asserts <c>Thread.CurrentThread == _loopThread</c>
/// in DEBUG builds via <see cref="AssertOnLoopThread"/>.</description></item>
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
    /// the start of <see cref="ProcessOne"/> and reset to
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

    public void Start()
    {
        _logger.LogInformation("channel {ChannelNumber} dispatcher starting", ChannelNumber);
        _loopTask = Task.Factory.StartNew(() => RunLoopAsync(_cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Capture the dispatch-loop thread identity on entry so
        // AssertOnLoopThread() in mutation paths can enforce the
        // single-writer invariant in DEBUG builds (issue #138).
        _loopThread = Thread.CurrentThread;
        // Issue #169: bind the matching engine eagerly so any off-thread
        // engine call is caught on the very first invocation, not just
        // after the first in-thread call has latched the owner.
        _engine.BindToDispatchThread(_loopThread);

        // Heartbeat is recorded on every loop wakeup (whether triggered by
        // new work or by the periodic timeout) so a stuck/dead dispatch
        // thread is detected by /health/live within HeartbeatInterval +
        // probe threshold.
        try
        {
            var reader = _inbound.Reader;
            while (!ct.IsCancellationRequested)
            {
                RecordHeartbeat();
                Task<bool> waitTask = reader.WaitToReadAsync(ct).AsTask();
                bool more;
                try
                {
                    more = await waitTask.WaitAsync(HeartbeatInterval, ct)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Timeout: loop and re-record the heartbeat on next iteration.
                    continue;
                }
                catch (OperationCanceledException) { return; }

                if (!more) return; // channel completed
                while (reader.TryRead(out var item))
                {
                    // Issue #170: contain unhandled exceptions per work-item
                    // so a single bad command (engine bug, sink throw, etc.)
                    // cannot kill the dispatch loop and silence the entire
                    // channel. We log with full context and bump
                    // exch_dispatcher_crash_total; the loop continues so
                    // healthy commands still get serviced. Cancellation
                    // bypasses containment because it is the cooperative
                    // shutdown path — let it bubble to the outer handler.
                    try
                    {
                        ProcessOne(item);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _metrics?.IncDispatcherCrashes();
                        LogDispatcherCrash(ex, ChannelNumber, item.Kind,
                            item.HasSession ? item.Session.ToString() : "(no-session)",
                            item.Firm, item.ClOrdId);
                    }
                    RecordHeartbeat();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber} dispatch loop terminated unexpectedly", ChannelNumber);
        }
    }

    private void RecordHeartbeat()
        => _metrics?.RecordTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// Asserts the calling thread is the dispatch-loop thread. Compiled out
    /// in Release builds. Guards every mutation path — engine state, packet
    /// buffer, sequence counters, and the per-session order-id index — to
    /// catch any future producer-thread regression at test time (issue #138).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertOnLoopThread()
    {
        var t = _loopThread;
        // _loopThread is null only before Start() (e.g. unit tests calling
        // ProcessOne directly on the test thread). Allow that case.
        System.Diagnostics.Debug.Assert(
            t == null || Thread.CurrentThread == t,
            $"ChannelDispatcher mutation off the dispatch loop thread "
            + $"(channel={ChannelNumber}, expected={t?.ManagedThreadId}, "
            + $"actual={Thread.CurrentThread.ManagedThreadId})");
    }

    private void ProcessBumpVersion()
    {
        // Atomic operator-initiated channel reset (issue #6). Order matters:
        //   1. Wipe per-instrument books and reset RptSeq on the engine.
        //   2. Bump incremental SequenceVersion + reset SequenceNumber.
        //   3. Bump snapshot rotator's SequenceVersion (if attached).
        //   4. Emit one ChannelReset_11 frame, flushed as a single-message
        //      packet under the NEW SequenceVersion. Because we just reset
        //      SequenceNumber to 0, FlushPacket() stamps SequenceNumber=1.
        // Anything that races with this would violate the dispatch-thread
        // invariant — ProcessOne is the sole caller and is invoked only
        // from the dispatch loop.
        AssertOnLoopThread();
        _engine.ResetForChannelReset();
        Volatile.Write(ref _sequenceVersion, (ushort)(_sequenceVersion + 1));
        Volatile.Write(ref _sequenceNumber, 0u);
        _snapshotRotator?.BumpSequenceVersion();

        // Force-write the ChannelReset_11 frame using the standard
        // ReserveOrFlush/Commit path so the packet header reflects the
        // NEW SequenceVersion.
        _packetWritten = 0;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.ChannelResetBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteChannelResetFrame(dst, _nowNanos());
        Commit(n);
        FlushPacket();
    }

    /// <summary>
    /// Operator-triggered trade-bust replay (issue #15). Synthesises a
    /// <c>TradeBust_57</c> frame on the incremental channel using the
    /// next available <see cref="MatchingEngine.AllocateNextRptSeq"/>.
    /// The bust is flushed as a single-message packet under the current
    /// SequenceVersion. No engine state is mutated — the matching engine
    /// is unaware that a previously-emitted trade has been busted.
    /// </summary>
    private void ProcessTradeBust(OperatorTradeBust bust)
    {
        AssertOnLoopThread();
        uint rptSeq = _engine.AllocateNextRptSeq();
        _packetWritten = 0;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.TradeBustBlockLength);
        int written = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTradeBustFrame(dst,
            bust.SecurityId, bust.PriceMantissa, bust.Size, bust.TradeId, bust.TradeDate,
            _nowNanos(), rptSeq);
        Commit(written);
        FlushPacket();
    }

    internal void ProcessOne(in WorkItem item)
    {
        AssertOnLoopThread();
        // Issue #173: dispatch_wait = enqueue → loop pickup. EnqueueTicks
        // is 0 for in-process synthetic items (e.g. snapshot tick scheduled
        // via Timer); skip the observation in that case to avoid skewing
        // the histogram with 1970-style "ages".
        long pickupTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (item.EnqueueTicks > 0 && _metrics != null)
            _metrics.DispatchWait.ObserveTicks(pickupTicks - item.EnqueueTicks);

        if (item.Kind == WorkKind.SnapshotRotation || item.Kind == WorkKind.OperatorSnapshotNow)
        {
            // Snapshot ticks bypass the per-command incremental packet buffer
            // entirely — they have their own sink + sequence space owned by
            // the rotator and emit one or more complete packets directly.
            _snapshotRotator?.PublishNext();
            return;
        }

        if (item.Kind == WorkKind.OperatorBumpVersion)
        {
            ProcessBumpVersion();
            return;
        }

        if (item.Kind == WorkKind.OperatorTradeBust)
        {
            ProcessTradeBust(item.TradeBust!);
            return;
        }

        _currentSession = item.Session;
        _currentFirm = item.Firm;
        _hasCurrentSession = item.HasSession;
        _currentClOrdId = item.ClOrdId;
        _currentOrigClOrdId = item.OrigClOrdId;
        _currentReceivedTimeNanos = item.Kind switch
        {
            WorkKind.New => item.NewOrder?.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.Cancel => item.Cancel?.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.Replace => item.Replace?.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.Cross => item.Cross?.Buy.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.MassCancel => item.MassCancel?.EnteredAtNanos ?? ulong.MaxValue,
            _ => ulong.MaxValue,
        };
        _packetWritten = 0;
        bool succeeded = false;
        long engineStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Issue #175: open engine.process as a child of the dispatch.enqueue
        // span captured at enqueue time. The dispatch loop crosses thread
        // boundaries from the gateway IO thread, so propagation must be
        // explicit — Activity.Current would not carry the context here.
        using var engineSpan = ExchangeTelemetry.Source.StartActivity(
            ExchangeTelemetry.SpanEngineProcess,
            System.Diagnostics.ActivityKind.Internal,
            item.ParentContext);
        if (engineSpan is not null)
        {
            engineSpan.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
            engineSpan.SetTag(ExchangeTelemetry.TagWorkKind, item.Kind.ToString());
            if (item.HasSession) engineSpan.SetTag(ExchangeTelemetry.TagSession, item.Session.Value);
            if (item.ClOrdId != 0) engineSpan.SetTag(ExchangeTelemetry.TagClOrdId, (long)item.ClOrdId);
        }
        try
        {
            switch (item.Kind)
            {
                case WorkKind.New: _metrics?.IncOrdersIn(); _engine.Submit(item.NewOrder!); break;
                case WorkKind.Cancel:
                    {
                        _metrics?.IncOrdersIn();
                        var cancel = item.Cancel!;
                        if (cancel.OrderId == 0)
                        {
                            // HostRouter is expected to pre-resolve OrigClOrdID
                            // → OrderId via the Gateway-side OrderOwnershipMap.
                            // Defensive guard: surface a deterministic reject if
                            // an unresolved command still reaches the engine.
                            EmitUnknownOrderIdReject(cancel.ClOrdId, cancel.SecurityId, cancel.EnteredAtNanos);
                            break;
                        }
                        _engine.Cancel(cancel);
                        break;
                    }
                case WorkKind.Replace:
                    {
                        _metrics?.IncOrdersIn();
                        var replace = item.Replace!;
                        if (replace.OrderId == 0)
                        {
                            EmitUnknownOrderIdReject(replace.ClOrdId, replace.SecurityId, replace.EnteredAtNanos);
                            break;
                        }
                        _engine.Replace(replace);
                        break;
                    }
                case WorkKind.Cross:
                    {
                        // Atomic two-leg submission. Both legs share the
                        // same _currentReceivedTimeNanos so receivedTime on
                        // each ER frame matches the original cross frame's
                        // ingress timestamp. _currentClOrdId is rebound
                        // before each leg so ER routing uses the correct
                        // per-leg ClOrdID. The packet buffer accumulates
                        // both legs' UMDF events and flushes once at the
                        // end of this dispatch turn.
                        var cross = item.Cross!;
                        _metrics?.IncOrdersIn();
                        _currentClOrdId = cross.BuyClOrdIdValue;
                        _engine.Submit(cross.Buy);
                        _metrics?.IncOrdersIn();
                        _currentClOrdId = cross.SellClOrdIdValue;
                        _engine.Submit(cross.Sell);
                        break;
                    }
                case WorkKind.MassCancel:
                    {
                        // OrderIds are pre-resolved by HostRouter against
                        // the Gateway-side OrderOwnershipMap (filter is
                        // session/firm/Side/SecurityId; spec §4.8 / #GAP-19).
                        // Engine sees only a flat orderId list and emits one
                        // ER_Cancel per matching order via OnOrderCanceled,
                        // routed back to the originating session by the
                        // Gateway router.
                        var mc = item.MassCancel!;
                        if (mc.OrderIds.Count > 0)
                            _engine.MassCancel(mc.OrderIds, mc.EnteredAtNanos);
                        break;
                    }
                case WorkKind.DecodeError:
                    if (_hasCurrentSession)
                    {
                        _outbound.WriteExecutionReportReject(_currentSession,
                            new RejectEvent(_currentClOrdId.ToString(), 0, 0, RejectReason.UnknownInstrument, _nowNanos()),
                            _currentClOrdId);
                        _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
                    }
                    break;
            }
            LogCommandProcessed(ChannelNumber, item.Kind, _currentClOrdId);
            succeeded = true;
        }
        finally
        {
            // Issue #173: engine_process = engine entry → engine exit
            // (regardless of crash/success). outbound_emit measured
            // separately around FlushPacket so the two phases are
            // distinguishable in the histogram.
            long engineEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_metrics != null)
                _metrics.EngineProcess.ObserveTicks(engineEnd - engineStart);
            // Issue #170: do not publish a half-built UMDF packet if the
            // command crashed mid-flight — the dispatcher loop will catch
            // the exception, count the crash, and move on; flushing
            // partial state would corrupt downstream consumers.
            if (succeeded)
            {
                long flushStart = engineEnd;
                using (var flushSpan = ExchangeTelemetry.Source.StartActivity(
                    ExchangeTelemetry.SpanOutboundEmit,
                    System.Diagnostics.ActivityKind.Producer))
                {
                    if (flushSpan is not null)
                        flushSpan.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
                    int bytes = _packetWritten;
                    FlushPacket();
                    if (flushSpan is not null)
                        flushSpan.SetTag(ExchangeTelemetry.TagBytes, bytes);
                }
                if (_metrics != null)
                    _metrics.OutboundEmit.ObserveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - flushStart);
            }
            else
            {
                _packetWritten = 0;
            }
            _currentSession = default;
            _currentFirm = 0;
            _hasCurrentSession = false;
            _currentClOrdId = 0;
            _currentOrigClOrdId = 0;
            _currentReceivedTimeNanos = ulong.MaxValue;
        }
    }

    private void EmitUnknownOrderIdReject(string clOrdId, long securityId, ulong nowNanos)
    {
        // Surface the failure directly as an ER_Reject so the client gets a
        // clear, deterministic response instead of a silently dropped command.
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportReject(_currentSession,
                new RejectEvent(clOrdId, securityId, 0, RejectReason.UnknownOrderId, nowNanos),
                _currentClOrdId);
            _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
        }
    }

    private void FlushPacket()
    {
        if (_packetWritten == 0) return;
        // Patch packet header with the live seq number + send time.
        //
        // SequenceNumber wraparound (uint, ~4.29 billion packets):
        //   At 100 packets/sec → ~1.36 years before overflow.
        //   At 10 000 packets/sec → ~5 days.
        // On overflow we bump SequenceVersion (B3 UMDF field intended for
        // exactly this kind of restart / rollover signal) and reset the
        // counter. Downstream consumers treat a new SequenceVersion as a
        // discontinuity and resync from snapshot — same code path they use
        // for a host restart.
        AssertOnLoopThread();
        if (_sequenceNumber == uint.MaxValue)
        {
            Volatile.Write(ref _sequenceVersion, (ushort)(_sequenceVersion + 1));
            Volatile.Write(ref _sequenceNumber, 0u);
            // The packet buffer's SequenceVersion was written by
            // ReserveOrFlush with the pre-bump value; rewrite it now so the
            // on-wire header matches the new (version, seq) tuple.
            ushort newVer = _sequenceVersion;
            System.Runtime.InteropServices.MemoryMarshal.Write(
                _packetBuf.AsSpan(B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSequenceVersionOffset, 2),
                in newVer);
        }
        Volatile.Write(ref _sequenceNumber, _sequenceNumber + 1);
        ulong now = _nowNanos();
        B3.Umdf.WireEncoder.UmdfWireEncoder.PatchPacketHeader(
            _packetBuf.AsSpan(0, B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize), _sequenceNumber, now);
        _packetSink.Publish(ChannelNumber, _packetBuf.AsSpan(0, _packetWritten));
        LogPacketFlushed(ChannelNumber, _sequenceNumber, _packetWritten);
        _metrics?.IncPacketsOut();
        // Issue #174: per-feed packet/byte throughput. The incremental feed
        // is published from the dispatcher's command-loop FlushPacket; the
        // snapshot/instrumentdef feeds account for themselves via the
        // CountingUdpPacketSinkDecorator wired in the host.
        _metrics?.IncUmdfPacket(UmdfFeedKind.Incremental, _packetWritten);
        _packetWritten = 0;
    }

    /// <summary>Test seam: fast-forward <see cref="SequenceNumber"/> close
    /// to <c>uint.MaxValue</c> to exercise the wraparound path without
    /// publishing billions of packets. Must be called before any work is
    /// processed (i.e. before <see cref="Start"/>) — there is no
    /// thread-safety contract beyond "called from the test thread on a
    /// quiescent dispatcher".</summary>
    internal void TestSetSequenceNumber(uint value) => Volatile.Write(ref _sequenceNumber, value);

    private Span<byte> ReserveOrFlush(int frameSize)
    {
        if (_packetWritten == 0)
        {
            // Reserve packet header up front; SequenceNumber + sendingTime
            // patched at flush.
            B3.Umdf.WireEncoder.UmdfWireEncoder.WritePacketHeader(_packetBuf,
                ChannelNumber, SequenceVersion, sequenceNumber: 0, sendingTimeNanos: 0);
            _packetWritten = B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize;
        }
        if (_packetWritten + frameSize > MaxPacketBytes)
        {
            FlushPacket();
            B3.Umdf.WireEncoder.UmdfWireEncoder.WritePacketHeader(_packetBuf,
                ChannelNumber, SequenceVersion, sequenceNumber: 0, sendingTimeNanos: 0);
            _packetWritten = B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize;
        }
        return _packetBuf.AsSpan(_packetWritten);
    }

    private void Commit(int written) => _packetWritten += written;

    // ====== IInboundCommandSink ======

    /// <summary>
    /// Issue #175: starts the <c>dispatch.enqueue</c> span. Returns
    /// <c>null</c> when no listener is subscribed (zero-overhead path so
    /// uninstrumented hosts pay nothing). Tags carry enough context for
    /// trace consumers to correlate with metrics & logs without joining
    /// against another store.
    /// </summary>
    private System.Diagnostics.Activity? StartEnqueueSpan(
        WorkKind kind, SessionId session, uint enteringFirm, ulong clOrdId, long securityId)
    {
        var act = ExchangeTelemetry.Source.StartActivity(
            ExchangeTelemetry.SpanDispatchEnqueue,
            System.Diagnostics.ActivityKind.Producer);
        if (act is null) return null;
        act.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
        act.SetTag(ExchangeTelemetry.TagSession, session.Value);
        act.SetTag(ExchangeTelemetry.TagFirm, (long)enteringFirm);
        act.SetTag(ExchangeTelemetry.TagWorkKind, kind.ToString());
        if (clOrdId != 0) act.SetTag(ExchangeTelemetry.TagClOrdId, (long)clOrdId);
        if (securityId != 0) act.SetTag(ExchangeTelemetry.TagSecurityId, securityId);
        return act;
    }

    public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue)
    {
        using var act = StartEnqueueSpan(WorkKind.New, session, enteringFirm, clOrdIdValue, cmd.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.New, session, enteringFirm, true,
            clOrdIdValue, 0, cmd, null, null, null,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.New); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.New);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        using var act = StartEnqueueSpan(WorkKind.Cancel, session, enteringFirm, clOrdIdValue, cmd.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.Cancel, session, enteringFirm, true,
            clOrdIdValue, origClOrdIdValue, null, cmd, null, null,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.Cancel); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.Cancel);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        using var act = StartEnqueueSpan(WorkKind.Replace, session, enteringFirm, clOrdIdValue, cmd.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.Replace, session, enteringFirm, true,
            clOrdIdValue, origClOrdIdValue, null, null, cmd, null,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.Replace); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.Replace);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm)
    {
        using var act = StartEnqueueSpan(WorkKind.Cross, session, enteringFirm, cmd.BuyClOrdIdValue, cmd.Buy.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.Cross, session, enteringFirm, true,
            cmd.BuyClOrdIdValue, cmd.SellClOrdIdValue, null, null, null, cmd,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.Cross); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.Cross);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm)
    {
        // OrderId resolution + filtering is the HostRouter's job (it owns
        // access to the Gateway-side OrderOwnershipMap). Forwarding the
        // pre-filter form here would require Core to track ownership again,
        // which is exactly what #66 set out to remove. Surface the misuse
        // instead of silently dropping.
        throw new InvalidOperationException(
            "Mass-cancel must be pre-resolved by the gateway router; call EnqueueResolvedMassCancel(orderIds) instead.");
    }

    /// <summary>
    /// Channel-local mass-cancel entry point used by <c>HostRouter</c>
    /// after it has resolved the spec §4.8 filter
    /// (session/firm/Side/SecurityId) against the Gateway-side
    /// <c>OrderOwnershipMap</c> and grouped the resulting orderIds by
    /// channel. The dispatcher submits the list as a single
    /// <c>MatchingEngine.MassCancel</c> call so all DEL frames pack into
    /// one UMDF packet (atomic from the consumer's POV) and one
    /// <c>ER_Cancel</c> per order is routed back to the originating session
    /// via <c>OnOrderCanceled</c>.
    /// </summary>
    public bool EnqueueResolvedMassCancel(IReadOnlyList<long> orderIds, SessionId session, uint enteringFirm,
        ulong enteredAtNanos)
    {
        if (orderIds == null || orderIds.Count == 0) return true;
        var mc = new ResolvedMassCancel(orderIds, enteredAtNanos);
        using var act = StartEnqueueSpan(WorkKind.MassCancel, session, enteringFirm, 0, securityId: 0);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.MassCancel, session, enteringFirm, true,
            0, 0, null, null, null, null, mc,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.MassCancel); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.MassCancel);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public void OnDecodeError(SessionId session, string error)
    {
        _logger.LogWarning("channel {ChannelNumber} inbound decode error: {Error}", ChannelNumber, error);
        _metrics?.IncDecodeErrors();
        _metrics?.IncInboundMessage(InboundMessageKind.DecodeError);
        if (!_inbound.Writer.TryWrite(new WorkItem(WorkKind.DecodeError, session, 0, true,
            0, 0, null, null, null, null, EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp())))
        { _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.DecodeError); }
    }

    /// <summary>
    /// Attaches a <see cref="SnapshotRotator"/> to this dispatcher. May only
    /// be called once. After this returns, any caller (typically a
    /// <see cref="System.Threading.Timer"/>) may invoke
    /// <see cref="EnqueueSnapshotTick"/> to schedule a snapshot publish on
    /// the dispatch thread.
    /// </summary>
    public void AttachSnapshotRotator(SnapshotRotator rotator)
    {
        ArgumentNullException.ThrowIfNull(rotator);
        if (_snapshotRotator != null)
            throw new InvalidOperationException("snapshot rotator already attached");
        _snapshotRotator = rotator;
    }

    /// <summary>
    /// Posts a snapshot tick into the inbound queue. Returns <c>false</c> if
    /// the queue is full (snapshots are idempotent — losing a tick simply
    /// defers the next refresh by one period). Safe to call from any thread.
    /// </summary>
    public bool EnqueueSnapshotTick()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.SnapshotRotation, default, 0, false,
            0, 0, null, null, null, null));

    public void OnSessionClosed(SessionId session)
    {
        // Per-session order ownership lives in the Gateway-side
        // OrderOwnershipMap; the dispatcher holds no per-session state to
        // release. The Gateway listener invokes
        // OrderOwnershipMap.EvictSession(session) directly on transport
        // close, so this method is a no-op for the Core side.
    }

    /// <summary>
    /// Operator command (issue #6): forces an immediate snapshot publish on
    /// the next available dispatcher cycle. Identical wire effect to a
    /// <see cref="EnqueueSnapshotTick"/>; the distinct work-kind exists so
    /// future operator commands can be metered/logged independently of the
    /// scheduled cadence ticks. Returns <c>false</c> if the inbound queue
    /// is full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorSnapshotNow()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorSnapshotNow, default, 0, false,
            0, 0, null, null, null, null));

    /// <summary>
    /// Operator command (issue #6): atomically (a) bumps the incremental
    /// channel's <see cref="SequenceVersion"/> + the attached snapshot
    /// rotator's <c>SequenceVersion</c>, (b) clears every per-instrument
    /// order book, (c) resets the engine's <c>RptSeq</c> counter to 0, and
    /// (d) emits a single <c>ChannelReset_11</c> frame on the incremental
    /// channel under the NEW <see cref="SequenceVersion"/>. The next
    /// snapshot publish (whether scheduled or operator-forced) will reflect
    /// the empty book stamped with the new versions. Returns <c>false</c>
    /// if the inbound queue is full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorBumpVersion()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorBumpVersion, default, 0, false,
            0, 0, null, null, null, null));

    /// <summary>
    /// Operator command (issue #15): publishes a <c>TradeBust_57</c> frame
    /// for a previously-emitted trade identified by
    /// (<paramref name="securityId"/>, <paramref name="tradeId"/>). The
    /// price/size/date echo fields are caller-supplied — the simulator
    /// does not retain a per-trade audit log. The bust frame is stamped
    /// with the next available <c>RptSeq</c> from the channel's matching
    /// engine and emitted under the current <c>SequenceVersion</c>.
    /// Returns <c>false</c> if the inbound queue is full. Safe to call
    /// from any thread.
    /// </summary>
    public bool EnqueueOperatorTradeBust(long securityId, long priceMantissa, long size,
        uint tradeId, ushort tradeDate)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorTradeBust, default, 0, false,
            0, 0, null, null, null, null,
            TradeBust: new OperatorTradeBust(securityId, priceMantissa, size, tradeId, tradeDate)));

    // ====== IMatchingEventSink ======

    public void OnOrderAccepted(in OrderAcceptedEvent e)
    {
        AssertOnLoopThread();
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.RemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
        Commit(n);

        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportNew(_currentSession, _currentFirm, _currentClOrdId, e, _currentReceivedTimeNanos);
            _metrics?.IncExecutionReport(ExecutionReportKind.New);
        }
    }

    public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e)
    {
        AssertOnLoopThread();
        // Update on the wire = OrderAdded with action UPDATE (0x01).
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.NewRemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
        // Patch MdUpdateAction byte from NEW(0x00) to UPDATE(0x01).
        int actionOffset = B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBodyMdUpdateActionOffset;
        dst[actionOffset] = 0x01; // MDUpdateAction.CHANGE
        Commit(n);
    }

    public void OnOrderCanceled(in OrderCanceledEvent e)
    {
        AssertOnLoopThread();
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.RemainingQuantityAtCancel, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(n);

        // Gateway resolves OrderId → owning session via OrderOwnershipMap
        // and evicts the entry. Pass the active session's ClOrdId (if any)
        // so the wire ER carries the requester's id while OrigClOrdID
        // points to the owner's original ClOrdID.
        _outbound.WriteExecutionReportPassiveCancel(e.OrderId, e, _currentClOrdId, _currentReceivedTimeNanos);
        _metrics?.IncExecutionReport(ExecutionReportKind.CancelPassive);
    }

    public void OnOrderFilled(in OrderFilledEvent e)
    {
        AssertOnLoopThread();
        // Fully consumed by trades — emit DeleteOrder; the per-fill ER_Trade
        // events were already dispatched via OnTrade.
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.FinalFilledQuantity, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(n);

        // Tell the Gateway to release the ownership entry — no wire ER here
        // (the per-trade ER_Trade frames have already covered the fills).
        _outbound.NotifyOrderTerminal(e.OrderId);
    }

    public void OnTrade(in TradeEvent e)
    {
        AssertOnLoopThread();
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.TradeBlockLength);
        bool aggressorIsBuy = e.AggressorSide == Side.Buy;
        uint buyer = aggressorIsBuy ? e.AggressorFirm : e.RestingFirm;
        uint seller = aggressorIsBuy ? e.RestingFirm : e.AggressorFirm;
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTradeFrame(dst,
            e.SecurityId, e.PriceMantissa, e.Quantity, e.TradeId, _tradeDate, e.TransactTimeNanos, e.RptSeq,
            buyerFirm: buyer, sellerFirm: seller);
        Commit(n);

        // ER_Trade for the aggressor side: routed to the active session by
        // SessionId. We do not maintain per-aggressor cum/leaves tracking
        // here; integration tests are scope-limited to single-fill scenarios.
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportTrade(_currentSession, e, isAggressor: true,
                ownerOrderId: e.AggressorOrderId, clOrdIdValue: _currentClOrdId,
                leavesQty: 0, cumQty: e.Quantity);
            _metrics?.IncExecutionReport(ExecutionReportKind.Trade);
        }
        // ER_Trade for the resting side: Gateway resolves owner via the
        // OrderOwnershipMap.
        _outbound.WriteExecutionReportPassiveTrade(e.RestingOrderId, e, leavesQty: 0, cumQty: e.Quantity);
        _metrics?.IncExecutionReport(ExecutionReportKind.TradePassive);
    }

    public void OnReject(in RejectEvent e)
    {
        AssertOnLoopThread();
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportReject(_currentSession, e, _currentClOrdId);
            _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
        }
    }

    // ====== shutdown ======

    /// <summary>
    /// Test hook: simulate a wedged dispatcher by cancelling the loop
    /// without disposing. Subsequent enqueues remain accepted by the
    /// channel but will never be processed, so liveness probes can verify
    /// that <c>/health/live</c> flips to 503 within the configured stale
    /// threshold.
    /// </summary>
    internal void KillForTesting()
    {
        try { _cts.Cancel(); } catch { }
    }

    /// <summary>
    /// Returns a test-only probe that exposes drain/kill helpers without
    /// requiring reflection or InternalsVisibleTo. The probe is the
    /// supported test seam — internal fields and methods are not part of
    /// the supported surface and can change without notice.
    /// </summary>
    public TestProbe CreateTestProbe() => new TestProbe(this);

    /// <summary>
    /// Test-only probe encapsulating drain/kill operations on a
    /// <see cref="ChannelDispatcher"/>. Production code must not call
    /// <see cref="CreateTestProbe"/>; the probe exists solely so tests
    /// can drive the dispatcher synchronously without piercing
    /// encapsulation via reflection.
    /// </summary>
    public sealed class TestProbe
    {
        private readonly ChannelDispatcher _disp;

        internal TestProbe(ChannelDispatcher disp) => _disp = disp;

        /// <summary>
        /// Synchronously processes every pending <c>WorkItem</c> on the
        /// dispatcher's inbound queue using the dispatcher's own
        /// <c>ProcessOne</c> path. The dispatcher loop is bypassed; the
        /// caller's thread does the work. Useful in unit tests that
        /// enqueue commands and need a deterministic point at which the
        /// engine has observed them.
        /// </summary>
        public void DrainInbound()
        {
            var reader = _disp._inbound.Reader;
            while (reader.TryRead(out var item))
            {
                _disp.ProcessOne(item);
            }
        }

        /// <summary>
        /// Cancels the dispatcher loop without disposing. See
        /// <see cref="ChannelDispatcher.KillForTesting"/>.
        /// </summary>
        public void Kill() => _disp.KillForTesting();
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("channel {ChannelNumber} dispatcher stopping (sequenceNumber={SequenceNumber})",
            ChannelNumber, SequenceNumber);
        _inbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        if (_loopTask != null) { try { await _loopTask.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }

    internal enum WorkKind : byte { New, Cancel, Replace, Cross, MassCancel, DecodeError, SnapshotRotation, OperatorSnapshotNow, OperatorBumpVersion, OperatorTradeBust }

    internal sealed record WorkItem(
        WorkKind Kind,
        SessionId Session,
        uint Firm,
        bool HasSession,
        ulong ClOrdId,
        ulong OrigClOrdId,
        NewOrderCommand? NewOrder,
        CancelOrderCommand? Cancel,
        ReplaceOrderCommand? Replace,
        CrossOrderCommand? Cross,
        ResolvedMassCancel? MassCancel = null,
        OperatorTradeBust? TradeBust = null,
        long EnqueueTicks = 0,
        System.Diagnostics.ActivityContext ParentContext = default);

    /// <summary>
    /// Per-channel mass-cancel payload after gateway-side resolution: a
    /// flat list of engine-assigned <c>OrderID</c>s plus the original
    /// inbound timestamp.
    /// </summary>
    internal sealed record ResolvedMassCancel(IReadOnlyList<long> OrderIds, ulong EnteredAtNanos);

    /// <summary>
    /// Operator-triggered trade-bust payload (issue #15): identifies a
    /// previously-published trade by (SecurityId, TradeId) and carries the
    /// echo fields (price/size/date) the consumer audits.
    /// </summary>
    internal sealed record OperatorTradeBust(
        long SecurityId,
        long PriceMantissa,
        long Size,
        uint TradeId,
        ushort TradeDate);

    // ====== high-frequency log messages (LoggerMessage source-gen) ======

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "channel {ChannelNumber} processed {WorkKind} clOrdId={ClOrdId}")]
    private partial void LogCommandProcessed(byte channelNumber, WorkKind workKind, ulong clOrdId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Trace,
        Message = "channel {ChannelNumber} flushed UMDF packet seq={Sequence} bytes={Bytes}")]
    private partial void LogPacketFlushed(byte channelNumber, uint sequence, int bytes);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "channel {ChannelNumber} inbound queue full; dropped {WorkKind} (slow consumer)")]
    private partial void LogQueueFull(byte channelNumber, WorkKind workKind);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "channel {ChannelNumber} dispatcher work-item crash workKind={WorkKind} session={Session} firm={Firm} clOrdId={ClOrdId}")]
    private partial void LogDispatcherCrash(Exception ex, byte channelNumber, WorkKind workKind, string session, uint firm, ulong clOrdId);
}
