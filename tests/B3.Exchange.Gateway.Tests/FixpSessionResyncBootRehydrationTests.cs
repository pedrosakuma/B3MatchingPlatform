using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #405 — boot rehydration end-to-end. Validates that an
/// <see cref="EntryPointListener"/> wired with a persisted outbound
/// journal + session-state snapshots resumes a previously persisted
/// session when a peer reconnects with a Negotiate targeting the
/// known SessionId. The newly-constructed <see cref="FixpSession"/>
/// must inherit the persisted <see cref="FixpSession.OutboundSeq"/>
/// and <see cref="FixpSession.LastIncomingSeqNo"/> from the snapshot,
/// proving the spec §1.5 RECOVERABLE serverFlow contract survives a
/// host restart and the peer can replay the missed range against the
/// journal cold-read path.
/// </summary>
public class FixpSessionResyncBootRehydrationTests
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

    [Fact]
    public async Task ListenerWiredWithPersistedState_RehydratesNewSessionOnReconnectingNegotiate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fixp-resync-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            // Arrange: simulate a prior host incarnation that persisted
            // 3 outbound ER frames + a session-state snapshot for
            // SessionId=1 (verId=100, outboundSeq=3, lastIncoming=2).
            using var journal = new FileFixpOutboundJournal(dir,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileFixpOutboundJournal>.Instance);
            using var statePersister = new FileFixpSessionStatePersister(dir,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileFixpSessionStatePersister>.Instance);
            for (uint seq = 1; seq <= 3; seq++)
            {
                journal.Append(sessionId: 1, seq: seq, timestampNanos: 1_000L * seq,
                    frame: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, (byte)seq });
            }
            statePersister.Save(new FixpSessionStateSnapshot(
                SessionId: 1,
                SessionVerId: 100UL,
                OutboundMsgSeqNum: 3u,
                LastIncomingSeqNo: 2u,
                EnteringFirm: 42u,
                UpdatedAtNanos: 1_000_000L));

            // Simulate the boot path that ExchangeHost runs: LoadAll the
            // state snapshots and seed the claim registry so the peer
            // can re-Negotiate with a higher SessionVerId than the
            // persisted one.
            var persistedStates = statePersister.LoadAll().ToDictionary(s => s.SessionId, s => s);
            var firms = new FirmRegistry(
                new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
                new[] { new SessionCredential(SessionId: "1", FirmId: "F1", AccessKey: "", AllowedSourceCidrs: null, Policy: SessionPolicy.Default) });
            var claims = new SessionClaimRegistry();
            foreach (var s in persistedStates.Values)
                claims.SeedLastVersion(s.SessionId, s.SessionVerId);
            var negValidator = new NegotiationValidator(firms, claims, devMode: true, timestampSkewToleranceNs: 0);
            var estValidator = new EstablishValidator(timestampSkewToleranceNs: 0);

            await using var listener = new EntryPointListener(
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
                outboundJournal: journal,
                statePersister: statePersister,
                persistedSessionStates: persistedStates);
            listener.Start();

            // Act: peer reconnects with a Negotiate referencing the
            // persisted SessionId and a strictly-greater SessionVerId
            // (per spec §1.4 microseconds-since-epoch monotonicity).
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            var creds = Encoding.UTF8.GetBytes("{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
            var buf = new byte[256];
            int len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
                sessionId: 1, sessionVerId: 101UL,
                timestampNanos: 0UL, enteringFirm: 42u, onBehalfFirm: null,
                credentials: creds,
                clientIp: ReadOnlySpan<byte>.Empty,
                clientAppName: ReadOnlySpan<byte>.Empty,
                clientAppVersion: ReadOnlySpan<byte>.Empty);
            await client.GetStream().WriteAsync(buf.AsMemory(0, len));

            // Wait until the listener finishes its first-frame router
            // and constructs the rehydrated session.
            var registered = await TestUtil.WaitUntilAsync(
                () => listener.ActiveSessions.Any(s => s.SessionId == 1),
                TimeSpan.FromSeconds(5));
            Assert.True(registered,
                $"rehydrated session should register (have {listener.ActiveSessions.Count})");

            // Assert: the new FixpSession was constructed WITH the
            // persisted snapshot, so its outbound seq counter resumes
            // at 3 (next emitted business frame will use seq=4) and
            // its LastIncomingSeqNo carries the persisted 2. Both
            // values would be 0 in a fresh (non-rehydrated) session.
            var session = listener.ActiveSessions.Single(s => s.SessionId == 1);
            Assert.Equal(3u, session.OutboundSeq);
            Assert.Equal(2u, session.LastIncomingSeqNo);
            Assert.Equal(42u, session.EnteringFirm);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ListenerWithoutPersistedState_DoesNotRehydrate_FreshSessionStartsAtSeqZero()
    {
        // Negative control: same wire flow, but no persistedStates
        // dictionary → the listener must construct a fresh session
        // (OutboundSeq == 0) rather than picking up stale on-disk
        // bytes. Guards against rehydration leaking across boots that
        // intentionally start clean (config opt-out, fresh dataDir).
        var dir = Path.Combine(Path.GetTempPath(), "fixp-resync-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            using var journal = new FileFixpOutboundJournal(dir,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileFixpOutboundJournal>.Instance);
            using var statePersister = new FileFixpSessionStatePersister(dir,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileFixpSessionStatePersister>.Instance);

            var firms = new FirmRegistry(
                new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
                new[] { new SessionCredential(SessionId: "1", FirmId: "F1", AccessKey: "", AllowedSourceCidrs: null, Policy: SessionPolicy.Default) });
            var claims = new SessionClaimRegistry();
            var negValidator = new NegotiationValidator(firms, claims, devMode: true, timestampSkewToleranceNs: 0);
            var estValidator = new EstablishValidator(timestampSkewToleranceNs: 0);

            await using var listener = new EntryPointListener(
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
                outboundJournal: journal,
                statePersister: statePersister,
                persistedSessionStates: null);
            listener.Start();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            var creds = Encoding.UTF8.GetBytes("{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
            var buf = new byte[256];
            int len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
                sessionId: 1, sessionVerId: 1UL,
                timestampNanos: 0UL, enteringFirm: 42u, onBehalfFirm: null,
                credentials: creds,
                clientIp: ReadOnlySpan<byte>.Empty,
                clientAppName: ReadOnlySpan<byte>.Empty,
                clientAppVersion: ReadOnlySpan<byte>.Empty);
            await client.GetStream().WriteAsync(buf.AsMemory(0, len));

            var registered = await TestUtil.WaitUntilAsync(
                () => listener.ActiveSessions.Any(s => s.SessionId == 1),
                TimeSpan.FromSeconds(5));
            Assert.True(registered);
            var session = listener.ActiveSessions.Single(s => s.SessionId == 1);
            Assert.Equal(0u, session.OutboundSeq);
            Assert.Equal(0u, session.LastIncomingSeqNo);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
