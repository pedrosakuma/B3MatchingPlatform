namespace B3.Umdf.WireEncoder;

/// <summary>
/// Builds a per-symbol UMDF snapshot as a sequence of UDP-ready packets:
/// the first packet carries the <c>SnapshotFullRefresh_Header_30</c> frame
/// followed by as many <c>SnapshotFullRefresh_Orders_MBO_71</c> frames as fit
/// in the destination buffer; subsequent packets carry only Orders_71 frames.
/// Each packet is written to a caller-supplied <see cref="Span{T}"/> via the
/// <see cref="PacketHandler"/> delegate so the caller can pool the buffer and
/// invoke its multicast publisher synchronously.
///
/// Caller is responsible for the snapshot channel sequence-number state — the
/// builder only writes the <see cref="WireOffsets.PacketHeaderSize"/>-byte
/// PacketHeader using the supplied sequence number and increments locally for
/// chunks within one snapshot.
/// </summary>
public static class SnapshotPacketBuilder
{
    /// <summary>
    /// Maximum entries per <c>Orders_71</c> repeating-group chunk. The group
    /// length encoding uses a <c>byte</c> NumInGroup so 255 is the absolute
    /// upper bound of the format. Caller should pass a smaller value when
    /// targeting standard 1500-byte MTU networks (a chunk of 30 entries is
    /// ~1.3 KB).
    /// </summary>
    public const int MaxEntriesPerChunk = 255;

    /// <summary>Standard B3 production snapshot MTU (jumbo frames).</summary>
    public const int DefaultPacketBufferSize = 16 * 1024;

