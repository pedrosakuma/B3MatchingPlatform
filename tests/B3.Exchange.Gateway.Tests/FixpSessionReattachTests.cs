using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #69b-2: <see cref="FixpSession.TryReattach"/> must accept a
/// fresh transport only when the session is currently Suspended (and
/// open and not already attached). Negative cases must leave session
/// state untouched and return false so the listener closes the new
/// socket.
/// </summary>
public class FixpSessionReattachTests
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

    private static async Task<(TcpListener tcp, NetworkStream serverSide, TcpClient client)> ConnectPairAsync()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)tcp.LocalEndpoint).Port);
        var serverSock = await tcp.AcceptSocketAsync();
        await connectTask;
        return (tcp, new NetworkStream(serverSock, ownsSocket: true), client);
    }

    private static async Task<FixpSession> NewSuspendedSessionAsync()
    {
        var (_, serverStream, client) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: serverStream, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        client.Close();
        await TestUtil.WaitUntilAsync(
            () => session.State == FixpState.Suspended && session.SuspendedSinceMs is not null,
            TimeSpan.FromSeconds(3));
        Assert.Equal(FixpState.Suspended, session.State);
        return session;
    }

    [Fact]
    public async Task TryReattach_returns_false_when_not_suspended()
    {
        var (_, serverStream, client) = await ConnectPairAsync();
        try
        {
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 7, sessionId: 100,
                stream: serverStream, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance);
            session.Start();
            // Still Idle / attached → re-attach must refuse.
            using var fakeStream = new MemoryStream();
            Assert.False(session.TryReattach(fakeStream));
            session.Close("test-cleanup");
        }
        finally { client.Close(); }
    }

    [Fact]
    public async Task TryReattach_returns_false_after_close()
    {
        var session = await NewSuspendedSessionAsync();
        session.Close("test-close");
        using var fakeStream = new MemoryStream();
        Assert.False(session.TryReattach(fakeStream));
    }

    [Fact]
    public async Task TryReattach_clears_SuspendedSinceMs_and_marks_attached()
    {
        var session = await NewSuspendedSessionAsync();
        try
        {
            // Provide a stream the new recv loop can read from; we won't
            // feed it a real Establish frame but we don't need to — we
            // only assert TryReattach's pre/post conditions. The loop
            // will block on the empty stream until we close the session.
            var (_, serverStream, client) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream));
                Assert.True(session.IsAttached);
                Assert.Null(session.SuspendedSinceMs);
                // State machine still Suspended until the recv loop
                // processes a real Establish — TryReattach itself does
                // not advance the state machine.
                Assert.Equal(FixpState.Suspended, session.State);
            }
            finally { client.Close(); }
        }
        finally
        {
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryReattach_resets_suspend_guard_allowing_a_second_suspend_cycle()
    {
        // Regression for the #496 follow-up: SuspendLocked's one-shot
        // _suspendInProgress guard was never cleared on a successful
        // suspend, and TryReattach did not reset it — so a SECOND
        // disconnect after any reattach silently no-opped the suspend,
        // wedging the session Established over a dead transport. Reattach
        // must begin a fresh suspend cycle.
        var session = await NewSuspendedSessionAsync();
        try
        {
            var (_, serverStream, client) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream));
                // Drive the state machine back to Established as a real
                // Establish replay would, so the second Suspend has an
                // Established → Suspended transition available.
                session.ApplyTransition(FixpEvent.Establish);
                Assert.Equal(FixpState.Established, session.State);

                // Second suspend cycle must actually demote the session.
                session.Suspend("second-cycle");
                Assert.Equal(FixpState.Suspended, session.State);
            }
            finally { client.Close(); }
        }
        finally
        {
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task SuspendForTakeover_does_not_arm_cancel_on_disconnect()
    {
        // Issue #496: an Establish-path takeover demotes a still-Established
        // session whose successor transport has already arrived, so CoD must
        // NOT be armed (the disconnect is immediately superseded and resting
        // orders must survive). A reattach must then succeed.
        var (_, serverStream0, client0) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: serverStream0, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        try
        {
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            // Opt the session into cancel-on-disconnect with a zero window so
            // the normal Suspend path would fire CoD essentially immediately.
            session.SetCancelOnDisconnectForTest(
                B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_OR_TERMINATE,
                codTimeoutWindowMs: 0);

            session.SuspendForTakeover("establish-takeover:test");
            Assert.Equal(FixpState.Suspended, session.State);

            var (_, serverStream1, client1) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream1));
                Assert.True(session.IsAttached);
            }
            finally { client1.Close(); }
        }
        finally
        {
            client0.Close();
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task SuspendForTakeover_blocks_business_until_successor_EstablishAck()
    {
        var (tcp0, serverStream0, client0) = await ConnectPairAsync();
        var registry = new SessionRegistry();
        var session = new FixpSession(
            connectionId: 2, enteringFirm: 7, sessionId: 100,
            stream: serverStream0, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance,
            sessionRegistry: registry);
        registry.Register(session);
        try
        {
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            session.SuspendForTakeover("establish-takeover:admission-test");
            Assert.Equal(FixpState.Suspended, session.State);

            var canceled = new OrderCanceledEvent(
                SecurityId: 1001,
                OrderId: 999,
                Side: Side.Sell,
                PriceMantissa: 101_0000,
                RemainingQuantityAtCancel: 25,
                TransactTimeNanos: 1_700_000_000_000_000_000UL,
                Reason: CancelReason.MassCancel,
                RptSeq: 2);
            var gateway = new GatewayRouter(
                registry, NullLogger<GatewayRouter>.Instance);
            var routed = gateway.WriteExecutionReportPassiveCancel(
                new B3.Exchange.Contracts.SessionId("100"),
                ownerClOrdId: 5555,
                orderId: canceled.OrderId,
                canceled,
                requesterClOrdIdOrZero: 0);

            Assert.True(routed.IsDeferred);
            Assert.Equal(1, registry.PendingWriteCount(session));
            Assert.Equal(0u, session.OutboundSeq);
            Assert.Equal(0, session.RetxBufferDepth);

            var (tcp1, serverStream1, client1) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream1));
                Assert.True(routed.IsDeferred);

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
                var stream = client1.GetStream();
                await stream.WriteAsync(establish.AsMemory(0, length));

                Assert.Equal(EntryPointFrameReader.TidEstablishAck,
                    await ReadTemplateIdAsync(stream));
                Assert.True((await routed.Completion.WaitAsync(
                    TimeSpan.FromSeconds(3))).IsTransportEnqueued);
                Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel,
                    await ReadTemplateIdAsync(stream));
            }
            finally
            {
                client1.Close();
                tcp1.Stop();
            }
        }
        finally
        {
            client0.Close();
            tcp0.Stop();
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrent_Establish_takeovers_keep_exactly_one_generation()
    {
        var (tcp0, server0, client0) = await ConnectPairAsync();
        var registry = new SessionRegistry();
        var session = new FixpSession(
            connectionId: 3, enteringFirm: 7, sessionId: 100,
            stream: server0, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance,
            sessionRegistry: registry);
        registry.Register(session);

        var (tcp1, server1, client1) = await ConnectPairAsync();
        var (tcp2, server2, client2) = await ConnectPairAsync();
        try
        {
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            var expected = session.CaptureAttachmentSnapshot();

            using var start = new ManualResetEventSlim(false);
            var first = Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    return session.TryReattachForEstablish(server1, expected);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            var second = Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    return session.TryReattachForEstablish(server2, expected);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            start.Set();
            bool firstWon = await first.WaitAsync(TimeSpan.FromSeconds(5));
            bool secondWon = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(firstWon, secondWon);

            var winner = firstWon ? client1 : client2;
            var loser = firstWon ? client2 : client1;
            loser.Close();

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
            var stream = winner.GetStream();
            await stream.WriteAsync(establish.AsMemory(0, length));
            Assert.Equal(EntryPointFrameReader.TidEstablishAck,
                await ReadTemplateIdAsync(stream));

            Assert.True(await TestUtil.WaitUntilAsync(
                () => session.State == FixpState.Established && session.IsAttached,
                TimeSpan.FromSeconds(3)));

            var canceled = new OrderCanceledEvent(
                SecurityId: 1001,
                OrderId: 1000,
                Side: Side.Buy,
                PriceMantissa: 100_0000,
                RemainingQuantityAtCancel: 10,
                TransactTimeNanos: 2,
                Reason: CancelReason.MassCancel,
                RptSeq: 3);
            var gateway = new GatewayRouter(
                registry, NullLogger<GatewayRouter>.Instance);
            Assert.True(gateway.WriteExecutionReportPassiveCancel(
                new B3.Exchange.Contracts.SessionId("100"),
                ownerClOrdId: 6000,
                orderId: canceled.OrderId,
                canceled,
                requesterClOrdIdOrZero: 0).IsTransportEnqueued);
            Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel,
                await ReadTemplateIdAsync(stream));
        }
        finally
        {
            client0.Close();
            client1.Close();
            client2.Close();
            server1.Dispose();
            server2.Dispose();
            tcp0.Stop();
            tcp1.Stop();
            tcp2.Stop();
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Deferred_business_queue_is_bounded_and_failed_on_close()
    {
        var registry = new SessionRegistry();
        await using var session = new FixpSession(
            connectionId: 4,
            enteringFirm: 7,
            sessionId: 100,
            stream: new MemoryStream(),
            sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance,
            sendQueueCapacity: 1,
            sessionClaims: new SessionClaimRegistry(),
            sessionRegistry: registry);
        registry.Register(session);
        var gateway = new GatewayRouter(
            registry, NullLogger<GatewayRouter>.Instance);
        var canceled = new OrderCanceledEvent(
            SecurityId: 1001,
            OrderId: 1001,
            Side: Side.Buy,
            PriceMantissa: 100_0000,
            RemainingQuantityAtCancel: 10,
            TransactTimeNanos: 2,
            Reason: CancelReason.MassCancel,
            RptSeq: 4);

        var first = gateway.WriteExecutionReportPassiveCancel(
            new B3.Exchange.Contracts.SessionId("100"),
            ownerClOrdId: 6001,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 0);
        var second = gateway.WriteExecutionReportPassiveCancel(
            new B3.Exchange.Contracts.SessionId("100"),
            ownerClOrdId: 6002,
            orderId: canceled.OrderId + 1,
            canceled with { OrderId = canceled.OrderId + 1 },
            requesterClOrdIdOrZero: 0);

        Assert.True(first.IsDeferred);
        Assert.False(second.IsAccepted);
        Assert.Equal(1, registry.PendingWriteCount(session));

        session.Close("test-close-pending-route");
        Assert.False((await first.Completion.WaitAsync(
            TimeSpan.FromSeconds(3))).IsCommitted);
        Assert.Equal(0, registry.PendingWriteCount(session));
    }

    private static async Task<ushort> ReadTemplateIdAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, timeout.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        var body = new byte[messageLength - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, body, timeout.Token);
        return templateId;
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
                buffer.AsMemory(read), cancellationToken);
            if (count <= 0)
                throw new EndOfStreamException();
            read += count;
        }
    }
}
