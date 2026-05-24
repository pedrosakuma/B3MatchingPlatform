using B3.EntryPoint.Wire;
namespace B3.Exchange.Gateway;

internal delegate void RetxAppendPersistCallback(uint seq, ReadOnlySpan<byte> frame);

/// <summary>
/// Per-session bounded ring buffer of recently-emitted business frames,
/// keyed by FIXP <c>MsgSeqNum</c>. Used by issue #46 to satisfy
/// <c>RetransmitRequest</c>s per spec §4.5.6.
///
/// <para>Producer (<see cref="Append"/>) is the session's outbound write
/// path under the FixpSession outbound serialization lock; consumer
/// (<see cref="TryGet"/>) is the inbound dispatch thread that handles
/// <c>RetransmitRequest</c>. The buffer takes its own internal lock so
/// validation + cloning happen as a single atomic snapshot — there is no
/// TOCTOU window between checking the seq window and reading the frames.</para>
///
/// <para>Replayed frames are deep-copied and have the
/// <c>OutboundBusinessHeader.EventIndicator.PossResend</c> bit (0x01)
/// set on the copy. The stored originals are never mutated, so a session
/// can serve many overlapping replays without corrupting the live stream
/// (per #GAP-13).</para>
///
/// <para>Bounded by <see cref="Capacity"/>: when full, the oldest entry
/// is evicted. <see cref="FirstAvailableSeqOrZero"/> tracks the lowest
/// retained seq (zero before the first append). <see cref="LastSeqOrZero"/>
/// is the highest. Combined with the request validator, this lets the
/// caller distinguish <c>OUT_OF_RANGE</c> (below window) from
/// <c>INVALID_FROMSEQNO</c> (above window) per #46 acceptance criteria.</para>
/// </summary>
internal sealed class RetransmitBuffer : IDisposable
{
    /// <summary>Absolute byte offset of the
    /// <c>OutboundBusinessHeader.EventIndicator</c> within a wire frame
    /// (SOFH 4 + SBE 8 + BusinessHeader prefix 16 = 28). All buffered
    /// business templates (ExecutionReport_*, BusinessMessageReject)
    /// share this layout — see ExecutionReportEncoder.WriteBusinessHeader.</summary>
    public const int EventIndicatorAbsoluteOffset = EntryPointFrameReader.WireHeaderSize + 16;

    /// <summary>EventIndicator bit for PossResend (per FIXP V6 schema
    /// EventIndicator set: PossResend = 1).</summary>
    public const byte PossResendBit = 0x01;

    /// <summary>Spec §4.5.6: max messages allowed in a single
    /// RetransmitRequest.</summary>
    public const int MaxRequestCount = 1000;

    private readonly object _lock = new();
    private readonly byte[]?[] _frames;
    private readonly PooledOutboundFrame?[] _pooledFrames;
    private readonly uint[] _seqs;
    private readonly B3.Exchange.Contracts.RetransmitMetrics? _metrics;
    private readonly Func<bool>? _isSuspended;
    private readonly RetxAppendPersistCallback? _onAppendPersist;
    /// <summary>
    /// Issue #405: cold-read callback into the persistent outbound
    /// journal, invoked when a <c>RetransmitRequest.fromSeqNo</c>
    /// falls below this ring's lowest retained seq (or the ring is
    /// empty). Signature is <c>(fromSeq, count) → entries in
    /// strictly increasing seq order starting at fromSeq, with no
    /// internal gaps</c>; an empty / short result is treated as
    /// "journal does not have this range" and stops the cold prefix.
    /// </summary>
    private readonly Func<uint, int, IReadOnlyList<B3.Exchange.Gateway.Persistence.OutboundJournalEntry>>? _coldRead;
    private int _head;        // next write index
    private int _count;
    private uint _lastSeq;

    public int Capacity { get; }

    public RetransmitBuffer(int capacity)
        : this(capacity, metrics: null, isSuspended: null)
    {
    }

