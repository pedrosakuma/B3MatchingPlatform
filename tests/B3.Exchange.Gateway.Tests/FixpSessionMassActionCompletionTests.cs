using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

public class FixpSessionMassActionCompletionTests
{
    private sealed class ControlledSink(bool enqueueResult = true) : IInboundCommandSink
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
            if (!enqueueResult) return false;
            Completion.TrySetResult(onCompleted);
            return true;
        }

        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private sealed class FailFirstAppendJournal : IFixpOutboundJournal
    {
        private int _appendCalls;
        public List<OutboundJournalEntry> Entries { get; } = new();

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
        {
            if (Interlocked.Increment(ref _appendCalls) == 1)
                throw new IOException("injected first append failure");
            Entries.Add(new OutboundJournalEntry(seq, timestampNanos, frame.ToArray()));
        }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }
        public void PruneUpTo(uint sessionId, uint uptoSeq) { }
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
            => Entries.Where(entry => entry.Seq >= fromSeq).Take(count).ToArray();
        public uint MaxSeq(uint sessionId) => Entries.Count == 0 ? 0u : Entries.Max(entry => entry.Seq);
        public long EntryCount(uint sessionId) => Entries.Count;
        public void RollGeneration(uint sessionId) => Entries.Clear();
        public void RestoreLatestGeneration(uint sessionId) { }
        public void ReleaseActive(uint sessionId) { }
        public void EnforceRetention(uint sessionId, long nowNanos) { }
        public void Remove(uint sessionId) => Entries.Clear();
        public IReadOnlyCollection<uint> ListSessions() => Array.Empty<uint>();
        public void Dispose() { }
    }

    [Fact]
    public async Task AcceptedReport_IsDeferredUntilTerminalCompletion()
    {
        var sink = new ControlledSink();
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = StartSession(server, sink);
            await client.GetStream().WriteAsync(BuildRequest(clOrdId: 7001, msgSeqNum: 9));

            var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            Assert.Equal(0, client.Available);

            complete(MassCancelOutcome.Completed(2));

            var report = await ReadFrameAsync(client.GetStream());
            Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
            Assert.Equal((byte)'1', report.Body[44]);
            Assert.Equal(7001UL,
                BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8)));
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task TerminalFailure_EmitsSystemBusyWithoutAcceptedReport()
    {
        var sink = new ControlledSink();
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = StartSession(server, sink);
            await client.GetStream().WriteAsync(BuildRequest(clOrdId: 7002, msgSeqNum: 10));

            var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            complete(MassCancelOutcome.SystemBusy);

            var reject = await ReadFrameAsync(client.GetStream());
            AssertSystemBusy(reject, expectedRefSeqNum: 10, expectedClOrdId: 7002);
            await Task.Delay(100);
            Assert.Equal(0, client.Available);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task EnqueueFailure_EmitsSystemBusyWithoutAcceptedReport()
    {
        var sink = new ControlledSink(enqueueResult: false);
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = StartSession(server, sink);
            await client.GetStream().WriteAsync(BuildRequest(clOrdId: 7003, msgSeqNum: 11));

            var reject = await ReadFrameAsync(client.GetStream());
            AssertSystemBusy(reject, expectedRefSeqNum: 11, expectedClOrdId: 7003);
            await Task.Delay(100);
            Assert.Equal(0, client.Available);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task AcceptedReportCommitFailure_EmitsSystemBusyAtSameSequence()
    {
        var sink = new ControlledSink();
        var journal = new FailFirstAppendJournal();
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = StartSession(server, sink, journal);
            await client.GetStream().WriteAsync(BuildRequest(clOrdId: 7004, msgSeqNum: 12));

            var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            complete(MassCancelOutcome.Completed(1));

            var reject = await ReadFrameAsync(client.GetStream());
            AssertSystemBusy(reject, expectedRefSeqNum: 12, expectedClOrdId: 7004);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(
                reject.Body.AsSpan(4, 4)));
            var persisted = Assert.Single(journal.Entries);
            Assert.Equal(1u, persisted.Seq);
            Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject,
                BinaryPrimitives.ReadUInt16LittleEndian(
                    persisted.Frame.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));
            Assert.Equal(1, session.RetxBufferDepth);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalCompletionDuringReattach_DefersUntilEstablishAck(
        bool succeeded)
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var (server1, client1) = await ConnectPairAsync();
        var session = StartSession(server1, sink, sessionRegistry: registry);
        registry.Register(session);
        try
        {
            await client1.GetStream().WriteAsync(BuildRequest(clOrdId: 7005, msgSeqNum: 9));
            var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3));

            client1.Close();
            Assert.True(await TestUtil.WaitUntilAsync(
                () => session.State == FixpState.Suspended,
                TimeSpan.FromSeconds(3)));

            var (server2, client2) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(server2));

                complete(succeeded
                    ? MassCancelOutcome.Completed(0)
                    : MassCancelOutcome.SystemBusy);
                Assert.Equal(1, registry.PendingWriteCount(session));
                Assert.Equal(0u, session.OutboundSeq);

                var establish = new byte[256];
                int establishLength = EntryPointFixpFrameCodec.EncodeEstablish(
                    establish,
                    sessionId: 100,
                    sessionVerId: 0,
                    timestampNanos: 1,
                    keepAliveIntervalMillis: 10_000,
                    nextSeqNo: 10,
                    cancelOnDisconnectType: 0,
                    codTimeoutWindowMillis: 0,
                    credentials: ReadOnlySpan<byte>.Empty);
                var stream2 = client2.GetStream();
                await stream2.WriteAsync(establish.AsMemory(0, establishLength));

                var ack = await ReadFrameAsync(stream2);
                Assert.Equal(EntryPointFrameReader.TidEstablishAck, ack.TemplateId);
                Assert.Equal(1u,
                    BinaryPrimitives.ReadUInt32LittleEndian(ack.Body.AsSpan(28, 4)));

                var terminal = await ReadFrameAsync(stream2);
                Assert.Equal(1u,
                    BinaryPrimitives.ReadUInt32LittleEndian(terminal.Body.AsSpan(4, 4)));
                if (succeeded)
                {
                    Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport,
                        terminal.TemplateId);
                    Assert.Equal(7005UL,
                        BinaryPrimitives.ReadUInt64LittleEndian(terminal.Body.AsSpan(20, 8)));
                }
                else
                {
                    AssertSystemBusy(terminal,
                        expectedRefSeqNum: 9,
                        expectedClOrdId: 7005);
                }

                Assert.Equal(1u, session.OutboundSeq);
            }
            finally
            {
                client2.Close();
            }
        }
        finally
        {
            session.Close("test");
            await session.DisposeAsync();
            client1.Close();
            server1.Dispose();
        }
    }

    private static FixpSession StartSession(
        NetworkStream server,
        IInboundCommandSink sink,
        IFixpOutboundJournal? outboundJournal = null,
        SessionRegistry? sessionRegistry = null)
    {
        var session = new FixpSession(
            connectionId: 1,
            enteringFirm: 7,
            sessionId: 100,
            stream: server,
            sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            outboundJournal: outboundJournal,
            sessionRegistry: sessionRegistry);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        return session;
    }

    private static byte[] BuildRequest(ulong clOrdId, uint msgSeqNum)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 52];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 52,
            templateId: EntryPointFrameReader.TidOrderMassActionRequest,
            version: 6);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), msgSeqNum);
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

    private static byte[] BuildRetransmitRequest(
        uint sessionId, ulong timestampNanos, uint fromSeqNo, uint count)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 20];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)frame.Length,
            blockLength: 20,
            templateId: EntryPointFrameReader.TidRetransmitRequest,
            version: 0);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), timestampNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(12, 4), fromSeqNo);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(16, 4), count);
        return frame;
    }

    private static void AssertSystemBusy(ReadFrame frame, uint expectedRefSeqNum, ulong expectedClOrdId)
    {
        Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, frame.TemplateId);
        Assert.Equal((byte)29, frame.Body[18]);
        Assert.Equal(expectedRefSeqNum,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(20, 4)));
        Assert.Equal(expectedClOrdId,
            BinaryPrimitives.ReadUInt64LittleEndian(frame.Body.AsSpan(24, 8)));
        Assert.Equal(BusinessMessageRejectEncoder.Reason.SystemBusy,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(32, 4)));
    }

    private static async Task<(NetworkStream ServerSide, TcpClient Client)> ConnectPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port);
        var serverSocket = await listener.AcceptSocketAsync();
        await connect;
        listener.Stop();
        return (new NetworkStream(serverSocket, ownsSocket: true), client);
    }

    private readonly record struct ReadFrame(ushort TemplateId, byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        var body = new byte[messageLength - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, body);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
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
