using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
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
        SessionRegistry registry)
        => new(
            connectionId,
            enteringFirm: 42,
            sessionId: 1,
            stream,
            sink,
            NullLogger<FixpSession>.Instance,
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

    private readonly record struct ReadFrame(ushort TemplateId);

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
        return new ReadFrame(templateId);
    }
}
