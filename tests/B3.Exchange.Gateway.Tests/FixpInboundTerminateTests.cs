using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// #198 (gap-functional §4): the gateway must accept inbound FIXP
/// <c>Terminate</c> (templateId=7) per spec §4.5.4 and respond with a
/// graceful <c>Terminate(FINISHED)</c> echo before closing the
/// transport. Previously the header parser rejected the template
/// outright (UnsupportedTemplate → DECODING_ERROR Terminate).
/// </summary>
public class FixpInboundTerminateTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;
        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private static byte[] EncodeInboundTerminate(uint sessionId, ulong sessionVerId, byte code)
    {
        var buf = new byte[EntryPointFixpFrameCodec.TerminateBlock + EntryPointFrameReader.WireHeaderSize];
        EntryPointFixpFrameCodec.EncodeTerminate(buf, sessionId, sessionVerId, code);
        return buf;
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var head = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactlyAsync(stream, head, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0, 2));
        var frame = new byte[messageLength];
        head.CopyTo(frame.AsSpan());
        await ReadExactlyAsync(stream, frame.AsMemory(head.Length), cts.Token);
        return frame;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (n <= 0) throw new EndOfStreamException("connection closed before expected frame was received");
            read += n;
        }
    }

    [Fact]
    public void HeaderParser_AcceptsInboundTerminateBlockLength()
    {
        // Sanity: the new TidTerminate=7 entry must round-trip the
        // header parser instead of being rejected as UnsupportedTemplate.
        Assert.Equal(13, EntryPointFrameReader.ExpectedInboundBlockLength(
            EntryPointFrameReader.TidTerminate, version: 0));
    }

    [Fact]
    public async Task PeerSendsTerminate_GatewayEchoesFinishedAndClosesTransport()
    {
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance);
        listener.Start();
        await using (listener)
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(listener.LocalEndpoint!.Address, listener.LocalEndpoint!.Port);

            // Send peer-initiated Terminate (graceful logout per spec §4.5.4).
            var frame = EncodeInboundTerminate(sessionId: 100, sessionVerId: 42,
                code: SessionRejectEncoder.TerminationCode.Finished);
            await client.GetStream().WriteAsync(frame);

            // Expect a Terminate echo back from the gateway.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buf = new byte[SessionRejectEncoder.TerminateTotal];
            var ns = client.GetStream();
            int read = 0;
            while (read < buf.Length)
            {
                int n = await ns.ReadAsync(buf.AsMemory(read), cts.Token);
                if (n <= 0) throw new EndOfStreamException("connection closed before Terminate echo received");
                read += n;
            }

            ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(
                buf.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
            Assert.Equal(EntryPointFrameReader.TidTerminate, tid);
            byte echoedCode = buf[EntryPointFrameReader.WireHeaderSize + 12];
            Assert.Equal(SessionRejectEncoder.TerminationCode.Finished, echoedCode);

        }
    }

    [Fact]
    public async Task EstablishedSession_TerminateEchoCarriesLiveSessionVerId()
    {
        const uint sessionId = 100;
        const ulong sessionVerId = 42;

        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance);
        listener.Start();
        await using (listener)
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(listener.LocalEndpoint!.Address, listener.LocalEndpoint!.Port);
            var ns = client.GetStream();
            var frame = new byte[256];

            int negotiateLen = EntryPointFixpFrameCodec.EncodeNegotiate(frame,
                sessionId, sessionVerId, timestampNanos: 1UL, enteringFirm: 42u, onBehalfFirm: null,
                credentials: Encoding.UTF8.GetBytes("{}"),
                clientIp: ReadOnlySpan<byte>.Empty,
                clientAppName: ReadOnlySpan<byte>.Empty,
                clientAppVersion: ReadOnlySpan<byte>.Empty);
            await ns.WriteAsync(frame.AsMemory(0, negotiateLen));
            byte[] negotiateResponse = await ReadFrameAsync(ns);
            Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
                BinaryPrimitives.ReadUInt16LittleEndian(negotiateResponse.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));

            int establishLen = EntryPointFixpFrameCodec.EncodeEstablish(frame,
                sessionId, sessionVerId, timestampNanos: 2UL, keepAliveIntervalMillis: 60_000UL,
                nextSeqNo: 1u, cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0UL,
                credentials: ReadOnlySpan<byte>.Empty);
            await ns.WriteAsync(frame.AsMemory(0, establishLen));
            byte[] establishAck = await ReadFrameAsync(ns);
            Assert.Equal(EntryPointFrameReader.TidEstablishAck,
                BinaryPrimitives.ReadUInt16LittleEndian(establishAck.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));

            byte[] terminate = EncodeInboundTerminate(sessionId, sessionVerId,
                SessionRejectEncoder.TerminationCode.Finished);
            await ns.WriteAsync(terminate);

            byte[] echo = await ReadFrameAsync(ns);
            Assert.Equal(EntryPointFrameReader.TidTerminate,
                BinaryPrimitives.ReadUInt16LittleEndian(echo.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));
            Assert.Equal(sessionVerId,
                BinaryPrimitives.ReadUInt64LittleEndian(echo.AsSpan(EntryPointFrameReader.WireHeaderSize + 4, 8)));
            Assert.Equal(SessionRejectEncoder.TerminationCode.Finished,
                echo[EntryPointFrameReader.WireHeaderSize + 12]);
        }
    }
}