    /// <summary>
    /// Invoked once per packet with the slice of <c>buffer</c> that contains a
    /// complete UDP payload (PacketHeader + one or more SBE frames).
    /// </summary>
    public delegate void PacketHandler(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Writes a complete snapshot for <paramref name="securityId"/>. Returns
    /// the number of packets emitted.
    /// </summary>
    /// <param name="buffer">Scratch buffer used for each packet. Size should
    /// cover at least one Orders_71 frame plus PacketHeader; default
    /// <see cref="DefaultPacketBufferSize"/> handles a full 255-entry chunk
    /// with margin.</param>
    /// <param name="channelNumber">UMDF snapshot channel number.</param>
    /// <param name="snapshotSequenceVersion">Current snapshot-loop sequence
    /// version written to each packet header.</param>
    /// <param name="firstSequenceNumber">Sequence number for the first packet
    /// of this snapshot. Subsequent packets within the same snapshot use
    /// successive numbers.</param>
    /// <param name="sendingTimeNanos">UNIX-epoch nanoseconds — same value is
    /// stamped on every packet of the snapshot so consumers see it as one
    /// logical refresh.</param>
    /// <param name="securityId">Instrument identifier (Trade/Order semantic).</param>
    /// <param name="lastRptSeq">Last incremental RptSeq published before this
    /// snapshot was taken; consumers gate snapshot acceptance on this value.
    /// Pass <c>null</c> for an empty illiquid snapshot (per B3 §7.4).</param>
    /// <param name="incrementalSequenceVersion">Incremental-channel sequence
    /// version represented by the captured book and watermarks. Written as
    /// <c>LastSequenceVersion</c> in Header_30; independent from
    /// <paramref name="snapshotSequenceVersion"/>.</param>
    /// <param name="bids">Resting bid orders in price-time priority (best
    /// price first within a side).</param>
    /// <param name="asks">Resting ask orders in price-time priority.</param>
    /// <param name="onPacket">Callback invoked for each packet ready to send.</param>
    /// <param name="maxEntriesPerChunk">Cap on per-frame entries (1..255).</param>
    public static int WriteSnapshot(
        Span<byte> buffer,
        byte channelNumber,
        ushort snapshotSequenceVersion,
        uint firstSequenceNumber,
        ulong sendingTimeNanos,
        long securityId,
        uint? lastRptSeq,
        ushort incrementalSequenceVersion,
        ReadOnlySpan<UmdfWireEncoder.SnapshotEntry> bids,
        ReadOnlySpan<UmdfWireEncoder.SnapshotEntry> asks,
        PacketHandler onPacket,
        int maxEntriesPerChunk = MaxEntriesPerChunk)
    {
        ArgumentNullException.ThrowIfNull(onPacket);
        if (maxEntriesPerChunk < 1 || maxEntriesPerChunk > MaxEntriesPerChunk)
            throw new ArgumentOutOfRangeException(nameof(maxEntriesPerChunk));
        if (buffer.Length < WireOffsets.PacketHeaderSize
            + WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SnapHeaderBlockLength)
        {
            throw new ArgumentException(
                $"buffer too small ({buffer.Length} bytes) for SnapshotFullRefresh_Header_30.",
                nameof(buffer));
        }
        if ((bids.Length != 0 || asks.Length != 0)
            && MaxOrdersEntriesThatFit(buffer.Length - WireOffsets.PacketHeaderSize) == 0)
        {
            throw new ArgumentException(
                $"buffer too small ({buffer.Length} bytes) for a single Orders_71 entry frame ({OrdersFrameSize(1)} bytes); increase buffer size.",
                nameof(buffer));
        }

        // Header_30 stamps total counts so consumers can size their buffers
        // and detect a complete refresh once they accumulate that many entries.
        uint totBids = (uint)bids.Length;
        uint totOffers = (uint)asks.Length;
        uint totReports = totBids + totOffers;

        uint seqNum = firstSequenceNumber;
        int packetCount = 0;

        // ------------------------- Packet 1: header --------------------------
        int p = UmdfWireEncoder.WritePacketHeader(buffer, channelNumber, snapshotSequenceVersion, seqNum, sendingTimeNanos);
        int headerLen = UmdfWireEncoder.WriteSnapshotHeaderFrame(buffer.Slice(p),
            securityId, totReports, totBids, totOffers, totNumStats: 0,
            lastRptSeq: lastRptSeq, lastSequenceVersion: incrementalSequenceVersion);
        p += headerLen;

        // Pack as many Orders_71 chunks as fit in the remaining space.
        int bidIdx = 0, askIdx = 0;
        while (true)
        {
            int chunkSize = NextChunkSize(
                bids, asks, bidIdx, askIdx,
                Math.Min(maxEntriesPerChunk, MaxOrdersEntriesThatFit(buffer.Length - p)));
            if (chunkSize == 0) break;
            int frameSize = OrdersFrameSize(chunkSize);
            WriteOrdersChunk(buffer.Slice(p), securityId, bids, asks, ref bidIdx, ref askIdx, chunkSize);
            p += frameSize;
        }
        onPacket(buffer.Slice(0, p));
        packetCount++;

        // -------------------- Subsequent packets: chunks ---------------------
        while (bidIdx < bids.Length || askIdx < asks.Length)
        {
            seqNum++;
            p = UmdfWireEncoder.WritePacketHeader(buffer, channelNumber, snapshotSequenceVersion, seqNum, sendingTimeNanos);
            // Pack up to buffer capacity.
            while (true)
            {
                int chunkSize = NextChunkSize(
                    bids, asks, bidIdx, askIdx,
                    Math.Min(maxEntriesPerChunk, MaxOrdersEntriesThatFit(buffer.Length - p)));
                if (chunkSize == 0) break;
                int frameSize = OrdersFrameSize(chunkSize);
                WriteOrdersChunk(buffer.Slice(p), securityId, bids, asks, ref bidIdx, ref askIdx, chunkSize);
                p += frameSize;
            }
            onPacket(buffer.Slice(0, p));
            packetCount++;
        }

        return packetCount;
    }

    private static int OrdersFrameSize(int entries)
        => WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
         + WireOffsets.SnapOrdersHeaderBlockLength
         + WireOffsets.SnapOrdersGroupSizeEncodingSize
         + WireOffsets.SnapOrdersEntrySize * entries;

    private static int MaxOrdersEntriesThatFit(int availableBytes)
    {
        int fixedBytes = OrdersFrameSize(0);
        return availableBytes <= fixedBytes
            ? 0
            : (availableBytes - fixedBytes) / WireOffsets.SnapOrdersEntrySize;
    }

    private static int NextChunkSize(
        ReadOnlySpan<UmdfWireEncoder.SnapshotEntry> bids,
        ReadOnlySpan<UmdfWireEncoder.SnapshotEntry> asks,
        int bidIdx, int askIdx, int cap)
    {
        if (cap <= 0) return 0;
        int remaining = (bids.Length - bidIdx) + (asks.Length - askIdx);
        return Math.Min(remaining, cap);
    }

    private static void WriteOrdersChunk(
        Span<byte> dst, long securityId,
        ReadOnlySpan<UmdfWireEncoder.SnapshotEntry> bids,
        ReadOnlySpan<UmdfWireEncoder.SnapshotEntry> asks,
        ref int bidIdx, ref int askIdx, int chunkSize)
    {
        // Bids first within a chunk for predictable consumer-side ordering;
        // top-of-book bid → bottom, then top-of-book ask → bottom. The wire
        // format does not require any side ordering — Snapshot consumers sort
        // by MDEntryType — but a stable order helps test reproducibility.
        Span<UmdfWireEncoder.SnapshotEntry> tmp = chunkSize <= 256
            ? stackalloc UmdfWireEncoder.SnapshotEntry[chunkSize]
            : new UmdfWireEncoder.SnapshotEntry[chunkSize];
        int t = 0;
        while (t < chunkSize && bidIdx < bids.Length) tmp[t++] = bids[bidIdx++];
        while (t < chunkSize && askIdx < asks.Length) tmp[t++] = asks[askIdx++];
        UmdfWireEncoder.WriteSnapshotOrdersFrame(dst, securityId, tmp);
    }

    /// <summary>
    /// Convenience: build snapshot entries from a sequence of arbitrary
    /// resting-order tuples. Caller-side adapter for the matching engine's
    /// <c>RestingOrderView</c> (kept here to avoid a project reference from
    /// WireEncoder to Matching).
    /// </summary>
    public static UmdfWireEncoder.SnapshotEntry MakeBidEntry(
        long priceMantissa, long size, ulong insertNanos, long secondaryOrderId, uint? enteringFirm = null)
        => new(priceMantissa, size, insertNanos, secondaryOrderId, UmdfWireEncoder.MdEntryTypeBid, enteringFirm);

    public static UmdfWireEncoder.SnapshotEntry MakeAskEntry(
        long priceMantissa, long size, ulong insertNanos, long secondaryOrderId, uint? enteringFirm = null)
        => new(priceMantissa, size, insertNanos, secondaryOrderId, UmdfWireEncoder.MdEntryTypeOffer, enteringFirm);
}
