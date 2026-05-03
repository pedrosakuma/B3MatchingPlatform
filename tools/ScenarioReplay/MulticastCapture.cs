using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Subscribes to a UMDF incremental multicast group and records each
/// received datagram as a "mcast" line in the supplied
/// <see cref="CaptureWriter"/>. Only the PacketHeader (channel + sequence
/// version + sequence number + message count) is decoded; the body is
/// written verbatim as a hex blob so a diff harness can compare bytes.
/// </summary>
public sealed class MulticastCapture : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CaptureWriter _writer;
    private readonly Action<string>? _logWarn;
    private readonly CancellationTokenSource _cts;
    private Task? _loop;

    private MulticastCapture(UdpClient udp, CaptureWriter writer, Action<string>? logWarn)
    {
        _udp = udp;
        _writer = writer;
        _logWarn = logWarn;
        _cts = new CancellationTokenSource();
    }

    public static MulticastCapture Start(IPAddress group, int port, CaptureWriter writer,
        IPAddress? localInterface = null, Action<string>? logWarn = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(writer);
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        var opt = localInterface == null
            ? new MulticastOption(group)
            : new MulticastOption(group, localInterface);
        udp.JoinMulticastGroup(opt.Group, opt.LocalAddress ?? IPAddress.Any);
        var cap = new MulticastCapture(udp, writer, logWarn);
        cap._loop = Task.Run(() => cap.RunAsync(cap._cts.Token));
        return cap;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var rx = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                RecordPacket(rx.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logWarn?.Invoke($"multicast capture loop error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes the UMDF PacketHeader and writes a mcast record. Exposed
    /// internally so the wire-decoder logic is testable without binding
    /// a real multicast socket.
    /// </summary>
    internal void RecordPacket(byte[] datagram) => DecodeAndRecord(_writer, datagram, _logWarn);

    internal static void DecodeAndRecord(CaptureWriter writer, byte[] datagram, Action<string>? logWarn = null)
    {
        if (datagram.Length < WireOffsets.PacketHeaderSize)
        {
            logWarn?.Invoke($"mcast: short datagram ({datagram.Length} bytes), skipped");
            return;
        }
        var span = datagram.AsSpan();
        byte channel = span[WireOffsets.PacketHeaderChannelOffset];
        ushort seqVer = BinaryPrimitives.ReadUInt16LittleEndian(
            span.Slice(WireOffsets.PacketHeaderSequenceVersionOffset, 2));
        uint seqNum = BinaryPrimitives.ReadUInt32LittleEndian(
            span.Slice(WireOffsets.PacketHeaderSequenceNumberOffset, 4));
        int messageCount = CountMessages(span.Slice(WireOffsets.PacketHeaderSize));
        writer.WriteMulticast(channel, seqVer, seqNum, messageCount, span);
    }

    private static int CountMessages(ReadOnlySpan<byte> body)
    {
        // UMDF incremental frames start with a 4-byte FramingHeader
        // (messageLength: ushort LE, encodingType: ushort LE) where
        // messageLength is the total frame length INCLUDING the 4-byte
        // FramingHeader itself. We don't need to interpret the SBE body —
        // counting frames is enough metadata for a diff harness.
        int count = 0;
        int p = 0;
        while (p + WireOffsets.FramingHeaderSize <= body.Length)
        {
            ushort frameLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(p, 2));
            if (frameLen < WireOffsets.FramingHeaderSize || p + frameLen > body.Length) break;
            count++;
            p += frameLen;
        }
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _udp.Dispose(); } catch { }
        if (_loop != null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
