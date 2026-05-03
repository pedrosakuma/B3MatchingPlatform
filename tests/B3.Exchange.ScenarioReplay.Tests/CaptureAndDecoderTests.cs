using System.Buffers.Binary;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.ScenarioReplay.Tests;

public class CaptureWriterTests
{
    [Fact]
    public void WriteEr_EmitsOneJsonLinePerCall_InOrder()
    {
        var sw = new StringWriter();
        long t = 1_700_000_000_000L;
        var cap = new CaptureWriter(sw, () => t++);
        cap.WriteEr("new", clOrdId: 42, securityId: 100, orderId: 7, side: "buy");
        cap.WriteEr("trade", clOrdId: 42, securityId: 100, orderId: 7, lastQty: 5, lastPxMantissa: 320000, leavesQty: 5, cumQty: 5, side: "buy");
        cap.Dispose();

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"src\":\"er\"", lines[0]);
        Assert.Contains("\"execType\":\"new\"", lines[0]);
        Assert.Contains("\"execType\":\"trade\"", lines[1]);
        Assert.Contains("\"lastQty\":5", lines[1]);
        Assert.DoesNotContain("\"lastQty\":null", lines[0]);
    }

    [Fact]
    public void WriteMulticast_EncodesHexAndCount()
    {
        var sw = new StringWriter();
        var cap = new CaptureWriter(sw, () => 0);
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        cap.WriteMulticast(channel: 84, sequenceVersion: 1, sequenceNumber: 99, messageCount: 3, bytes);
        cap.Dispose();
        var line = sw.ToString().Trim();
        Assert.Contains("\"src\":\"mcast\"", line);
        Assert.Contains("\"channel\":84", line);
        Assert.Contains("\"sequenceNumber\":99", line);
        Assert.Contains("\"messageCount\":3", line);
        Assert.Contains("\"bytes\":\"DEADBEEF\"", line);
    }
}

public class MulticastCaptureWireDecoderTests
{
    [Fact]
    public void RecordPacket_DecodesPacketHeader_AndCountsFrames()
    {
        var sw = new StringWriter();
        var cap = new CaptureWriter(sw, () => 0);
        // Build a synthetic UMDF packet: 16-byte PacketHeader + 2 frames.
        // Each frame is just FramingHeader(4) + 4 bytes body (8 total).
        var packet = new byte[WireOffsets.PacketHeaderSize + 2 * 8];
        packet[WireOffsets.PacketHeaderChannelOffset] = 84;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(WireOffsets.PacketHeaderSequenceVersionOffset, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4), 12345);
        // Frame 0
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(WireOffsets.PacketHeaderSize, 2), 8);
        // Frame 1
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(WireOffsets.PacketHeaderSize + 8, 2), 8);

        // Use reflection-free path via internal RecordPacket.
        // Need an instance — but the constructor is private. Build one via the
        // static Start factory? That binds a real socket. Instead, call
        // RecordPacket through a small wrapper that we expose via InternalsVisibleTo.
        InvokeRecordPacket(cap, packet);

        var line = sw.ToString().Trim();
        Assert.Contains("\"channel\":84", line);
        Assert.Contains("\"sequenceVersion\":1", line);
        Assert.Contains("\"sequenceNumber\":12345", line);
        Assert.Contains("\"messageCount\":2", line);
    }

    [Fact]
    public void RecordPacket_SkipsTruncated()
    {
        var sw = new StringWriter();
        var cap = new CaptureWriter(sw, () => 0);
        InvokeRecordPacket(cap, new byte[4]);
        Assert.Empty(sw.ToString());
    }

    // Use the static internal entry point; avoids needing to bind a real
    // multicast socket in unit tests.
    private static void InvokeRecordPacket(CaptureWriter writer, byte[] datagram)
        => MulticastCapture.DecodeAndRecord(writer, datagram, null);
}
