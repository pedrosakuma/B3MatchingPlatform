namespace B3.Exchange.Core;

/// <summary>
/// Per-channel bounded ring buffer of recently-published UMDF incremental
/// packets, keyed by the packet's <c>SequenceNumber</c> (uint).
///
/// <para>Issue #216 (Onda L · L3a): infrastructure half. The dispatcher
/// appends every successfully-published incremental packet here so that a
/// future TCP retransmit responder (L3b) can serve gap-fill requests
/// without replaying through the matching engine. The snapshot and
/// instrumentdef feeds are NOT buffered here — they have their own
/// recovery story (snapshot rotation / SequenceVersion bump).</para>
///
/// <para>Stored bytes are deep-copied on <see cref="Append"/> because the
/// dispatcher reuses its outgoing packet buffer between flushes; mutating
/// the original after publish must not corrupt the retained snapshot.
/// Consumers receive newly-allocated arrays from <see cref="TryGet"/> and
/// <see cref="TryGetRange"/> so concurrent readers cannot trip on a
/// reader-side mutation either.</para>
///
/// <para>Producer (<see cref="Append"/>, <see cref="Reset"/>) is the
/// dispatcher's single loop thread. Readers (<see cref="TryGet"/>,
/// <see cref="TryGetRange"/>, <see cref="FirstAvailableSeqOrZero"/>,
/// <see cref="LastSeqOrZero"/>, <see cref="Count"/>) may run from any
/// thread — the buffer takes its own lock so a range query observes a
/// consistent snapshot of seq window + payloads.</para>
///
/// <para>When the ring is full, the oldest entry is evicted (FIFO);
/// <see cref="Evictions"/> counts how many drops have happened over the
/// dispatcher's lifetime. <see cref="Reset"/> wipes the ring and is
/// invoked when the dispatcher's <c>SequenceVersion</c> rolls over —
/// the (version, seq) tuple is the real identity, so a new version
/// invalidates everything stored against the previous one.</para>
/// </summary>
public sealed class UmdfPacketRetransmitBuffer
{
    /// <summary>Cap inherited from <see cref="RetransmitBufferDefaults"/>;
    /// default 65 536 packets keeps the buffer well under 100 MiB even at
    /// the 1 400-byte UMDF packet ceiling.</summary>
    public int Capacity { get; }

    private readonly object _lock = new();
    private readonly byte[]?[] _packets;
    private readonly uint[] _seqs;
    private int _head;        // next write slot
    private int _count;
    private long _evictions;

    public UmdfPacketRetransmitBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be > 0");
        Capacity = capacity;
        _packets = new byte[]?[capacity];
        _seqs = new uint[capacity];
    }

    /// <summary>Number of packets currently retained.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Total number of FIFO evictions since construction
    /// (monotonic). Useful as a metrics gauge for sizing the ring.</summary>
    public long Evictions { get { lock (_lock) return _evictions; } }

    /// <summary>Lowest <c>SequenceNumber</c> currently retained, or
    /// 0 when the buffer is empty.</summary>
    public uint FirstAvailableSeqOrZero
    {
        get
        {
            lock (_lock)
            {
                if (_count == 0) return 0u;
                int oldest = (_head - _count + Capacity) % Capacity;
                return _seqs[oldest];
            }
        }
    }

    /// <summary>Highest <c>SequenceNumber</c> currently retained, or
    /// 0 when the buffer is empty. Note: a real packet could legitimately
    /// have sequence 0 only at startup (the dispatcher emits its first
    /// packet with seq 1), so the sentinel is unambiguous in practice.</summary>
    public uint LastSeqOrZero
    {
        get
        {
            lock (_lock)
            {
                if (_count == 0) return 0u;
                int newest = (_head - 1 + Capacity) % Capacity;
                return _seqs[newest];
            }
        }
    }

    /// <summary>Append a packet. Bytes are deep-copied. When the ring is
    /// full the oldest entry is dropped and <see cref="Evictions"/> is
    /// incremented.</summary>
    public void Append(uint sequenceNumber, ReadOnlySpan<byte> packet)
    {
        var copy = packet.ToArray();
        lock (_lock)
        {
            if (_count == Capacity)
            {
                _evictions++;
            }
            else
            {
                _count++;
            }
            _packets[_head] = copy;
            _seqs[_head] = sequenceNumber;
            _head = (_head + 1) % Capacity;
        }
    }

    /// <summary>Wipe the ring. Call on <c>SequenceVersion</c> rollover,
    /// when the (version, seq) namespace changes and previously-stored
    /// packets are no longer addressable.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            for (int i = 0; i < _packets.Length; i++) _packets[i] = null;
            _head = 0;
            _count = 0;
            // _evictions intentionally retained — it is a lifetime
            // counter, not a per-version counter; useful when sizing the
            // ring across the whole process lifetime.
        }
    }

    /// <summary>Look up a single packet by sequence number. Returns a
    /// fresh byte[] copy (so the caller cannot mutate the stored entry).
    /// Returns <c>false</c> when the seq is outside the current
    /// retention window.</summary>
    public bool TryGet(uint sequenceNumber, out byte[] packet)
    {
        lock (_lock)
        {
            if (_count == 0) { packet = Array.Empty<byte>(); return false; }
            int oldest = (_head - _count + Capacity) % Capacity;
            uint first = _seqs[oldest];
            uint last = _seqs[(_head - 1 + Capacity) % Capacity];
            if (sequenceNumber < first || sequenceNumber > last) { packet = Array.Empty<byte>(); return false; }
            // Direct subtraction: seq numbers are stored monotonically,
            // wrap-around is handled by the dispatcher resetting the ring
            // before the version bump.
            int offset = (int)(sequenceNumber - first);
            int slot = (oldest + offset) % Capacity;
            var stored = _packets[slot];
            if (stored is null || _seqs[slot] != sequenceNumber)
            {
                // Defensive: shouldn't happen under append-only single-writer
                // semantics, but if it does, fail safe rather than serve a
                // stale slot.
                packet = Array.Empty<byte>();
                return false;
            }
            packet = (byte[])stored.Clone();
            return true;
        }
    }

    /// <summary>Look up an inclusive contiguous range
    /// <c>[fromSeq..fromSeq+count-1]</c>. Returns <c>false</c> if any seq
    /// in the range is missing (entirely outside the retention window or
    /// straddling the lower edge); on success, <paramref name="packets"/>
    /// holds <paramref name="count"/> fresh byte[] copies in seq order.
    /// </summary>
    public bool TryGetRange(uint fromSeq, int count, out byte[][] packets)
    {
        if (count <= 0) { packets = Array.Empty<byte[]>(); return false; }
        lock (_lock)
        {
            if (_count == 0) { packets = Array.Empty<byte[]>(); return false; }
            int oldest = (_head - _count + Capacity) % Capacity;
            uint first = _seqs[oldest];
            uint last = _seqs[(_head - 1 + Capacity) % Capacity];
            uint endSeq = fromSeq + (uint)(count - 1);
            if (fromSeq < first || endSeq > last) { packets = Array.Empty<byte[]>(); return false; }
            var result = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                uint seq = fromSeq + (uint)i;
                int slot = (oldest + (int)(seq - first)) % Capacity;
                var stored = _packets[slot];
                if (stored is null || _seqs[slot] != seq)
                {
                    packets = Array.Empty<byte[]>();
                    return false;
                }
                result[i] = (byte[])stored.Clone();
            }
            packets = result;
            return true;
        }
    }
}

/// <summary>Documented defaults for the UMDF retransmit ring sizing.</summary>
public static class RetransmitBufferDefaults
{
    /// <summary>Default ring capacity (packets) when host config does not
    /// override. 65 536 packets × ~1.4 KB worst-case = ~90 MiB ceiling
    /// per channel; comfortable for the autopilot scenarios in this repo
    /// without straining containerised memory limits.</summary>
    public const int UmdfRingCapacity = 65_536;
}
