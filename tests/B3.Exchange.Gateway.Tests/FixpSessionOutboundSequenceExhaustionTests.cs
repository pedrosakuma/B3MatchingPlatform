using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Gateway.Tests;

public sealed class FixpSessionOutboundSequenceExhaustionTests
{
    private sealed class ControlledSink : IInboundCommandSink
    {
        public TaskCompletionSource<Action<MassCancelOutcome>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SessionClosedCalls { get; private set; }

        public bool EnqueueNewOrder(
            in NewOrderCommand cmd,
            ContractsSessionId session,
            uint enteringFirm,
            ulong clOrdIdValue) => true;

        public bool EnqueueCancel(
            in CancelOrderCommand cmd,
            ContractsSessionId session,
            uint enteringFirm,
            ulong clOrdIdValue,
            ulong origClOrdIdValue) => true;

        public bool EnqueueReplace(
            in ReplaceOrderCommand cmd,
            ContractsSessionId session,
            uint enteringFirm,
            ulong clOrdIdValue,
            ulong origClOrdIdValue) => true;

        public bool EnqueueCross(
            in CrossOrderCommand cmd,
            ContractsSessionId session,
            uint enteringFirm) => true;

        public bool EnqueueMassCancel(
            in MassCancelCommand cmd,
            ContractsSessionId session,
            uint enteringFirm) => true;

        public bool EnqueueMassCancel(
            in MassCancelCommand cmd,
            ContractsSessionId session,
            uint enteringFirm,
            Action<MassCancelOutcome> onCompleted)
        {
            Completion.TrySetResult(onCompleted);
            return true;
        }

        public void OnDecodeError(ContractsSessionId session, string error) { }

        public void OnSessionClosed(ContractsSessionId session)
            => SessionClosedCalls++;
    }

    private sealed class RecordingJournal : IFixpOutboundJournal
    {
        private readonly Dictionary<uint, SortedDictionary<uint, byte[]>> _entries = new();

        public List<uint> AppendAttempts { get; } = new();
        public int RemoveCalls { get; private set; }

        public void Append(
            uint sessionId,
            uint seq,
            long timestampNanos,
            ReadOnlySpan<byte> frame)
        {
            AppendAttempts.Add(seq);
            if (seq == 0)
                throw new InvalidOperationException("sequence zero must never be journaled");
            if (!_entries.TryGetValue(sessionId, out var sessionEntries))
                _entries[sessionId] = sessionEntries = new();
            sessionEntries.Add(seq, frame.ToArray());
        }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }

        public void PruneUpTo(uint sessionId, uint uptoSeq) { }

        public IReadOnlyList<OutboundJournalEntry> ReadRange(
            uint sessionId,
            uint fromSeq,
            int count)
        {
            if (!_entries.TryGetValue(sessionId, out var sessionEntries))
                return [];
            return sessionEntries
                .Where(entry => entry.Key >= fromSeq)
                .Take(count)
                .Select(entry => new OutboundJournalEntry(
                    entry.Key,
                    0,
                    entry.Value))
                .ToArray();
        }

        public uint MaxSeq(uint sessionId)
            => _entries.TryGetValue(sessionId, out var sessionEntries)
                && sessionEntries.Count > 0
                ? sessionEntries.Keys.Max()
                : 0;

        public long EntryCount(uint sessionId)
            => _entries.TryGetValue(sessionId, out var sessionEntries)
                ? sessionEntries.Count
                : 0;

        public void RollGeneration(uint sessionId)
            => _entries.Remove(sessionId);
        public void RestoreLatestGeneration(uint sessionId) { }
        public void ReleaseActive(uint sessionId) { }

        public void EnforceRetention(uint sessionId, long nowNanos) { }

        public void Remove(uint sessionId)
        {
            RemoveCalls++;
            _entries.Remove(sessionId);
        }

        public IReadOnlyCollection<uint> ListSessions()
            => _entries.Keys.ToArray();

