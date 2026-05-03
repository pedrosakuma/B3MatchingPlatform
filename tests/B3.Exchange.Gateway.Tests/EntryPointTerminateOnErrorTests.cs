using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// GAP-03 (#41): the gateway must send a FIXP <c>Terminate</c> with the
/// appropriate <c>TerminationCode</c> before closing the TCP connection
/// on framing/decoding errors (spec §4.5.7 / §4.10).
/// </summary>
public class EntryPointTerminateOnErrorTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { return true; }
        public bool EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public bool EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) { }
    }

    private static async Task<(EntryPointListener listener, TcpClient client)> ConnectAsync()
    {
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance);
        listener.Start();
        var client = new TcpClient();
        await client.ConnectAsync(listener.LocalEndpoint!.Address, listener.LocalEndpoint!.Port);
        return (listener, client);
    }

    private static async Task<byte> SendBadFrameExpectingTerminateAsync(byte[] frame)
    {
        var (listener, client) = await ConnectAsync();
        await using (listener)
        using (client)
        {
            await client.GetStream().WriteAsync(frame);
            return await ReadTerminationCodeAsync(client);
        }
    }

    private static async Task<byte> ReadTerminationCodeAsync(TcpClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buf = new byte[SessionRejectEncoder.TerminateTotal];
        int read = 0;
        var ns = client.GetStream();
        while (read < buf.Length)
        {
            int n = await ns.ReadAsync(buf.AsMemory(read), cts.Token);
            if (n <= 0) throw new EndOfStreamException("connection closed before Terminate received");
            read += n;
        }
        // Validate it is a Terminate frame and return the terminationCode byte.
        ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2));
        Assert.Equal(SessionRejectEncoder.TerminateTotal, msgLen);
        ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(2, 2));
        Assert.Equal(EntryPointFrameReader.SofhEncodingType, encoding);
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        Assert.Equal(EntryPointFrameReader.TidTerminate, tid);
        // body offset = WireHeaderSize, terminationCode at body[12]
        return buf[EntryPointFrameReader.WireHeaderSize + 12];
    }

    [Fact]
    public async Task BadSofhEncodingType_SendsInvalidSofhTerminate()
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)frame.Length,
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        // Corrupt encodingType.
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), 0x1234);

        byte code = await SendBadFrameExpectingTerminateAsync(frame);
        Assert.Equal(SessionRejectEncoder.TerminationCode.InvalidSofh, code);
    }

    [Fact]
    public async Task MessageLengthOverCap_SendsInvalidSofhTerminate()
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)(EntryPointFrameReader.MaxInboundMessageLength + 1),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        byte code = await SendBadFrameExpectingTerminateAsync(frame);
        Assert.Equal(SessionRejectEncoder.TerminationCode.InvalidSofh, code);
    }

    [Fact]
    public async Task UnknownTemplate_SendsUnrecognizedMessageTerminate()
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)frame.Length,
            blockLength: 82, templateId: 9999, version: 2);

        byte code = await SendBadFrameExpectingTerminateAsync(frame);
        Assert.Equal(SessionRejectEncoder.TerminationCode.UnrecognizedMessage, code);
    }

    [Fact]
    public async Task BlockLengthMismatch_SendsDecodingErrorTerminate()
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 80];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)frame.Length,
            blockLength: 80, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        byte code = await SendBadFrameExpectingTerminateAsync(frame);
        Assert.Equal(SessionRejectEncoder.TerminationCode.DecodingError, code);
    }

    [Fact]
    public async Task VarDataOverflow_SendsDecodingErrorTerminate()
    {
        // SimpleNewOrder with valid header but a memo varData field whose
        // declared length exceeds the schema cap (40). Frame is sized so
        // SOFH messageLength matches.
        const int blockLen = 82;
        const int badMemoLen = 200; // > MemoEncoding maxValue (40)
        int total = EntryPointFrameReader.WireHeaderSize + blockLen + 1 + badMemoLen;
        var frame = new byte[total];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)total,
            blockLength: blockLen, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        // Fill fixed block with a valid-enough body so per-field decode would
        // succeed if it ran (Side='1', OrdType='2', TIF='0').
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize, blockLen);
        body[56] = (byte)'1'; // Side
        body[57] = (byte)'2'; // OrdType
        body[58] = (byte)'0'; // TIF
        // varData: length prefix 200, payload bytes follow.
        frame[EntryPointFrameReader.WireHeaderSize + blockLen] = badMemoLen;

        byte code = await SendBadFrameExpectingTerminateAsync(frame);
        Assert.Equal(SessionRejectEncoder.TerminationCode.DecodingError, code);
    }
}
