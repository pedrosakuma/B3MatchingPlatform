using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

public class FixpSessionMassActionTakeoverTests
{
    private sealed class ControlledSink : IInboundCommandSink
    {
        public TaskCompletionSource<Action<MassCancelOutcome>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;

        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm,
            Action<MassCancelOutcome> onCompleted)
        {
            Completion.TrySetResult(onCompleted);
            return true;
        }

        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    [Fact]
    public async Task TakeoverDuringMassCancel_RoutesTerminalReportToReplacementAfterCancelEr()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        await using var listener = BuildListener(sink, registry, claims);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        await oldClient.GetStream().WriteAsync(BuildMassActionRequest(clOrdId: 7001));
        var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var replacementClient = await ConnectAndEstablishAsync(listener, sessionVerId: 3);
        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var canceled = new OrderCanceledEvent(
            SecurityId: 123,
            OrderId: 55,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 1,
            Reason: CancelReason.MassCancel,
            RptSeq: 1);

        Assert.True(gateway.WriteExecutionReportPassiveCancel(
            new SessionId("1"),
            ownerClOrdId: 5001,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 7001));
        complete(MassCancelOutcome.Completed(1));

        var cancel = await ReadOneFrameAsync(replacementClient.GetStream());
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel.TemplateId);
        var report = await ReadOneFrameAsync(replacementClient.GetStream());
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal(7001UL,
            BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8)));
    }

    [Fact]
    public async Task CompletionFromClosedLogicalSession_DoesNotRouteToLaterSessionReuse()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        await using var listener = BuildListener(sink, registry, claims);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        await oldClient.GetStream().WriteAsync(BuildMassActionRequest(clOrdId: 7002));
        var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        listener.ActiveSessions.Single(s => s.SessionId == 1).Close("test-logical-session-ended");
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.All(s => s.SessionId != 1),
            TimeSpan.FromSeconds(5)));

        using var unrelatedClient = await ConnectAndEstablishAsync(listener, sessionVerId: 3);
        complete(MassCancelOutcome.Completed(0));

        await AssertNoFrameAsync(unrelatedClient.GetStream());
    }

    private static EntryPointListener BuildListener(
        IInboundCommandSink sink,
        SessionRegistry registry,
        SessionClaimRegistry claims)
    {
        var firms = new FirmRegistry(
            new[] { new Firm("F1", "Firm 1", 42) },
            new[]
            {
                new SessionCredential("1", "F1", "",
                    AllowedSourceCidrs: null, Policy: SessionPolicy.Default),
            });
        return new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
                SendingTimeSkewToleranceNs = 0,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0));
    }

    private static async Task<TcpClient> ConnectAndEstablishAsync(
        EntryPointListener listener,
        ulong sessionVerId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream = client.GetStream();
        var credentials = Encoding.UTF8.GetBytes(
            "{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buffer = new byte[512];

        int length = EntryPointFixpFrameCodec.EncodeNegotiate(buffer,
            sessionId: 1,
            sessionVerId: sessionVerId,
            timestampNanos: 0,
            enteringFirm: 42,
            onBehalfFirm: null,
            credentials: credentials,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(buffer.AsMemory(0, length));
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadOneFrameAsync(stream)).TemplateId);

        length = EntryPointFixpFrameCodec.EncodeEstablish(buffer,
            sessionId: 1,
            sessionVerId: sessionVerId,
            timestampNanos: 0,
            keepAliveIntervalMillis: 10_000,
            nextSeqNo: 1,
            cancelOnDisconnectType: 0,
            codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(buffer.AsMemory(0, length));
        Assert.Equal(EntryPointFrameReader.TidEstablishAck,
            (await ReadOneFrameAsync(stream)).TemplateId);
        return client;
    }

    private static byte[] BuildMassActionRequest(ulong clOrdId)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 52];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 52,
            templateId: EntryPointFrameReader.TidOrderMassActionRequest,
            version: 6);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), 1);
        body[18] = 3;
        body[19] = 255;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        body[28] = 255;
        body[46] = (byte)'B';
        body[47] = (byte)'V';
        body[48] = (byte)'M';
        body[49] = (byte)'F';
        return frame;
    }

    private readonly record struct ReadFrame(ushort TemplateId, byte[] Body);

    private static async Task<ReadFrame> ReadOneFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        var body = new byte[messageLength - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, body);
    }

    private static async Task AssertNoFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var oneByte = new byte[1];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadExactlyAsync(oneByte, cts.Token));
    }

    private static async Task ReadExactAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count <= 0) throw new EndOfStreamException();
            read += count;
        }
    }
}
