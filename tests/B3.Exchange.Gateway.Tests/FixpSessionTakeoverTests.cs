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
/// Issue #492 — Session takeover end-to-end. Validates that when a live
/// session (old TCP still connected) is targeted by a Negotiate from a
/// new transport carrying a strictly-greater sessionVerId, the exchange
/// accepts the new session via <see cref="SessionClaimRegistry.TryForceTakeOver"/>
/// rather than rejecting with <c>DUPLICATE_SESSION_CONNECTION</c>.
/// </summary>
public class FixpSessionTakeoverTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;
        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private static async Task<(ushort TemplateId, byte[] Frame)> ReadOneFrameAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var hdr = new byte[EntryPointFrameReader.WireHeaderSize];
        int read = 0;
        while (read < hdr.Length)
        {
            int n = await stream.ReadAsync(hdr.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("connection closed");
            read += n;
        }
        ushort msgLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
        ushort tid = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(6, 2));
        int bodyLen = msgLen - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        int btotal = 0;
        while (btotal < bodyLen)
        {
            int n = await stream.ReadAsync(body.AsMemory(btotal), ct);
            if (n == 0) throw new EndOfStreamException("connection closed");
            btotal += n;
        }
        return (tid, body);
    }

    private static EntryPointListener BuildListener(
        FirmRegistry firms, SessionClaimRegistry claims,
        NegotiationValidator negValidator, EstablishValidator estValidator,
        IFixpSessionStatePersister? statePersister = null)
        => new(
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
            statePersister: statePersister);

    private sealed class RecordingStatePersister : IFixpSessionStatePersister
    {
        private readonly object _lock = new();
        private FixpSessionStateSnapshot? _last;

        public FixpSessionStateSnapshot? LastSaved
        {
            get { lock (_lock) return _last; }
        }

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            lock (_lock) _last = snapshot;
        }

        public FixpSessionStateSnapshot? Load(uint sessionId) => null;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => Array.Empty<FixpSessionStateSnapshot>();
        public void Remove(uint sessionId) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Issue #492: trading-host crashes (old TCP still alive from exchange POV),
    /// restarts with an incremented sessionVerId. The exchange must accept the
    /// new Negotiate via TryForceTakeOver and close the stale old session.
    /// Before PR #491 this would hang on DUPLICATE_SESSION_CONNECTION.
    /// </summary>
    [Fact]
    public async Task Negotiate_HigherVerid_WhileOldSessionStillConnected_AcceptsViaTakeOver()
    {
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[] { new SessionCredential(SessionId: "1", FirmId: "F1", AccessKey: "",
                AllowedSourceCidrs: null, Policy: SessionPolicy.Default) });
        var claims = new SessionClaimRegistry();
        var negValidator = new NegotiationValidator(firms, claims, devMode: true,
            timestampSkewToleranceNs: 0);
        var estValidator = new EstablishValidator(timestampSkewToleranceNs: 0);

        await using var listener = BuildListener(firms, claims, negValidator, estValidator);
        listener.Start();

        var creds = Encoding.UTF8.GetBytes("{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buf = new byte[512];

        // --- Phase 1: establish the old session (verId=2) and keep it alive ---
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream1 = client1.GetStream();

        int len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
            sessionId: 1, sessionVerId: 2UL, timestampNanos: 0UL, enteringFirm: 42u,
            onBehalfFirm: null, credentials: creds,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await stream1.WriteAsync(buf.AsMemory(0, len));
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tid1, _) = await ReadOneFrameAsync(stream1, cts1.Token);
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse, tid1);

        len = EntryPointFixpFrameCodec.EncodeEstablish(buf,
            sessionId: 1, sessionVerId: 2UL, timestampNanos: 0UL,
            keepAliveIntervalMillis: 10_000, nextSeqNo: 1,
            cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await stream1.WriteAsync(buf.AsMemory(0, len));
        var (tid2, _) = await ReadOneFrameAsync(stream1, cts1.Token);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck, tid2);

        var oldEstablished = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Any(s => s.SessionId == 1 && s.State == FixpState.Established),
            TimeSpan.FromSeconds(5));
        Assert.True(oldEstablished, "old session (verId=2) must reach Established state");

        // --- Phase 2: reconnect with incremented verId (old TCP still alive) ---
        // This simulates: trading-host crashes, restarts fast (before exchange
        // detects dead TCP), and sends Negotiate with verId=3.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream2 = client2.GetStream();

        len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
            sessionId: 1, sessionVerId: 3UL, timestampNanos: 0UL, enteringFirm: 42u,
            onBehalfFirm: null, credentials: creds,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await stream2.WriteAsync(buf.AsMemory(0, len));
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tid3, _) = await ReadOneFrameAsync(stream2, cts2.Token);

        // This is the core assertion of issue #492: the exchange MUST accept
        // the reconnect via TryForceTakeOver, not reject with
        // DUPLICATE_SESSION_CONNECTION (which would give TidNegotiateReject).
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse, tid3);

        // Old session (verId=2) must be evicted from ActiveSessions after the
        // takeover. The new session (verId=3) has the same SessionId=1 and
        // remains open — so we filter by SessionVerId to tell them apart.
        var oldEvicted = await TestUtil.WaitUntilAsync(
            () => !listener.ActiveSessions.Any(s => s.SessionId == 1 && s.SessionVerId == 2UL),
            TimeSpan.FromSeconds(5));
        Assert.True(oldEvicted, "old session (verId=2) must be removed from ActiveSessions after the takeover");

        // New session (verId=3) claim must be held in the registry.
        Assert.True(claims.TryGetActiveClaim(1u, out _, out var claimedVerId));
        Assert.Equal(3UL, claimedVerId);
    }

    /// <summary>
    /// After a successful takeover, the persisted snapshot must reflect the
    /// new session's verId (3), not the evicted session's verId (2).
    /// Bug: before the fix, CloseLocked called SaveStateSnapshotSafe() for
    /// the evicted session (kind=SessionTakeOver), overwriting the new
    /// session's snapshot with the old verId.
    /// </summary>
    [Fact]
    public async Task TakeOver_WithStatePersister_FinalSnapshotHasNewVerid()
    {
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[] { new SessionCredential(SessionId: "1", FirmId: "F1", AccessKey: "",
                AllowedSourceCidrs: null, Policy: SessionPolicy.Default) });
        var claims = new SessionClaimRegistry();
        var negValidator = new NegotiationValidator(firms, claims, devMode: true,
            timestampSkewToleranceNs: 0);
        var estValidator = new EstablishValidator(timestampSkewToleranceNs: 0);
        var persister = new RecordingStatePersister();

        await using var listener = BuildListener(firms, claims, negValidator, estValidator,
            statePersister: persister);
        listener.Start();

        var creds = Encoding.UTF8.GetBytes("{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buf = new byte[512];

        // Establish old session (verId=2).
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream1 = client1.GetStream();
        int len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
            sessionId: 1, sessionVerId: 2UL, timestampNanos: 0UL, enteringFirm: 42u,
            onBehalfFirm: null, credentials: creds,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await stream1.WriteAsync(buf.AsMemory(0, len));
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tid1, _) = await ReadOneFrameAsync(stream1, cts1.Token);
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse, tid1);

        // Take over with verId=3.
        using var client2 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream2 = client2.GetStream();
        len = EntryPointFixpFrameCodec.EncodeNegotiate(buf,
            sessionId: 1, sessionVerId: 3UL, timestampNanos: 0UL, enteringFirm: 42u,
            onBehalfFirm: null, credentials: creds,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await stream2.WriteAsync(buf.AsMemory(0, len));
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tid2, _) = await ReadOneFrameAsync(stream2, cts2.Token);
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse, tid2);

        // Wait for the old session to be evicted.
        await TestUtil.WaitUntilAsync(
            () => !listener.ActiveSessions.Any(s => s.SessionId == 1 && s.SessionVerId == 2UL),
            TimeSpan.FromSeconds(5));

        // The persisted snapshot MUST carry verId=3 (the new session), not
        // verId=2 (the evicted session). Before the fix, the evicted session's
        // CloseLocked called SaveStateSnapshotSafe() and overwrote the file.
        Assert.NotNull(persister.LastSaved);
        Assert.Equal(3UL, persister.LastSaved!.Value.SessionVerId);
    }
}
