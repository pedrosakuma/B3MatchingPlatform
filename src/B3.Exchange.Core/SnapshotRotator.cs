using B3.Exchange.Contracts.Time;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Core;

/// <summary>
/// Per-UMDF-channel snapshot publisher. Each invocation of
/// <see cref="PublishNext"/> picks the next instrument in round-robin order,
/// reads the resting book on both sides, and publishes a
/// <c>SnapshotFullRefresh_Header_30</c> + as many
/// <c>SnapshotFullRefresh_Orders_MBO_71</c> chunks as the book requires (each
/// chunk capped at <see cref="SnapshotPacketBuilder.MaxEntriesPerChunk"/> or
/// the caller-supplied per-chunk cap, whichever is smaller), followed by one
/// standalone <c>SecurityStatus_3</c> recovery packet for that instrument's
/// current status.
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
/// no resting orders AND its <see cref="ISnapshotBookSource.GetCurrentRptSeq"/>
/// watermark is
/// 0.
/// </para>
/// </summary>
public sealed class SnapshotRotator
{
    private readonly byte _channelNumber;
    private readonly ISnapshotBookSource _source;
    private readonly IUmdfPacketSink _sink;
    private readonly INanosTimeSource _timeSource;
    private readonly int _maxEntriesPerChunk;
    private readonly byte[] _packetBuf;
    private const int SecurityStatusPacketSize = WireOffsets.PacketHeaderSize
        + WireOffsets.FramingHeaderSize
        + WireOffsets.SbeMessageHeaderSize
        + WireOffsets.SecurityStatusBlockLength;

    private int _rotationIndex;

    public byte ChannelNumber => _channelNumber;
    public ushort SequenceVersion { get; private set; } = 1;
    public uint SequenceNumber { get; private set; }
    public int RotationIndex => _rotationIndex;

    public TestProbe CreateTestProbe() => new(this);

    public sealed class TestProbe
    {
        private readonly SnapshotRotator _rotator;

        internal TestProbe(SnapshotRotator rotator) => _rotator = rotator;

        public void SetSequence(ushort version, uint number)
        {
            _rotator.SequenceVersion = version;
            _rotator.SequenceNumber = number;
        }
    }

    public SnapshotRotator(
        byte channelNumber,
        ISnapshotBookSource source,
        IUmdfPacketSink sink,
        INanosTimeSource? timeSource = null,
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
        _timeSource = timeSource ?? SystemNanosTimeSource.Instance;
        _maxEntriesPerChunk = maxEntriesPerChunk;
        _packetBuf = new byte[packetBufferSize];
    }

    /// <summary>
    /// Bumps both the snapshot <see cref="SequenceVersion"/> and resets the
    /// snapshot <see cref="SequenceNumber"/> to 0. Designed to be called by
    /// the dispatcher when the operator-issued sequence-bump command (issue
    /// #6) lands; the dispatcher is responsible for bumping the incremental
    /// channel state in lockstep.
    /// </summary>
    public void BumpSequenceVersion()
    {
        SequenceVersion = UmdfSequenceVersion.NextOrThrow(
            SequenceVersion,
            $"snapshot channel {_channelNumber} epoch");
        SequenceNumber = 0;
    }

    /// <summary>
    /// Publishes a complete snapshot for the next instrument in the rotation.
    /// Returns the number of UDP packets emitted: 0 when
    /// <see cref="ISnapshotBookSource.SecurityIds"/> is empty; otherwise at
    /// least 2, since even an empty book emits the header packet plus a
    /// <c>SecurityStatus_3</c> packet. Caller MUST invoke from the dispatcher
    /// thread so that
    /// <see cref="ISnapshotBookSource.EnumerateBook"/> observes a stable book.
    /// <paramref name="incrementalSequenceVersion"/> must be captured from the
    /// dispatcher in the same turn as the book and RptSeq watermark.
    /// </summary>
    public int PublishNext(ushort incrementalSequenceVersion)
    {
        var ids = _source.SecurityIds;
        if (ids.Count == 0) return 0;
        long securityId = ids[_rotationIndex % ids.Count];
        _rotationIndex = (_rotationIndex + 1) % ids.Count;
        return PublishFor(securityId, incrementalSequenceVersion);
    }

