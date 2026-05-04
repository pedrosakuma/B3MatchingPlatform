using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.SyntheticTrader.Tests.Fixp;

/// <summary>
/// In-process fake FIXP server for <see cref="FixpClient"/> recovery
/// tests. It listens on a loopback port, accepts a single client,
/// completes the FIXP handshake (Negotiate→NegotiateResponse,
/// Establish→EstablishAck) with configurable behavior, and then
/// exposes the post-handshake server-side stream so individual tests
/// can either read frames the client emits (heartbeat,
/// RetransmitRequest, Terminate) or push session-layer frames into
/// the client (Sequence, Terminate, NotApplied, Retransmission).
///
/// Tests that only need to exercise the post-handshake state machine
/// can also call <see cref="FixpClient.HandleInboundFrame"/> and
/// <see cref="FixpClient.TrackInboundBusinessSeq"/> directly without
/// going over the socket.
/// </summary>
internal sealed class FakeFixpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private TcpClient? _serverSide;
    private NetworkStream? _serverStream;

    /// <summary>Port the listener is bound to.</summary>
    public int Port { get; }

    public FakeFixpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Server-side stream after the client has connected.</summary>
    public NetworkStream Stream => _serverStream
        ?? throw new InvalidOperationException("client has not connected yet");

    /// <summary>
    /// Accepts one client and runs the handshake. Returns the
    /// last-incoming-seq-no the server should report in
    /// <c>EstablishAck</c> (0 for a fresh session, or a higher value
    /// to simulate a re-bind after the client lost messages).
    /// </summary>
    public async Task RunHandshakeAsync(uint serverNextSeqNo = 1, uint lastIncomingSeqNo = 0,
        CancellationToken ct = default)
    {
        _serverSide = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        _serverStream = _serverSide.GetStream();

        // Read Negotiate.
        var negFrame = await ReadFrameAsync(ct).ConfigureAwait(false);
        if (negFrame.TemplateId != EntryPointFrameReader.TidNegotiate)
            throw new InvalidOperationException($"expected Negotiate, got templateId={negFrame.TemplateId}");
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(negFrame.Body.AsSpan(0, 4));
        ulong sessionVerId = BinaryPrimitives.ReadUInt64LittleEndian(negFrame.Body.AsSpan(4, 8));

        // Send NegotiateResponse.
        var negResp = new byte[NegotiateResponseEncoder.Total];
        NegotiateResponseEncoder.Encode(negResp, sessionId, sessionVerId,
            requestTimestampNanos: 0, enteringFirm: 7,
            semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
        await _serverStream.WriteAsync(negResp, ct).ConfigureAwait(false);

        // Read Establish.
        var estFrame = await ReadFrameAsync(ct).ConfigureAwait(false);
        if (estFrame.TemplateId != EntryPointFrameReader.TidEstablish)
            throw new InvalidOperationException($"expected Establish, got templateId={estFrame.TemplateId}");

        // Send EstablishAck.
        var ack = new byte[EstablishAckEncoder.Total];
        EstablishAckEncoder.Encode(ack, sessionId, sessionVerId,
            requestTimestampNanos: 0, keepAliveIntervalMillis: 2000,
            nextSeqNo: serverNextSeqNo, lastIncomingSeqNo: lastIncomingSeqNo,
            semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
        await _serverStream.WriteAsync(ack, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reject the Negotiate handshake instead of completing it.
    /// </summary>
    public async Task RunRejectingHandshakeAsync(FixpSbe.NegotiationRejectCode rejectCode,
        CancellationToken ct = default)
    {
        _serverSide = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        _serverStream = _serverSide.GetStream();
        var negFrame = await ReadFrameAsync(ct).ConfigureAwait(false);
        if (negFrame.TemplateId != EntryPointFrameReader.TidNegotiate)
            throw new InvalidOperationException($"expected Negotiate, got templateId={negFrame.TemplateId}");
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(negFrame.Body.AsSpan(0, 4));
        ulong sessionVerId = BinaryPrimitives.ReadUInt64LittleEndian(negFrame.Body.AsSpan(4, 8));

        var rej = new byte[NegotiateRejectEncoder.Total];
        NegotiateRejectEncoder.Encode(rej, sessionId, sessionVerId,
            requestTimestampNanos: 0, enteringFirm: 7,
            rejectCode: rejectCode, currentSessionVerId: sessionVerId);
        await _serverStream.WriteAsync(rej, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one fully-framed inbound message from the client.</summary>
    public async Task<DecodedFrame> ReadFrameAsync(CancellationToken ct = default)
    {
        var hdr = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(Stream, hdr, ct).ConfigureAwait(false);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(EntryPointFrameReader.SofhSize, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        if (bodyLen > 0) await ReadExactAsync(Stream, body, ct).ConfigureAwait(false);
        return new DecodedFrame(templateId, blockLength, body);
    }

    /// <summary>Reads the next frame matching <paramref name="templateId"/>.</summary>
    public async Task<DecodedFrame> ReadUntilAsync(ushort templateId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var f = await ReadFrameAsync(cts.Token).ConfigureAwait(false);
            if (f.TemplateId == templateId) return f;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _serverStream?.Dispose(); } catch { }
        try { _serverSide?.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
        await Task.CompletedTask;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("connection closed");
            read += n;
        }
    }

    public readonly record struct DecodedFrame(ushort TemplateId, ushort BlockLength, byte[] Body);
}
