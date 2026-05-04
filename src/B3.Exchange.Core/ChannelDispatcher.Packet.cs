namespace B3.Exchange.Core;

/// <summary>
/// UMDF packet-buffer facet of <see cref="ChannelDispatcher"/>
/// (issue #168 split): the per-command incremental packet builder.
/// <see cref="ReserveOrFlush"/> ensures there is room for the next frame
/// (writing the packet header up-front when starting a new packet),
/// <see cref="Commit"/> advances the cursor, and <see cref="FlushPacket"/>
/// stamps the SequenceNumber + sendingTime, hands the bytes to the
/// <see cref="IUmdfPacketSink"/>, and accounts the throughput counters.
/// All methods run on the dispatch thread.
/// </summary>
public sealed partial class ChannelDispatcher
{
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
            // Issue #216 (L3a): the (version, seq) tuple identifies a
            // packet uniquely; once version bumps, the previously-stored
            // packets address a now-stale namespace and would mislead a
            // gap-fill responder. Wipe the ring before appending under
            // the new version.
            _retxBuffer?.Reset();
        }
        Volatile.Write(ref _sequenceNumber, _sequenceNumber + 1);
        ulong now = _nowNanos();
        B3.Umdf.WireEncoder.UmdfWireEncoder.PatchPacketHeader(
            _packetBuf.AsSpan(0, B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize), _sequenceNumber, now);
        _packetSink.Publish(ChannelNumber, _packetBuf.AsSpan(0, _packetWritten));
        // Issue #216 (L3a): retain a deep-copied snapshot of the just-
        // published incremental packet for the future retransmit
        // responder (L3b). Snapshot/instrumentdef feeds are NOT routed
        // through here — they reach the wire via the host-owned
        // CountingUdpPacketSinkDecorator, by design.
        _retxBuffer?.Append(_sequenceNumber, _packetBuf.AsSpan(0, _packetWritten));
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
}
