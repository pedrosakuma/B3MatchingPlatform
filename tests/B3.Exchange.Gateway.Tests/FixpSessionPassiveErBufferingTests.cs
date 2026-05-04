using ContractsSessionId = B3.Exchange.Contracts.SessionId;
using B3.Exchange.Contracts;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Side = B3.Exchange.Matching.Side;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #217 (Onda L · L4): passive ExecutionReports delivered to a
/// FIXP session that is currently <see cref="FixpState.Suspended"/> must
/// be buffered into the session's FIXP retransmit ring rather than
/// silently dropped, so a subsequent <c>Establish</c> + <c>RetransmitRequest</c>
/// from the reattaching peer can replay them as part of normal recovery.
/// </summary>
public class FixpSessionPassiveErBufferingTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) => true;
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

    private static TradeEvent MakeTrade(uint tradeId = 7, long restingOrderId = 12345)
        => new TradeEvent(
            SecurityId: 1001,
            TradeId: tradeId,
            PriceMantissa: 100_0000,
            Quantity: 50,
            AggressorSide: Side.Buy,
            AggressorOrderId: 99,
            AggressorClOrdId: "AGGR-1",
            AggressorFirm: 13,
            RestingOrderId: restingOrderId,
            RestingFirm: 7,
            TransactTimeNanos: 1_700_000_000_000_000_000UL,
            RptSeq: 1);

    [Fact]
    public async Task PassiveTrade_WhileSuspended_AppendsToRetransmitRing()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new NoOpEngineSink();
            var session = new FixpSession(
                connectionId: 7001, enteringFirm: 7, sessionId: 217,
                stream: serverStream, sink: sink,
                logger: NullLogger<FixpSession>.Instance);
            session.Start();

            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            Assert.Equal(FixpState.Established, session.State);
            int depthBefore = session.RetxBufferDepth;

            // Drop the client side → session demotes Established → Suspended.
            client.Close();
            await TestUtil.WaitUntilAsync(() => session.State == FixpState.Suspended, TimeSpan.FromSeconds(3));
            Assert.False(session.IsOpen, "transport should be down while Suspended");
            Assert.True(session.IsRegistered, "session must remain registered while Suspended");

            // L4 contract: a passive ER routed to the suspended session must
            // be encoded + appended to the retransmit ring (the underlying
            // TryEnqueueFrame on the dead transport will silently fail).
            var trade = MakeTrade(tradeId: 7, restingOrderId: 12345);
            bool result = session.WriteExecutionReportTrade(
                trade, isAggressor: false, ownerOrderId: 12345,
                clOrdIdValue: 5555, leavesQty: 0, cumQty: 50);

            // The transport rejection bubbles up as `false`, but the frame
            // is in the ring — verify by depth growth.
            Assert.False(result, "TryEnqueueFrame must fail when transport is down");
            Assert.Equal(depthBefore + 1, session.RetxBufferDepth);

            // A second passive event also accrues.
            var trade2 = MakeTrade(tradeId: 8, restingOrderId: 12345);
            session.WriteExecutionReportTrade(trade2, isAggressor: false, ownerOrderId: 12345,
                clOrdIdValue: 5555, leavesQty: 0, cumQty: 100);
            Assert.Equal(depthBefore + 2, session.RetxBufferDepth);

            session.Close("test-cleanup");
        }
        finally
        {
            client.Dispose();
            serverStream.Dispose();
            tcp.Stop();
        }
    }

    [Fact]
    public async Task PassiveCancel_WhileSuspended_AppendsToRetransmitRing()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new NoOpEngineSink();
            var session = new FixpSession(
                connectionId: 7002, enteringFirm: 7, sessionId: 218,
                stream: serverStream, sink: sink,
                logger: NullLogger<FixpSession>.Instance);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            client.Close();
            await TestUtil.WaitUntilAsync(() => session.State == FixpState.Suspended, TimeSpan.FromSeconds(3));
            int depthBefore = session.RetxBufferDepth;

            var canceled = new OrderCanceledEvent(
                SecurityId: 1001,
                OrderId: 999,
                Side: Side.Sell,
                PriceMantissa: 101_0000,
                RemainingQuantityAtCancel: 25,
                TransactTimeNanos: 1_700_000_000_000_000_000UL,
                Reason: CancelReason.MassCancel,
                RptSeq: 2);
            session.WriteExecutionReportCancel(canceled, clOrdIdValue: 5555, origClOrdIdValue: 0);
            Assert.Equal(depthBefore + 1, session.RetxBufferDepth);

            session.Close("test-cleanup");
        }
        finally
        {
            client.Dispose();
            serverStream.Dispose();
            tcp.Stop();
        }
    }

    [Fact]
    public async Task PassiveTrade_AfterTerminalClose_IsDropped()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new NoOpEngineSink();
            var session = new FixpSession(
                connectionId: 7003, enteringFirm: 7, sessionId: 219,
                stream: serverStream, sink: sink,
                logger: NullLogger<FixpSession>.Instance);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            int depthBefore = session.RetxBufferDepth;

            // Force terminal close (not Suspend).
            session.Close("test-terminal");
            Assert.False(session.IsRegistered);

            var trade = MakeTrade();
            bool result = session.WriteExecutionReportTrade(
                trade, isAggressor: false, ownerOrderId: 12345,
                clOrdIdValue: 5555, leavesQty: 0, cumQty: 50);
            Assert.False(result);
            // No buffering once terminal — IsRegistered is false.
            Assert.Equal(depthBefore, session.RetxBufferDepth);
        }
        finally
        {
            client.Dispose();
            serverStream.Dispose();
            tcp.Stop();
        }
    }
}
