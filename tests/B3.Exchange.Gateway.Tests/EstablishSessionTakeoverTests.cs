using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #496 — Establish-path session takeover. After an OMS restart the
/// matcher stays up, so the only reattach path that can fire is the hot
/// reattach in <see cref="EntryPointListener"/>. When the prior TCP died
/// without a timely FIN (hard kill / partition), the stale
/// <see cref="FixpSession"/> is still <see cref="FixpState.Established"/>
/// when the resuming <c>Establish</c> (same SessionId + same SessionVerId,
/// per Binary EntryPoint spec §5.3) arrives. Before #496 that landed on a
/// fresh <see cref="FixpState.Idle"/> session and was rejected
/// <c>UNNEGOTIATED</c>, forcing the client to <c>Negotiate</c> and bump the
/// SessionVerID (orphaning resting orders). The fix force-suspends the
/// still-Established stale session and re-attaches the new transport
/// deterministically, mirroring the Negotiate-path takeover from #488.
/// </summary>
public class EstablishSessionTakeoverTests
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

    private static EntryPointListener NewListener(out int port)
    {
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[] { new SessionCredential(SessionId: "1", FirmId: "F1", AccessKey: "", AllowedSourceCidrs: null, Policy: SessionPolicy.Default) });
        var claims = new SessionClaimRegistry();
        var negValidator = new NegotiationValidator(firms, claims, devMode: true, timestampSkewToleranceNs: 0);
        var estValidator = new EstablishValidator(timestampSkewToleranceNs: 0);

        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            new SessionRegistry(),
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
            },
            negotiationValidator: negValidator,
            sessionClaims: claims,
            establishValidator: estValidator,
            persistedSessionStates: null);
        listener.Start();
        port = listener.LocalEndpoint!.Port;
        return listener;
    }

    private static byte[] BuildNegotiate(uint sessionId, ulong sessionVerId)
    {
        var creds = Encoding.UTF8.GetBytes("{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buf = new byte[256];
        int len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
            sessionId: sessionId, sessionVerId: sessionVerId,
            timestampNanos: 0UL, enteringFirm: 42u, onBehalfFirm: null,
            credentials: creds,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        return buf.AsSpan(0, len).ToArray();
    }

    private static byte[] BuildEstablish(uint sessionId, ulong sessionVerId)
    {
        var buf = new byte[256];
        int len = EntryPointFixpFrameCodec.EncodeEstablish(buf,
            sessionId: sessionId, sessionVerId: sessionVerId,
            timestampNanos: 0UL, keepAliveIntervalMillis: 60_000UL,
            nextSeqNo: 1u,
            cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0UL,
            credentials: ReadOnlySpan<byte>.Empty);
        return buf.AsSpan(0, len).ToArray();
    }

    private static async Task<ushort> ReadTemplateIdAsync(NetworkStream s, CancellationToken ct)
    {
        var hdr = new byte[EntryPointFrameReader.WireHeaderSize];
        int total = 0;
        while (total < hdr.Length)
        {
            int n = await s.ReadAsync(hdr.AsMemory(total), ct);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
        ushort messageLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(6, 2));
        int bodyLen = messageLen - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        int btotal = 0;
        while (btotal < bodyLen)
        {
            int n = await s.ReadAsync(body.AsMemory(btotal), ct);
            if (n == 0) throw new EndOfStreamException();
            btotal += n;
        }
        return tid;
    }

    [Fact]
    public async Task ResumingEstablish_OnStillEstablishedStaleSession_TakesOver_AckNotUnnegotiated()
    {
        await using var listener = NewListener(out int port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Phase 1: first connection performs the full Negotiate + Establish
        // handshake and reaches Established. Its TCP is intentionally left
        // OPEN below to model a hard-killed peer whose dead transport the
        // server has not yet detected (the stale session is still
        // Established when the resume arrives).
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var s1 = client1.GetStream();
        await s1.WriteAsync(BuildNegotiate(sessionId: 1, sessionVerId: 100UL), cts.Token);
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse, await ReadTemplateIdAsync(s1, cts.Token));
        await s1.WriteAsync(BuildEstablish(sessionId: 1, sessionVerId: 100UL), cts.Token);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck, await ReadTemplateIdAsync(s1, cts.Token));

        var established = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Any(x => x.SessionId == 1 && x.State == FixpState.Established),
            TimeSpan.FromSeconds(5));
        Assert.True(established);
        var original = listener.ActiveSessions.Single(x => x.SessionId == 1);
        var originalConnectionId = original.ConnectionId;

        // Phase 2: a brand-new connection sends a resuming Establish reusing
        // the SAME SessionId + SessionVerId while the stale session is still
        // Established. Per #496 this must be taken over (force-suspend +
        // reattach), and the new transport must receive an EstablishAck —
        // NOT an EstablishReject(UNNEGOTIATED).
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var s2 = client2.GetStream();
        await s2.WriteAsync(BuildEstablish(sessionId: 1, sessionVerId: 100UL), cts.Token);

        var tid = await ReadTemplateIdAsync(s2, cts.Token);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck, tid);

        // The takeover reuses the SAME session instance (no fresh Idle
        // session was spun up) and the session ends back in Established
        // bound to the new transport, with its identity preserved.
        var afterTakeover = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count(x => x.SessionId == 1) == 1
                && listener.ActiveSessions.Single(x => x.SessionId == 1).State == FixpState.Established,
            TimeSpan.FromSeconds(5));
        Assert.True(afterTakeover);
        var resumed = listener.ActiveSessions.Single(x => x.SessionId == 1);
        Assert.Equal(originalConnectionId, resumed.ConnectionId);
        Assert.Equal(100UL, resumed.SessionVerId);
    }
}
