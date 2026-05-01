namespace B3.Exchange.Gateway;

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
internal sealed class RetransmitBuffer
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
    private readonly uint[] _seqs;
    private int _head;        // next write index
    private int _count;
    private uint _lastSeq;

    public int Capacity { get; }

    public RetransmitBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _frames = new byte[]?[capacity];
        _seqs = new uint[capacity];
    }

    /// <summary>
    /// Appends a wire frame (SOFH+SBE+body) to the buffer at the given
    /// sequence number. The buffer takes a reference; do not mutate
    /// <paramref name="frame"/> after calling. Evicts the oldest entry
    /// if the buffer is full.
    /// </summary>
    public void Append(uint seq, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            _frames[_head] = frame;
            _seqs[_head] = seq;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            _lastSeq = seq;
        }
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
            if (_count == 0)
            {
                // No history at all — treat as "above window".
                return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO);
            }

            int firstIndex = (_head - _count + Capacity) % Capacity;
            uint firstSeq = _seqs[firstIndex];
            uint lastSeq = _lastSeq;

            if (fromSeq < firstSeq)
                return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE);
            if (fromSeq > lastSeq)
                return new(Array.Empty<byte[]>(), 0, 0, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO);

            // Promote to ulong for clamp arithmetic per gpt-5.5 critique
            // — fromSeq+count near uint.MaxValue can overflow.
            ulong available = (ulong)lastSeq - fromSeq + 1UL;
            uint actual = (uint)Math.Min(requestedCount, available);

            var clones = new byte[actual][];
            int offsetFromFirst = (int)(fromSeq - firstSeq);
            for (uint i = 0; i < actual; i++)
            {
                int idx = (firstIndex + offsetFromFirst + (int)i) % Capacity;
                var src = _frames[idx]!;
                var clone = new byte[src.Length];
                Buffer.BlockCopy(src, 0, clone, 0, src.Length);
                if (clone.Length > EventIndicatorAbsoluteOffset)
                    clone[EventIndicatorAbsoluteOffset] |= PossResendBit;
                clones[i] = clone;
            }
            return new(clones, fromSeq, actual, null);
        }
    }
}