        public void Dispose() { }
    }

    private sealed class RecordingStatePersister : IFixpSessionStatePersister
    {
        private readonly Dictionary<uint, FixpSessionStateSnapshot> _entries = new();

        public List<uint> SavedOutboundSequences { get; } = new();
        public int RemoveCalls { get; private set; }

        public void Seed(in FixpSessionStateSnapshot snapshot)
            => _entries[snapshot.SessionId] = snapshot;

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            SavedOutboundSequences.Add(snapshot.OutboundMsgSeqNum);
            _entries[snapshot.SessionId] = snapshot;
        }

        public FixpSessionStateSnapshot? Load(uint sessionId)
            => _entries.TryGetValue(sessionId, out var snapshot)
                ? snapshot
                : null;

        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => _entries.Values.ToArray();

        public void Remove(uint sessionId)
        {
            RemoveCalls++;
            _entries.Remove(sessionId);
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task MassActionCompletion_AtSequenceExhaustion_ClosesOnceWithoutSeqZero_AndFreshSessionRestartsAtOne()
    {
        var sink = new ControlledSink();
        var journal = new RecordingJournal();
        var state = new RecordingStatePersister();
        var persisted = ExhaustedState();
        state.Seed(persisted);
        var registry = new SessionRegistry();
        var (server, client) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 1,
            enteringFirm: 7,
            sessionId: 100,
            stream: server,
            sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            outboundJournal: journal,
            statePersister: state,
            persistedState: persisted,
            onClosed: (closed, _) => registry.Deregister(closed),
            sessionRegistry: registry);
        registry.Register(session);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);

        try
        {
            await client.GetStream().WriteAsync(
                BuildMassActionRequest(clOrdId: 7001, msgSeqNum: 9));
            var complete = await sink.Completion.Task.WaitAsync(
                TimeSpan.FromSeconds(3));

            complete(MassCancelOutcome.Completed(1));

            Assert.False(session.IsRegistered);
            Assert.Equal(uint.MaxValue, session.OutboundSeq);
            Assert.Equal(1, sink.SessionClosedCalls);
            Assert.Equal(0, registry.Count);
            Assert.Equal(0, registry.PendingWriteCount(session));
            Assert.Empty(journal.AppendAttempts);
            Assert.Equal(0, session.RetxBufferDepth);
            Assert.Equal(1, journal.RemoveCalls);
            Assert.Equal(1, state.RemoveCalls);
            Assert.Empty(state.SavedOutboundSequences);
            await Task.Delay(100);
            Assert.Equal(0, client.Available);

            await using var recovered = new FixpSession(
                connectionId: 2,
                enteringFirm: 7,
                sessionId: 100,
                stream: new MemoryStream(),
                sink: new ControlledSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state,
                persistedState: state.Load(100));
            recovered.ApplyTransition(FixpEvent.Negotiate);
            recovered.ApplyTransition(FixpEvent.Establish);

            var result = recovered.WriteOrderMassActionReport(
                clOrdIdValue: 7002,
                massActionResponse:
                    OrderMassActionReportEncoder.MassActionResponseAccepted,
                massActionRejectReason: null,
                side: null,
                securityId: 0,
                transactTimeNanos: 2);

            Assert.True(result.IsCommitted);
            Assert.Equal(1u, recovered.OutboundSeq);
            Assert.Equal([1u], journal.AppendAttempts);
            Assert.DoesNotContain(0u, journal.AppendAttempts);
        }
        finally
        {
            await session.DisposeAsync();
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public void PassiveCancel_AtSequenceExhaustion_ClosesWithoutFrameJournalOrRetry()
    {
        var sink = new ControlledSink();
        var journal = new RecordingJournal();
        var state = new RecordingStatePersister();
        var persisted = ExhaustedState();
        state.Seed(persisted);
        var registry = new SessionRegistry();
        var session = new FixpSession(
            connectionId: 3,
            enteringFirm: 7,
            sessionId: 100,
            stream: new MemoryStream(),
            sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            outboundJournal: journal,
            statePersister: state,
            persistedState: persisted,
            onClosed: (closed, _) => registry.Deregister(closed),
            sessionRegistry: registry);
        registry.Register(session);
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        var router = new GatewayRouter(
            registry,
            NullLogger<GatewayRouter>.Instance);
        var canceled = new OrderCanceledEvent(
            SecurityId: 1001,
            OrderId: 99,
            Side: Side.Sell,
            PriceMantissa: 1_010_000,
            RemainingQuantityAtCancel: 25,
            TransactTimeNanos: 1,
            Reason: CancelReason.MassCancel,
            RptSeq: 2);

        var result = router.WriteExecutionReportPassiveCancel(
            session.Identity,
            ownerClOrdId: 5001,
            orderId: 99,
            canceled,
            requesterClOrdIdOrZero: 7001);
        var repeated = router.WriteExecutionReportPassiveCancel(
            session.Identity,
            ownerClOrdId: 5001,
            orderId: 99,
            canceled,
            requesterClOrdIdOrZero: 7001);

        Assert.False(result.IsAccepted);
        Assert.False(repeated.IsAccepted);
        Assert.False(session.IsRegistered);
        Assert.Equal(uint.MaxValue, session.OutboundSeq);
        Assert.Equal(1, sink.SessionClosedCalls);
        Assert.Equal(0, registry.Count);
        Assert.Empty(journal.AppendAttempts);
        Assert.Equal(0, session.RetxBufferDepth);
        Assert.Equal(1, journal.RemoveCalls);
        Assert.Equal(1, state.RemoveCalls);
        Assert.Empty(state.SavedOutboundSequences);
    }

    [Fact]
    public async Task EstablishAtSequenceExhaustion_RejectsWithoutAckNextSeqZero()
    {
        var sink = new ControlledSink();
        var journal = new RecordingJournal();
        var state = new RecordingStatePersister();
        var persisted = ExhaustedState();
        state.Seed(persisted);
        var (server, client) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 4,
            enteringFirm: 7,
            sessionId: 100,
            stream: server,
            sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            outboundJournal: journal,
            statePersister: state,
            persistedState: persisted);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);

        try
        {
            var establish = new byte[256];
            int length = EntryPointFixpFrameCodec.EncodeEstablish(
                establish,
                sessionId: 100,
                sessionVerId: 0,
                timestampNanos: 1,
                keepAliveIntervalMillis: 10_000,
                nextSeqNo: 1,
                cancelOnDisconnectType: 0,
                codTimeoutWindowMillis: 0,
                credentials: ReadOnlySpan<byte>.Empty);
            await client.GetStream().WriteAsync(
                establish.AsMemory(0, length));

            var reject = await ReadFrameAsync(client.GetStream());
            var terminate = await ReadFrameAsync(client.GetStream());

            Assert.Equal(EstablishRejectEncoder.TemplateId, reject.TemplateId);
            Assert.Equal(EntryPointFrameReader.TidTerminate, terminate.TemplateId);
            Assert.NotEqual(EntryPointFrameReader.TidEstablishAck, reject.TemplateId);
            Assert.False(session.IsRegistered);
            Assert.Empty(journal.AppendAttempts);
            Assert.DoesNotContain(0u, state.SavedOutboundSequences);
        }
        finally
        {
            await session.DisposeAsync();
            client.Dispose();
            server.Dispose();
        }
    }

    private static FixpSessionStateSnapshot ExhaustedState()
        => new(
            SessionId: 100,
            SessionVerId: 0,
            OutboundMsgSeqNum: uint.MaxValue,
            LastIncomingSeqNo: 0,
            EnteringFirm: 7,
            UpdatedAtNanos: 0);

    private static byte[] BuildMassActionRequest(
        ulong clOrdId,
        uint msgSeqNum)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 52];
        EntryPointFrameReader.WriteHeader(
            frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 52,
            templateId: EntryPointFrameReader.TidOrderMassActionRequest,
            version: 6);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), msgSeqNum);
        body[18] = 3;
        body[19] = 255;
        BinaryPrimitives.WriteUInt64LittleEndian(
            body.Slice(20, 8),
            clOrdId);
        body[28] = 255;
        body[46] = (byte)'B';
        body[47] = (byte)'V';
        body[48] = (byte)'M';
        body[49] = (byte)'F';
        return frame;
    }

    private static async Task<(NetworkStream ServerSide, TcpClient Client)>
        ConnectPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync(
            IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port);
        var serverSocket = await listener.AcceptSocketAsync();
        await connect;
        listener.Stop();
        return (
            new NetworkStream(serverSocket, ownsSocket: true),
            client);
    }

    private readonly record struct ReadFrame(
        ushort TemplateId,
        byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(3));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        var body = new byte[
            messageLength - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, body);
    }

    private static async Task ReadExactAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(
                buffer.AsMemory(read),
                cancellationToken);
            if (count <= 0)
                throw new EndOfStreamException();
            read += count;
        }
    }
}
