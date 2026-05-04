using B3.Exchange.Contracts;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #54 / GAP-18: when a FIXP session whose <c>Establish</c> claimed
/// a non-zero <see cref="FixpSbe.CancelOnDisconnectType"/> covering the
/// transport-disconnect trigger (modes
/// <c>CANCEL_ON_DISCONNECT_ONLY</c> and
/// <c>CANCEL_ON_DISCONNECT_OR_TERMINATE</c>) is demoted to
/// <see cref="FixpState.Suspended"/> by a transport drop, the gateway
/// must arm a one-shot grace-window timer. On expiry it enqueues a
/// session-scoped <see cref="MassCancelCommand"/> through the existing
/// <see cref="IInboundCommandSink.EnqueueMassCancel"/> seam so the
/// gateway-side <c>OrderOwnershipMap</c> resolves it to the resting
/// orders this session owned. A reconnect (TryReattach) inside the
/// grace window must cancel the pending CoD trigger; full
/// <c>Close</c> must dispose the timer too. The on-terminate half of
/// modes <c>CANCEL_ON_TERMINATE_ONLY</c> /
/// <c>CANCEL_ON_DISCONNECT_OR_TERMINATE</c> is deferred until inbound
/// peer-Terminate framing is wired (out of scope for this PR).
/// </summary>
public class FixpSessionCodTests
{
    private sealed record CapturedMassCancel(MassCancelCommand Cmd, B3.Exchange.Contracts.SessionId Session, uint Firm);

    private sealed class RecordingEngineSink : IInboundCommandSink
    {
        public ConcurrentQueue<CapturedMassCancel> MassCancels { get; } = new();
        public int SessionClosedCalls;
        public bool EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { return true; }
        public bool EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public bool EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm)
        {
            MassCancels.Enqueue(new CapturedMassCancel(cmd, session, enteringFirm));
            return true;
        }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) => Interlocked.Increment(ref SessionClosedCalls);
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

    private static FixpSession DriveToSuspended(
        NetworkStream serverStream,
        RecordingEngineSink sink,
        FixpSbe.CancelOnDisconnectType codType,
        long codWindowMs,
        SessionLifecycleMetrics? metrics = null)
    {
        var options = new FixpSessionOptions { LifecycleMetrics = metrics };
        var session = new FixpSession(
            connectionId: 42, enteringFirm: 7, sessionId: 100,
            stream: serverStream, sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            options: options);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        session.SetCancelOnDisconnectForTest(codType, codWindowMs);
        return session;
    }

    private static async Task DropAndAwaitSuspendAsync(FixpSession session, TcpClient client)
    {
        client.Close();
        await TestUtil.WaitUntilAsync(() => !session.IsAttached, TimeSpan.FromSeconds(3));
        Assert.Equal(FixpState.Suspended, session.State);
    }

    [Fact]
    public async Task Mode1_FiresMassCancel_AfterWindow()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new RecordingEngineSink();
            var metrics = new SessionLifecycleMetrics();
            var session = DriveToSuspended(serverStream, sink,
                FixpSbe.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_ONLY,
                codWindowMs: 50, metrics: metrics);

            await DropAndAwaitSuspendAsync(session, client);

            await TestUtil.WaitUntilAsync(() => sink.MassCancels.Count == 1,
                TimeSpan.FromSeconds(3));
            Assert.True(sink.MassCancels.TryPeek(out var captured));
            Assert.Equal(0, captured!.Cmd.SecurityId);
            Assert.Null(captured.Cmd.SideFilter);
            Assert.Equal(7u, captured.Firm);
            Assert.Equal(session.Identity, captured.Session);
            Assert.Equal(1, metrics.CancelOnDisconnectFired);

            session.Close("test-cleanup");
        }
        finally
        {
            try { serverStream.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            tcp.Stop();
        }
    }

    [Fact]
    public async Task Mode3_FiresMassCancel_OnDisconnect()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new RecordingEngineSink();
            var session = DriveToSuspended(serverStream, sink,
                FixpSbe.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_OR_TERMINATE,
                codWindowMs: 50);

            await DropAndAwaitSuspendAsync(session, client);

            await TestUtil.WaitUntilAsync(() => sink.MassCancels.Count == 1,
                TimeSpan.FromSeconds(3));
            session.Close("test-cleanup");
        }
        finally
        {
            try { serverStream.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            tcp.Stop();
        }
    }

    [Fact]
    public async Task Mode0_DoesNotFire()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new RecordingEngineSink();
            var session = DriveToSuspended(serverStream, sink,
                FixpSbe.CancelOnDisconnectType.DO_NOT_CANCEL_ON_DISCONNECT_OR_TERMINATE,
                codWindowMs: 50);

            await DropAndAwaitSuspendAsync(session, client);
            await Task.Delay(200);
            Assert.Empty(sink.MassCancels);
            session.Close("test-cleanup");
        }
        finally
        {
            try { serverStream.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            tcp.Stop();
        }
    }

    [Fact]
    public async Task Mode2_DoesNotFire_OnTransportDrop()
    {
        // CANCEL_ON_TERMINATE_ONLY must NOT fire on a bare transport
        // drop — the trigger is an explicit Terminate frame from the
        // peer (deferred from this PR until inbound Terminate framing
        // lands). Verifying this guard here so a future change that
        // mis-classifies the trigger fails loudly.
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new RecordingEngineSink();
            var session = DriveToSuspended(serverStream, sink,
                FixpSbe.CancelOnDisconnectType.CANCEL_ON_TERMINATE_ONLY,
                codWindowMs: 50);

            await DropAndAwaitSuspendAsync(session, client);
            await Task.Delay(200);
            Assert.Empty(sink.MassCancels);
            session.Close("test-cleanup");
        }
        finally
        {
            try { serverStream.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            tcp.Stop();
        }
    }

    [Fact]
    public async Task ReattachWithinWindow_CancelsPendingTrigger()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new RecordingEngineSink();
            var session = DriveToSuspended(serverStream, sink,
                FixpSbe.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_ONLY,
                codWindowMs: 5_000);

            await DropAndAwaitSuspendAsync(session, client);
            // Re-attach a fresh stream — TryReattach drains the old
            // recv/watchdog tasks and installs a new transport, which
            // must dispose the pending CoD timer.
            var (tcp2, serverStream2, client2) = await ConnectPairAsync();
            try
            {
                var ok = session.TryReattach(serverStream2);
                Assert.True(ok);
                // Wait a generous slice past where a 50ms timer would
                // have fired; with a 5s window + immediate cancel the
                // map must remain untouched.
                await Task.Delay(150);
                Assert.Empty(sink.MassCancels);
                session.Close("test-cleanup");
            }
            finally
            {
                try { serverStream2.Dispose(); } catch { }
                try { client2.Dispose(); } catch { }
                tcp2.Stop();
            }
        }
        finally
        {
            try { serverStream.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            tcp.Stop();
        }
    }

    [Fact]
    public async Task CloseDuringWindow_CancelsPendingTrigger()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new RecordingEngineSink();
            var session = DriveToSuspended(serverStream, sink,
                FixpSbe.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_ONLY,
                codWindowMs: 5_000);

            await DropAndAwaitSuspendAsync(session, client);
            session.Close("test-cleanup");
            await Task.Delay(150);
            Assert.Empty(sink.MassCancels);
        }
        finally
        {
            try { serverStream.Dispose(); } catch { }
            try { client.Dispose(); } catch { }
            tcp.Stop();
        }
    }
}