    /// <summary>
    /// Issue #288 overload: the optional <paramref name="metrics"/> sink
    /// receives a tick on every full-ring eviction
    /// (<see cref="B3.Exchange.Contracts.RetransmitMetrics.IncBufferEvictions"/>)
    /// and on every Append observed while
    /// <paramref name="isSuspended"/> returns <c>true</c>
    /// (<see cref="B3.Exchange.Contracts.RetransmitMetrics.IncPassiveErBuffered"/>).
    /// Both callbacks may be <c>null</c> in tests / standalone use.
    /// </summary>
    public RetransmitBuffer(int capacity,
        B3.Exchange.Contracts.RetransmitMetrics? metrics,
        Func<bool>? isSuspended)
        : this(capacity, metrics, isSuspended, onAppendPersist: null, coldRead: null)
    {
    }

    /// <summary>
    /// Issue #405 overload: <paramref name="onAppendPersist"/> mirrors
    /// every append into the persistent outbound journal (cheap; runs
    /// under this buffer's internal lock so the on-disk order matches
    /// the in-memory order). <paramref name="coldRead"/> is consulted
    /// for any <see cref="TryGet"/> whose <c>fromSeq</c> falls below
    /// the ring's lowest retained seq (or when the ring is empty), so
    /// the bounded ring becomes a hot cache in front of the unbounded
    /// persistent outbound journal. The callback is invoked under
    /// this buffer's internal lock — a single journal disk read per
    /// request is acceptable since the rare path is a
    /// reconnect-resync, not the steady-state append path; the
    /// alternative (release-then-reacquire) would require a
    /// re-verification window that is materially more complex than
    /// the cost we save.
    /// </summary>
    public RetransmitBuffer(int capacity,
        B3.Exchange.Contracts.RetransmitMetrics? metrics,
        Func<bool>? isSuspended,
        RetxAppendPersistCallback? onAppendPersist,
        Func<uint, int, IReadOnlyList<B3.Exchange.Gateway.Persistence.OutboundJournalEntry>>? coldRead)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _frames = new byte[]?[capacity];
        _pooledFrames = new PooledOutboundFrame?[capacity];
        _seqs = new uint[capacity];
        _metrics = metrics;
        _isSuspended = isSuspended;
        _onAppendPersist = onAppendPersist;
        _coldRead = coldRead;
    }

    /// <summary>
    /// Appends a wire frame (SOFH+SBE+body) to the buffer at the given
    /// sequence number. The buffer takes a reference; do not mutate
    /// <paramref name="frame"/> after calling. Evicts the oldest entry
    /// if the buffer is full (and bumps
    /// <see cref="B3.Exchange.Contracts.RetransmitMetrics.BufferEvictions"/>
    /// for issue #288 alerting).
    /// </summary>
    public void Append(uint seq, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool evicted;
        lock (_lock)
        {
            evicted = _count == Capacity;
            ReleasePooledAt(_head);
            _frames[_head] = frame;
            _pooledFrames[_head] = null;
            _seqs[_head] = seq;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            _lastSeq = seq;
            // Issue #405: every append is mirrored to the persistent
            // outbound journal. Callback runs INSIDE the lock so the
            // on-disk order matches the in-memory order (both producer
            // threads serialize on _lock anyway).
            _onAppendPersist?.Invoke(seq, frame);
        }
        // Issue #288: surface eviction + suspended-write counters outside
        // the lock so a slow metrics consumer cannot back-pressure the
        // outbound encoder. Both checks are best-effort.
        if (evicted) _metrics?.IncBufferEvictions();
        if (_isSuspended is { } cb && cb()) _metrics?.IncPassiveErBuffered();
    }

    internal void Append(uint seq, PooledOutboundFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool evicted;
        lock (_lock)
        {
            evicted = _count == Capacity;
            ReleasePooledAt(_head);
            frame.AddRef();
            _frames[_head] = null;
            _pooledFrames[_head] = frame;
            _seqs[_head] = seq;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            _lastSeq = seq;
            _onAppendPersist?.Invoke(seq, frame.Buffer.AsSpan(0, frame.Length));
        }
        if (evicted) _metrics?.IncBufferEvictions();
        if (_isSuspended is { } cb && cb()) _metrics?.IncPassiveErBuffered();
    }

    /// <summary>Snapshot of the lowest seq currently retained, or 0 if
    /// the buffer is empty.</summary>
    public uint FirstAvailableSeqOrZero
    {
        get
        {
            lock (_lock)
            {
                if (_count == 0) return 0;
                int firstIndex = (_head - _count + Capacity) % Capacity;
                return _seqs[firstIndex];
            }
        }
    }

    /// <summary>Snapshot of the highest seq currently retained, or 0
    /// if the buffer is empty.</summary>
    public uint LastSeqOrZero
    {
        get { lock (_lock) { return _count == 0 ? 0u : _lastSeq; } }
    }

    public int Count { get { lock (_lock) { return _count; } } }

    public void Dispose()
    {
        lock (_lock)
        {
            for (int i = 0; i < Capacity; i++)
            {
                ReleasePooledAt(i);
                _seqs[i] = 0;
            }
            _head = 0;
            _count = 0;
            _lastSeq = 0;
        }
    }

    /// <summary>
    /// Outcome of <see cref="TryGet"/>. On success <see cref="Frames"/>
    /// holds <see cref="ActualCount"/> deep-copied frames in seq order
    /// starting at <see cref="FirstSeq"/> (= the request's
    /// <c>fromSeqNo</c> when accepted), each with the
    /// <c>PossResend</c> bit set. On failure <see cref="RejectCode"/>
    /// is non-null.
    /// </summary>
    public readonly record struct GetResult(
        byte[][] Frames,
        uint FirstSeq,
        uint ActualCount,
        B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode? RejectCode);

    /// <summary>
    /// Validates and clones the requested seq range under a single lock.
    /// Validation order: count bounds → empty buffer → fromSeq window
    /// (below = OUT_OF_RANGE, above = INVALID_FROMSEQNO). On accept,
    /// clamps the response to <c>min(count, lastSeq - fromSeq + 1)</c>
    /// when the request extends past what's been produced.
    /// </summary>
    public GetResult TryGet(uint fromSeq, uint requestedCount)
    {
        // Per spec / schema range; map count==0 to INVALID_COUNT and
        // count>1000 to REQUEST_LIMIT_EXCEEDED. (See gpt-5.5 critique
        // for taxonomy choice.)
        if (requestedCount == 0)
            return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_COUNT);
        if (requestedCount > MaxRequestCount)
            return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.REQUEST_LIMIT_EXCEEDED);

        lock (_lock)
        {
            // Empty ring: defer to journal cold-read if available;
            // otherwise treat as "above window" per pre-#405 behavior.
            if (_count == 0)
            {
                if (_coldRead is null)
                    return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO);
                return ServeFromColdOnlyLocked(fromSeq, requestedCount);
            }

            int firstIndex = (_head - _count + Capacity) % Capacity;
            uint firstSeq = _seqs[firstIndex];
            uint lastSeq = _lastSeq;

            if (fromSeq < firstSeq)
            {
                // Below the hot ring; spill into the journal (issue #405).
                // When no journal is wired we keep pre-#405 semantics
                // (OUT_OF_RANGE) so existing tests / behavior stand.
                if (_coldRead is null)
                    return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE);
                return ServeColdThenWarmLocked(fromSeq, requestedCount, firstSeq, firstIndex, lastSeq);
            }
            if (fromSeq > lastSeq)
                return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO);

            // Promote to ulong for clamp arithmetic per gpt-5.5 critique
            // — fromSeq+count near uint.MaxValue can overflow.
            ulong available = (ulong)lastSeq - fromSeq + 1UL;
            uint actual = (uint)Math.Min(requestedCount, available);
            return new(CloneWarmLocked(fromSeq, firstSeq, firstIndex, actual), fromSeq, actual, null);
        }
    }

    /// <summary>
    /// Empty-ring path: every requested seq comes from the journal.
    /// </summary>
    private GetResult ServeFromColdOnlyLocked(uint fromSeq, uint requestedCount)
    {
        var cold = SafeColdRead(fromSeq, (int)requestedCount);
        if (cold.Count == 0)
            return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE);
        var clones = new byte[cold.Count][];
        for (int i = 0; i < cold.Count; i++) clones[i] = CloneWithPossResend(cold[i].Frame);
        return new(clones, fromSeq, (uint)cold.Count, null);
    }

    /// <summary>
    /// Mixed path: cold range <c>[fromSeq, firstSeq-1]</c> from the
    /// journal, then warm range <c>[firstSeq, fromSeq+count-1]</c>
    /// from the ring. If the journal cannot serve the full cold
    /// range (e.g. pruned watermark), only the contiguous prefix
    /// starting at <paramref name="fromSeq"/> is returned and the
    /// warm portion is silently dropped — the peer will request the
    /// next missing slice itself.
    /// </summary>
    private GetResult ServeColdThenWarmLocked(uint fromSeq, uint requestedCount,
        uint firstSeq, int firstIndex, uint lastSeq)
    {
        // Cold portion length capped by request and by the ring's
        // floor; promotion to long keeps the subtraction sane near
        // uint boundaries.
        long coldDesired = Math.Min((long)requestedCount, (long)firstSeq - fromSeq);
        var cold = SafeColdRead(fromSeq, (int)coldDesired);
        if (cold.Count == 0)
            return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE);

        // Cap warm portion at what the request still needs after the cold prefix.
        long remaining = (long)requestedCount - cold.Count;
        bool coldReachesRing = (long)cold[^1].Seq + 1 == firstSeq;
        uint warmCount = 0;
        if (coldReachesRing && remaining > 0)
        {
            ulong available = (ulong)lastSeq - firstSeq + 1UL;
            warmCount = (uint)Math.Min((ulong)remaining, available);
        }

        var clones = new byte[cold.Count + (int)warmCount][];
        for (int i = 0; i < cold.Count; i++) clones[i] = CloneWithPossResend(cold[i].Frame);
        if (warmCount > 0)
        {
            var warm = CloneWarmLocked(firstSeq, firstSeq, firstIndex, warmCount);
            warm.CopyTo(clones, cold.Count);
        }
        return new(clones, fromSeq, (uint)clones.Length, null);
    }

    private byte[][] CloneWarmLocked(uint fromSeq, uint firstSeq, int firstIndex, uint actual)
    {
        var clones = new byte[actual][];
        int offsetFromFirst = (int)(fromSeq - firstSeq);
        for (uint i = 0; i < actual; i++)
        {
            int idx = (firstIndex + offsetFromFirst + (int)i) % Capacity;
            clones[i] = CloneWithPossResend(FrameSpanAt(idx));
        }
        return clones;
    }

    private static byte[] CloneWithPossResend(byte[] src)
        => CloneWithPossResend(src.AsSpan());

    private static byte[] CloneWithPossResend(ReadOnlySpan<byte> src)
    {
        var clone = new byte[src.Length];
        src.CopyTo(clone);
        if (clone.Length > EventIndicatorAbsoluteOffset)
            clone[EventIndicatorAbsoluteOffset] |= PossResendBit;
        return clone;
    }

    private ReadOnlySpan<byte> FrameSpanAt(int idx)
    {
        if (_pooledFrames[idx] is { } pooled)
            return pooled.Buffer.AsSpan(0, pooled.Length);
        return _frames[idx]!;
    }

    private void ReleasePooledAt(int idx)
    {
        if (_pooledFrames[idx] is { } pooled)
        {
            pooled.Release();
            _pooledFrames[idx] = null;
        }
        _frames[idx] = null;
    }

    private IReadOnlyList<B3.Exchange.Gateway.Persistence.OutboundJournalEntry> SafeColdRead(uint fromSeq, int count)
    {
        if (_coldRead is null || count <= 0)
            return Array.Empty<B3.Exchange.Gateway.Persistence.OutboundJournalEntry>();
        try
        {
            var raw = _coldRead(fromSeq, count);
            if (raw is null || raw.Count == 0)
                return Array.Empty<B3.Exchange.Gateway.Persistence.OutboundJournalEntry>();
            // Defensive: enforce the journal contract here (no gap,
            // starts at fromSeq). A misbehaving journal that violates
            // this would otherwise silently corrupt the replay.
            if (raw[0].Seq != fromSeq)
                return Array.Empty<B3.Exchange.Gateway.Persistence.OutboundJournalEntry>();
            for (int i = 1; i < raw.Count; i++)
            {
                if (raw[i].Seq != raw[i - 1].Seq + 1)
                {
                    // Trim at the gap.
                    var prefix = new B3.Exchange.Gateway.Persistence.OutboundJournalEntry[i];
                    for (int k = 0; k < i; k++) prefix[k] = raw[k];
                    return prefix;
                }
            }
            return raw;
        }
        catch
        {
            // Cold-read errors must never crash the inbound dispatch
            // thread; downgrade to "journal cannot serve this range".
            return Array.Empty<B3.Exchange.Gateway.Persistence.OutboundJournalEntry>();
        }
    }
}