    /// <summary>
    /// Publishes a complete snapshot for the supplied <paramref name="securityId"/>
    /// without advancing the rotation cursor. Useful for tests and for
    /// targeted re-publishes.
    /// </summary>
    public int PublishFor(long securityId, ushort incrementalSequenceVersion)
    {
        UmdfSequenceVersion.EnsureCanBeginLiveWork(
            SequenceVersion,
            $"snapshot channel {_channelNumber} work-item boundary");

        // Materialise the book sides into lists. Snapshot ticks are
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
        // orders matched and no new ones have arrived). A zero RptSeq is
        // therefore treated as "illiquid" only when the book is also empty.
        uint currentRptSeq = _source.GetCurrentRptSeq(securityId);
        bool isBookEmpty = bidsList.Count == 0 && asksList.Count == 0;
        uint? lastRptSeq = isBookEmpty && currentRptSeq == 0 ? null : currentRptSeq;

        int requiredSnapshotPackets = SnapshotPacketBuilder.GetPacketCount(
            _packetBuf.Length,
            bidsList.Count,
            asksList.Count,
            _maxEntriesPerChunk);
        const int statusPackets = 1;
        int requiredPackets = checked(requiredSnapshotPackets + statusPackets);
        EnsurePacketSequenceCapacity(requiredPackets);

        ushort version = SequenceVersion;
        uint firstSeq = SequenceNumber + 1;
        ulong nowNanos = _timeSource.NowNanos();
        byte channel = _channelNumber;
        var sink = _sink;
        var (securityTradingStatus, securityTradingEvent) = _source.GetCurrentSecurityStatus(securityId);

        int snapshotPacketsEmitted = SnapshotPacketBuilder.WriteSnapshot(
            buffer: _packetBuf,
            channelNumber: channel,
            snapshotSequenceVersion: version,
            firstSequenceNumber: firstSeq,
            sendingTimeNanos: nowNanos,
            securityId: securityId,
            lastRptSeq: lastRptSeq,
            incrementalSequenceVersion: incrementalSequenceVersion,
            bids: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bidsList),
            asks: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(asksList),
            onPacket: pkt => sink.Publish(channel, pkt),
            maxEntriesPerChunk: _maxEntriesPerChunk);

        if (snapshotPacketsEmitted != requiredSnapshotPackets)
        {
            throw new InvalidOperationException(
                $"snapshot packet count changed during encode: reserved {requiredSnapshotPackets}, emitted {snapshotPacketsEmitted}");
        }

        int statusPacketLength = WriteSecurityStatusPacket(
            dst: _packetBuf,
            channelNumber: channel,
            sequenceVersion: version,
            sequenceNumber: firstSeq + (uint)snapshotPacketsEmitted,
            sendingTimeNanos: nowNanos,
            securityId: securityId,
            securityTradingStatus: securityTradingStatus,
            securityTradingEvent: securityTradingEvent,
            transactTimeNanos: nowNanos,
            rptSeq: currentRptSeq);
        sink.Publish(channel, _packetBuf.AsSpan(0, statusPacketLength));

        SequenceNumber = (uint)((ulong)SequenceNumber + (uint)requiredPackets);
        return requiredPackets;
    }

    private void EnsurePacketSequenceCapacity(int requiredPackets)
    {
        if (requiredPackets < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredPackets));
        if ((ulong)SequenceNumber + (uint)requiredPackets <= uint.MaxValue)
            return;

        SequenceVersion = UmdfSequenceVersion.NextOrThrow(
            SequenceVersion,
            $"snapshot channel {_channelNumber} automatic epoch rollover");
        SequenceNumber = 0;
    }

    private static int WriteSecurityStatusPacket(
        Span<byte> dst,
        byte channelNumber,
        ushort sequenceVersion,
        uint sequenceNumber,
        ulong sendingTimeNanos,
        long securityId,
        byte securityTradingStatus,
        byte securityTradingEvent,
        ulong transactTimeNanos,
        uint rptSeq)
    {
        if (dst.Length < SecurityStatusPacketSize)
            throw new ArgumentException("buffer too small for SecurityStatus_3 packet", nameof(dst));

        int packetHeaderLength = UmdfWireEncoder.WritePacketHeader(
            dst,
            channelNumber: channelNumber,
            sequenceVersion: sequenceVersion,
            sequenceNumber: sequenceNumber,
            sendingTimeNanos: sendingTimeNanos);
        int frameLength = UmdfWireEncoder.WriteSecurityStatusFrame(
            dst.Slice(packetHeaderLength),
            securityId: securityId,
            tradingSessionId: 0,
            securityTradingStatus: securityTradingStatus,
            securityTradingEvent: securityTradingEvent,
            tradeDate: 0,
            tradSesOpenTimeNanos: 0,
            transactTimeNanos: transactTimeNanos,
            rptSeq: rptSeq);
        return packetHeaderLength + frameLength;
    }
}
