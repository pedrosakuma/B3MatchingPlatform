using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

public class SessionRegistryHandoffTests
{
    private sealed class NoOpSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;
        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private sealed class StrictJournal : IFixpOutboundJournal
    {
        private readonly SortedDictionary<uint, OutboundJournalEntry> _entries = new();

        public IReadOnlyList<OutboundJournalEntry> Entries => _entries.Values.ToArray();

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
        {
            if (_entries.Count > 0 && seq <= _entries.Keys.Max())
                throw new InvalidOperationException("outbound sequence must be strictly monotonic");
            _entries.Add(seq, new OutboundJournalEntry(seq, timestampNanos, frame.ToArray()));
        }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }
        public void PruneUpTo(uint sessionId, uint uptoSeq) { }
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
            => _entries.Values.Where(entry => entry.Seq >= fromSeq).Take(count).ToArray();
        public uint MaxSeq(uint sessionId) => _entries.Count == 0 ? 0u : _entries.Keys.Max();
        public long EntryCount(uint sessionId) => _entries.Count;
        public void Remove(uint sessionId) => _entries.Clear();
        public IReadOnlyCollection<uint> ListSessions() => _entries.Count == 0 ? Array.Empty<uint>() : new[] { 1u };
        public void Dispose() { }
    }

    private sealed class MemoryStatePersister : IFixpSessionStatePersister
    {
        private FixpSessionStateSnapshot? _snapshot;

        public void Save(in FixpSessionStateSnapshot snapshot) => _snapshot = snapshot;
        public FixpSessionStateSnapshot? Load(uint sessionId)
            => _snapshot is { } snapshot && snapshot.SessionId == sessionId
                ? snapshot
                : null;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => _snapshot is { } snapshot
                ? new[] { snapshot }
                : Array.Empty<FixpSessionStateSnapshot>();
        public void Remove(uint sessionId)
        {
            if (_snapshot?.SessionId == sessionId)
                _snapshot = null;
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task FastTerminateReconnect_ReplacesRetiredRoute_AndOldCleanupCannotRemoveReplacement()
    {
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var sink = new NoOpSink();
        var (oldServer, oldClient) = await ConnectPairAsync();
        var (replacementServer, replacementClient) = await ConnectPairAsync();
        await using var oldSession = NewSession(1, oldServer, sink, registry);
        await using var replacement = NewSession(2, replacementServer, sink, registry);
        oldSession.Start();
        replacement.Start();

        registry.Register(oldSession);
        var deferredRoute = registry.CaptureRoute(oldSession);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            claims.TryClaim(1, 2, oldSession));

        oldSession.ApplyTransition(FixpEvent.Terminate);
        claims.Release(1, oldSession);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            claims.TryClaim(1, 3, replacement));
        replacement.ApplyTransition(FixpEvent.Negotiate);

        Assert.True(registry.TryUpdateIdentity(
            replacement,
            new SessionId("pending-2"),
            new SessionId("1"),
            claims,
            claimedSessionId: 1,
            replaceRetired: true));

        registry.Deregister(oldSession);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(new SessionId("1"), out var registered));
        Assert.Same(replacement, registered);
        Assert.True(claims.TryGetActiveClaim(1, out var holder, out var version));
        Assert.Same(replacement, holder);
        Assert.Equal(3UL, version);

        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var canceled = CreateCancel(orderId: 51, rptSeq: 1);
        Assert.True(gateway.WriteExecutionReportPassiveCancel(
            new SessionId("1"),
            ownerClOrdId: 5001,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 7001).IsCommitted);
        Assert.True(deferredRoute.TryInvoke(target =>
            target.WriteOrderMassActionReport(
                clOrdIdValue: 7001,
                massActionResponse: OrderMassActionReportEncoder.MassActionResponseAccepted,
                massActionRejectReason: null,
                side: (byte)'1',
                securityId: 123,
                transactTimeNanos: 2)).IsCommitted);

        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel,
            (await ReadFrameAsync(replacementClient.GetStream())).TemplateId);
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport,
            (await ReadFrameAsync(replacementClient.GetStream())).TemplateId);

        oldClient.Close();
        replacementClient.Close();
    }

    [Fact]
    public async Task FastTerminalReconnect_ResetsJournalBeforeReplacementEmission_AndDisposesRetiredRetx()
    {
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var journal = new StrictJournal();
        var statePersister = new MemoryStatePersister();
        var sink = new NoOpSink();
        var (oldServer, oldClient) = await ConnectPairAsync();
        var (replacementServer, replacementClient) = await ConnectPairAsync();
        await using var oldSession = NewSession(
            20, oldServer, sink, registry, journal, statePersister);
        await using var replacement = NewSession(
            21, replacementServer, sink, registry, journal, statePersister);
        oldSession.Start();
        replacement.Start();
        oldSession.ApplyTransition(FixpEvent.Negotiate);
        oldSession.ApplyTransition(FixpEvent.Establish);
        registry.Register(oldSession);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            claims.TryClaim(1, 2, oldSession));

        Assert.True(oldSession.WriteOrderMassActionReport(
            clOrdIdValue: 7100,
            massActionResponse: OrderMassActionReportEncoder.MassActionResponseAccepted,
            massActionRejectReason: null,
            side: (byte)'1',
            securityId: 123,
            transactTimeNanos: 1).IsCommitted);
        Assert.Equal(1u, (await ReadFrameAsync(oldClient.GetStream())).MsgSeqNum);
        Assert.Equal(1, oldSession.RetxBufferDepth);
        Assert.Equal(1u, journal.MaxSeq(1));

        oldSession.ApplyTransition(FixpEvent.Terminate);
        claims.Release(1, oldSession);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            claims.TryClaim(1, 3, replacement));
        replacement.ApplyTransition(FixpEvent.Negotiate);
        Assert.True(registry.TryUpdateIdentity(
            replacement,
            new SessionId("pending-21"),
            new SessionId("1"),
            claims,
            claimedSessionId: 1,
            replaceRetired: true));
        replacement.ApplyTransition(FixpEvent.Establish);

        Assert.True(replacement.WriteOrderMassActionReport(
            clOrdIdValue: 7101,
            massActionResponse: OrderMassActionReportEncoder.MassActionResponseAccepted,
            massActionRejectReason: null,
            side: (byte)'1',
            securityId: 123,
            transactTimeNanos: 2).IsCommitted);
        var replacementFrame = await ReadFrameAsync(replacementClient.GetStream());
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, replacementFrame.TemplateId);
        Assert.Equal(1u, replacementFrame.MsgSeqNum);
        replacement.SaveStateSnapshotSafe();

        oldSession.Close("peer-terminate-delayed-cleanup", CloseKind.PeerTerminate);

        Assert.Equal(0, oldSession.RetxBufferDepth);
        var persisted = Assert.Single(journal.Entries);
        Assert.Equal(1u, persisted.Seq);
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport,
            BinaryPrimitives.ReadUInt16LittleEndian(
                persisted.Frame.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));
        Assert.NotNull(statePersister.Load(1));

        oldClient.Close();
        replacementClient.Close();
    }

    [Fact]
    public async Task ActiveTakeover_KeepsOldRouteAuthoritative_UntilCommit()
    {
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var sink = new NoOpSink();
        var (oldServer, oldClient) = await ConnectPairAsync();
        var (replacementServer, replacementClient) = await ConnectPairAsync();
        await using var oldSession = NewSession(10, oldServer, sink, registry);
        await using var replacement = NewSession(11, replacementServer, sink, registry);
        oldSession.Start();
        replacement.Start();
        oldSession.ApplyTransition(FixpEvent.Negotiate);
        oldSession.ApplyTransition(FixpEvent.Establish);

        registry.Register(oldSession);
        var deferredRoute = registry.CaptureRoute(oldSession);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            claims.TryClaim(1, 2, oldSession));
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            claims.TryForceTakeOver(1, 3, replacement, out var evicted));
        Assert.Same(oldSession, evicted);

        Assert.True(registry.TryUpdateIdentity(
            replacement,
            new SessionId("pending-11"),
            new SessionId("1"),
            claims,
            claimedSessionId: 1,
            replaceRetired: false));

        Assert.True(registry.TryGet(new SessionId("1"), out var registered));
        Assert.Same(oldSession, registered);

        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var canceled = CreateCancel(orderId: 52, rptSeq: 2);
        Assert.True(gateway.WriteExecutionReportPassiveCancel(
            new SessionId("1"),
            ownerClOrdId: 5002,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 7002).IsCommitted);
        Assert.True(deferredRoute.TryInvoke(target =>
            target.WriteOrderMassActionReport(
                clOrdIdValue: 7002,
                massActionResponse: OrderMassActionReportEncoder.MassActionResponseAccepted,
                massActionRejectReason: null,
                side: (byte)'1',
                securityId: 123,
                transactTimeNanos: 3)).IsCommitted);

        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel,
            (await ReadFrameAsync(oldClient.GetStream())).TemplateId);
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport,
            (await ReadFrameAsync(oldClient.GetStream())).TemplateId);
        Assert.Equal(0, replacementClient.Available);

        oldClient.Close();
        replacementClient.Close();
    }

    private static FixpSession NewSession(
        long connectionId,
        NetworkStream stream,
        IInboundCommandSink sink,
        SessionRegistry registry,
        IFixpOutboundJournal? outboundJournal = null,
        IFixpSessionStatePersister? statePersister = null)
        => new(
            connectionId,
            enteringFirm: 42,
            sessionId: 1,
            stream,
            sink,
            NullLogger<FixpSession>.Instance,
            outboundJournal: outboundJournal,
            statePersister: statePersister,
            sessionRegistry: registry);

    private static OrderCanceledEvent CreateCancel(long orderId, uint rptSeq) =>
        new(
            SecurityId: 123,
            OrderId: orderId,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 1,
            Reason: CancelReason.MassCancel,
            RptSeq: rptSeq);

    private static async Task<(NetworkStream Server, TcpClient Client)> ConnectPairAsync()
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
        return (new NetworkStream(serverSocket, ownsSocket: true), client);
    }

    private readonly record struct ReadFrame(ushort TemplateId, uint MsgSeqNum);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        var body = new byte[messageLength - EntryPointFrameReader.WireHeaderSize];
        await stream.ReadExactlyAsync(body, cts.Token);
        uint msgSeqNum = body.Length >= 8
            ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4))
            : 0u;
        return new ReadFrame(templateId, msgSeqNum);
    }
}
