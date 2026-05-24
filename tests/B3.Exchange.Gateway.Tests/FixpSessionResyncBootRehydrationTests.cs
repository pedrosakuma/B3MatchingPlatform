using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;
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

    private static byte[] BuildFixedBlock(uint sessionId, uint msgSeqNum, ulong clOrdId)
    {
        var fb = new byte[82];
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(0, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(4, 4), msgSeqNum);
        BinaryPrimitives.WriteUInt64LittleEndian(fb.AsSpan(20, 8), clOrdId);
        return fb;
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

    [Fact]
    public async Task ReconnectingEstablish_WithSamePersistedSessionVerId_ResumesNegotiatedAndAcceptsEstablish()
    {
        // Issue #405 / review finding: after a host crash the peer
        // reconnects with Establish (NOT Negotiate) using its original
        // SessionVerId, per spec §1.5 EstablishmentAck.serverFlow=
        // RECOVERABLE. The rehydrated FixpSession must come up in
        // FixpState.Negotiated so the Establish lands as
        // (Negotiated, Establish) → Established. Without the
        // resumeAsNegotiated path, the session would start Idle and
        // EstablishValidator would reject with UNNEGOTIATED.
        var dir = Path.Combine(Path.GetTempPath(), "fixp-resync-est-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
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

            // Act: peer reconnects with Establish reusing the persisted
            // SessionVerId (RECOVERABLE-flow resume).
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            var buf = new byte[256];
            int len = EntryPointFixpFrameCodec.EncodeEstablish(buf,
                sessionId: 1, sessionVerId: 100UL,
                timestampNanos: 0UL, keepAliveIntervalMillis: 60_000UL,
                nextSeqNo: 3u,
                cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0UL,
                credentials: ReadOnlySpan<byte>.Empty);
            await client.GetStream().WriteAsync(buf.AsMemory(0, len));

            // Assert: the listener constructs a rehydrated session that
            // accepts the Establish (state transitions to Established).
            var establishedSession = await TestUtil.WaitUntilAsync(() =>
            {
                var s = listener.ActiveSessions.FirstOrDefault(x => x.SessionId == 1);
                return s is not null && s.State == FixpState.Established;
            }, TimeSpan.FromSeconds(5));
            Assert.True(establishedSession,
                "rehydrated session should reach Established after Establish-resume; " +
                $"current states: {string.Join(",", listener.ActiveSessions.Select(s => $"{s.SessionId}:{s.State}"))}");

            var session = listener.ActiveSessions.Single(s => s.SessionId == 1);
            // The persisted identity must survive: same SessionVerId,
            // EnteringFirm, and counters from the snapshot.
            Assert.Equal(100UL, session.SessionVerId);
            Assert.Equal(42u, session.EnteringFirm);
            Assert.Equal(3u, session.OutboundSeq);
            Assert.Equal(2u, session.LastIncomingSeqNo);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(50, 51, 50, 50, 1)]
    [InlineData(0, 250, 250, 0, 0)]
    public async Task ReconnectingEstablish_WithSamePersistedSessionVerId_AppliesCredentialRateLimit(
        int configuredMaxOrderRatePerSecond,
        int attemptedOrders,
        int expectedAccepted,
        int expectedMetricAccepted,
        int expectedRejected)
    {
        var persistedStates = new Dictionary<uint, FixpSessionStateSnapshot>
        {
            [1] = new(
                SessionId: 1,
                SessionVerId: 100UL,
                OutboundMsgSeqNum: 3u,
                LastIncomingSeqNo: 2u,
                EnteringFirm: 42u,
                UpdatedAtNanos: 1_000_000L),
        };
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: new SessionPolicy(MaxOrderRatePerSecond: configuredMaxOrderRatePerSecond)),
            });
        var claims = new SessionClaimRegistry();
        foreach (var s in persistedStates.Values)
            claims.SeedLastVersion(s.SessionId, s.SessionVerId);

        var metrics = new ThrottleMetrics();
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
                MaxOrderRatePerSecond = 200,
                ThrottleMetrics = metrics,
            },
            negotiationValidator: negValidator,
            sessionClaims: claims,
            establishValidator: estValidator,
            persistedSessionStates: persistedStates,
            persistedMaxOrderRateResolver: sessionId =>
                firms.FindSessionByWire(sessionId)?.Policy.MaxOrderRatePerSecond);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var buf = new byte[256];
        int len = EntryPointFixpFrameCodec.EncodeEstablish(buf,
            sessionId: 1, sessionVerId: 100UL,
            timestampNanos: 0UL, keepAliveIntervalMillis: 60_000UL,
            nextSeqNo: 3u,
            cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0UL,
            credentials: ReadOnlySpan<byte>.Empty);
        await client.GetStream().WriteAsync(buf.AsMemory(0, len));

        var establishedSession = await TestUtil.WaitUntilAsync(() =>
        {
            var s = listener.ActiveSessions.FirstOrDefault(x => x.SessionId == 1);
            return s is not null && s.State == FixpState.Established;
        }, TimeSpan.FromSeconds(5));
        Assert.True(establishedSession);

        var session = listener.ActiveSessions.Single(s => s.SessionId == 1);
        for (int i = 0; i < attemptedOrders; i++)
        {
            var accepted = session.TryAcceptInboundThrottle(
                EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(1, (uint)(i + 1), (ulong)(10_000 + i)));
            Assert.Equal(i < expectedAccepted, accepted);
        }

        Assert.Equal(expectedMetricAccepted, metrics.Accepted);
        Assert.Equal(expectedRejected, metrics.Rejected);
    }

    private sealed class FaultingStatePersister : IFixpSessionStatePersister
    {
        public int SaveCount;
        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            Interlocked.Increment(ref SaveCount);
            throw new IOException("simulated disk failure");
        }
        public FixpSessionStateSnapshot? Load(uint sessionId) => null;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => Array.Empty<FixpSessionStateSnapshot>();
        public void Remove(uint sessionId) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task Negotiate_StatePersisterFails_RejectsAndReleasesClaim()
    {
        // Issue #405 (review finding #2): the FIXP Negotiate accept
        // path must persist the new SessionVerID BEFORE acking. If the
        // persister throws (disk full / IO error), the peer must
        // receive a NegotiateReject — never a NegotiateResponse for a
        // SessionVerID that never reached durable storage. The
        // in-memory claim must also be released so the peer can
        // immediately retry on a fresh socket without colliding with
        // DUPLICATE_SESSION_CONNECTION.
        var faulting = new FaultingStatePersister();
        using var journal = new FileFixpOutboundJournal(
            Path.Combine(Path.GetTempPath(), "fixp-fault-" + Guid.NewGuid().ToString("n")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileFixpOutboundJournal>.Instance);
        try
        {
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
                statePersister: faulting,
                persistedSessionStates: null);
            listener.Start();

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            var creds = Encoding.UTF8.GetBytes("{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
            var buf = new byte[256];
            int len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
                sessionId: 1, sessionVerId: 100UL,
                timestampNanos: 0UL, enteringFirm: 42u, onBehalfFirm: null,
                credentials: creds,
                clientIp: ReadOnlySpan<byte>.Empty,
                clientAppName: ReadOnlySpan<byte>.Empty,
                clientAppVersion: ReadOnlySpan<byte>.Empty);
            await client.GetStream().WriteAsync(buf.AsMemory(0, len));

            // Wait for the listener to: (a) call the faulting persister,
            // (b) reject the session, (c) deregister it from
            // ActiveSessions (which the reject path drives via Close →
            // onClosed callback).
            var rejected = await TestUtil.WaitUntilAsync(
                () => Volatile.Read(ref faulting.SaveCount) >= 1
                    && listener.ActiveSessions.All(s => s.SessionId != 1),
                TimeSpan.FromSeconds(5));
            Assert.True(rejected,
                $"expected reject + deregister; SaveCount={faulting.SaveCount}, " +
                $"active=[{string.Join(",", listener.ActiveSessions.Select(s => $"{s.SessionId}:{s.State}"))}]");

            // The claim registry must be empty so the peer can retry
            // with the same SessionVerID without hitting
            // DUPLICATE_SESSION_CONNECTION.
            Assert.False(claims.TryGetActiveClaim(1u, out _, out _));
        }
        finally
        {
            try
            {
                if (journal is IDisposable d) d.Dispose();
            }
            catch { }
        }
    }
}
