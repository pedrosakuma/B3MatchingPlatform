using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Integration;

/// <summary>
/// Per-UMDF-channel snapshot publisher. Each invocation of
/// <see cref="PublishNext"/> picks the next instrument in round-robin order,
/// reads the resting book on both sides, and publishes a
/// <c>SnapshotFullRefresh_Header_30</c> + as many
/// <c>SnapshotFullRefresh_Orders_MBO_71</c> chunks as the book requires (each
/// chunk capped at <see cref="SnapshotPacketBuilder.MaxEntriesPerChunk"/> or
/// the caller-supplied per-chunk cap, whichever is smaller).
///
/// <para>
/// Threading: <see cref="PublishNext"/> reads the matching engine's resting
/// book and must therefore run on the owning <see cref="ChannelDispatcher"/>'s
/// dispatch thread. The dispatcher invokes it from the inbound work loop, so
/// it never races with order processing.
/// </para>
///
/// <para>
/// Sequence-number discipline: the snapshot channel maintains its OWN
/// monotonic <see cref="SequenceVersion"/> + <see cref="SequenceNumber"/>
/// state, separate from the incremental channel. A future operator command
/// (issue #6) will bump both atomically — see
/// <see cref="BumpSequenceVersion"/> and <see cref="ChannelDispatcher"/>.
/// </para>
///
/// <para>
/// Empty / illiquid handling: per B3 §7.4 a symbol with no incremental
/// history publishes a snapshot header with <c>LastRptSeq</c> absent and an
/// empty Orders_71 group. The rotator emits exactly that when the book has
/// no resting orders AND <see cref="ISnapshotBookSource.CurrentRptSeq"/> is
/// 0.
/// </para>
/// </summary>
public sealed class SnapshotRotator
{
    private readonly byte _channelNumber;
    private readonly ISnapshotBookSource _source;
    private readonly IUmdfPacketSink _sink;
    private readonly Func<ulong> _nowNanos;
    private readonly int _maxEntriesPerChunk;
    private readonly byte[] _packetBuf;

    private int _rotationIndex;

    public byte ChannelNumber => _channelNumber;
    public ushort SequenceVersion { get; private set; } = 1;
    public uint SequenceNumber { get; private set; }
    public int RotationIndex => _rotationIndex;

    public SnapshotRotator(
        byte channelNumber,
        ISnapshotBookSource source,
        IUmdfPacketSink sink,
        Func<ulong>? nowNanos = null,
        int maxEntriesPerChunk = SnapshotPacketBuilder.MaxEntriesPerChunk,
        int packetBufferSize = SnapshotPacketBuilder.DefaultPacketBufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        if (maxEntriesPerChunk < 1 || maxEntriesPerChunk > SnapshotPacketBuilder.MaxEntriesPerChunk)
            throw new ArgumentOutOfRangeException(nameof(maxEntriesPerChunk));
        if (packetBufferSize < 256)
            throw new ArgumentOutOfRangeException(nameof(packetBufferSize), "buffer too small for a snapshot header");
        _channelNumber = channelNumber;
        _source = source;
        _sink = sink;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _maxEntriesPerChunk = maxEntriesPerChunk;
        _packetBuf = new byte[packetBufferSize];
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

    /// <summary>
    /// Bumps both the snapshot <see cref="SequenceVersion"/> and resets the
    /// snapshot <see cref="SequenceNumber"/> to 0. Designed to be called by
    /// the dispatcher when the operator-issued sequence-bump command (issue
    /// #6) lands; the dispatcher is responsible for bumping the incremental
    /// channel state in lockstep.
    /// </summary>
    public void BumpSequenceVersion()
    {
        SequenceVersion = (ushort)(SequenceVersion + 1);
        SequenceNumber = 0;
    }

    /// <summary>
    /// Publishes a complete snapshot for the next instrument in the rotation.
    /// Returns the number of UDP packets emitted (always &gt;= 1, since even
    /// an empty book emits the header packet). Caller MUST invoke from the
    /// dispatcher thread so that <see cref="ISnapshotBookSource.EnumerateBook"/>
    /// observes a stable book.
    /// </summary>
    public int PublishNext()
    {
        var ids = _source.SecurityIds;
        if (ids.Count == 0) return 0;
        long securityId = ids[_rotationIndex % ids.Count];
        _rotationIndex = (_rotationIndex + 1) % ids.Count;
        return PublishFor(securityId);
    }

    /// <summary>
    /// Publishes a complete snapshot for the supplied <paramref name="securityId"/>
    /// without advancing the rotation cursor. Useful for tests and for
    /// targeted re-publishes.
    /// </summary>
    public int PublishFor(long securityId)
    {
        // Materialise the book sides into pooled buffers. Snapshot ticks are
        // low-frequency (typically every few seconds per instrument) so a
        // per-tick allocation is acceptable; the alternative — caching
        // per-symbol scratch lists — adds complexity for negligible benefit.
        var bidsList = new List<UmdfWireEncoder.SnapshotEntry>();
        var asksList = new List<UmdfWireEncoder.SnapshotEntry>();
        foreach (var o in _source.EnumerateBook(securityId, Side.Buy))
            bidsList.Add(SnapshotPacketBuilder.MakeBidEntry(
                o.PriceMantissa, o.RemainingQuantity, o.InsertTimestampNanos, o.OrderId, o.EnteringFirm));
        foreach (var o in _source.EnumerateBook(securityId, Side.Sell))
            asksList.Add(SnapshotPacketBuilder.MakeAskEntry(
                o.PriceMantissa, o.RemainingQuantity, o.InsertTimestampNanos, o.OrderId, o.EnteringFirm));

        // B3 §7.4 illiquid case: no incremental history → publish header with
        // LastRptSeq absent. Once any incremental event has been emitted we
        // always stamp the live RptSeq, even if the book is empty (e.g. all
        // orders matched and no new ones have arrived).
        uint? lastRptSeq = _source.CurrentRptSeq == 0 ? null : _source.CurrentRptSeq;

        ushort version = SequenceVersion;
        uint firstSeq = SequenceNumber + 1;
        ulong nowNanos = _nowNanos();
        byte channel = _channelNumber;
        var sink = _sink;

        int packetsEmitted = SnapshotPacketBuilder.WriteSnapshot(
            buffer: _packetBuf,
            channelNumber: channel,
            sequenceVersion: version,
            firstSequenceNumber: firstSeq,
            sendingTimeNanos: nowNanos,
            securityId: securityId,
            lastRptSeq: lastRptSeq,
            bids: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bidsList),
            asks: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(asksList),
            onPacket: pkt => sink.Publish(channel, pkt),
            maxEntriesPerChunk: _maxEntriesPerChunk);

        SequenceNumber += (uint)packetsEmitted;
        return packetsEmitted;
    }
}
