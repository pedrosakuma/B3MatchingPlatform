using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Matching;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #43 — strict app-message gating: a SimpleNewOrder/Modify/Cancel
/// frame received before <c>Negotiate</c>+<c>Establish</c> have completed
/// must be answered with <c>Terminate(Unnegotiated)</c> /
/// <c>Terminate(NotEstablished)</c>, not silently accepted.
/// </summary>
public class EntryPointStrictGatingTests
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

    private static EntryPointListener BuildListenerWithValidators()
    {
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[] { new SessionCredential(SessionId: "1", FirmId: "F1", AccessKey: "key", AllowedSourceCidrs: null, Policy: SessionPolicy.Default) });
        var claims = new SessionClaimRegistry();
        var negotiationValidator = new NegotiationValidator(firms, claims, devMode: false,
            timestampSkewToleranceNs: 0);
        var establishValidator = new EstablishValidator(timestampSkewToleranceNs: 0);
        return new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            new SessionRegistry(),
            NullLoggerFactory.Instance,
            negotiationValidator: negotiationValidator,
            sessionClaims: claims,
            establishValidator: establishValidator);
    }

    private static byte[] BuildBareNewOrderFrame()
    {
        // Minimal valid SimpleNewOrder header (block content does not need to
        // be valid because gating fires BEFORE decode).
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)frame.Length,
            blockLength: 82,
            templateId: EntryPointFrameReader.TidSimpleNewOrder,
            version: 2);
        return frame;
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
            if (n <= 0) throw new EndOfStreamException("connection closed before Terminate");
            read += n;
        }
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        Assert.Equal(EntryPointFrameReader.TidTerminate, tid);
        return buf[EntryPointFrameReader.WireHeaderSize + 12];
    }

    [Fact]
    public async Task NewOrder_before_negotiate_yields_terminate_unnegotiated()
    {
        var listener = BuildListenerWithValidators();
        listener.Start();
        await using (listener)
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(listener.LocalEndpoint!.Address, listener.LocalEndpoint!.Port);
            await client.GetStream().WriteAsync(BuildBareNewOrderFrame());
            byte code = await ReadTerminationCodeAsync(client);
            Assert.Equal(SessionRejectEncoder.TerminationCode.Unnegotiated, code);
        }
    }
}
