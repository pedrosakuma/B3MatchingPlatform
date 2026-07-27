using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using B3.Exchange.Contracts.Time;
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
/// session through matching Establish or a successfully claimed
/// higher-version Negotiate. Negotiate candidates remain pending until
/// their claim commits.
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

    private sealed class StrictJournal : IFixpOutboundJournal
    {
        private readonly object _lock = new();
        private readonly Dictionary<uint, SortedDictionary<uint, OutboundJournalEntry>> _sessions = new();

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var entries))
                    _sessions[sessionId] = entries = new();
                if (entries.Count > 0 && seq <= entries.Keys.Max())
                    throw new InvalidOperationException("outbound sequence must be strictly monotonic");
                entries.Add(seq, new OutboundJournalEntry(seq, timestampNanos, frame.ToArray()));
            }
        }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }
        public void PruneUpTo(uint sessionId, uint uptoSeq) { }
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
        {
            lock (_lock)
                return _sessions.TryGetValue(sessionId, out var entries)
                    ? entries.Values.Where(entry => entry.Seq >= fromSeq).Take(count).ToArray()
                    : Array.Empty<OutboundJournalEntry>();
        }
        public uint MaxSeq(uint sessionId)
        {
            lock (_lock)
                return _sessions.TryGetValue(sessionId, out var entries) && entries.Count > 0
                    ? entries.Keys.Max()
                    : 0u;
        }
        public long EntryCount(uint sessionId)
        {
            lock (_lock)
                return _sessions.TryGetValue(sessionId, out var entries) ? entries.Count : 0;
        }
        public void Remove(uint sessionId)
        {
            lock (_lock) _sessions.Remove(sessionId);
        }
        public IReadOnlyCollection<uint> ListSessions()
        {
            lock (_lock) return _sessions.Keys.ToArray();
        }
        public void Dispose() { }
    }

    private sealed class FailOnceMaxSeqJournal : IFixpOutboundJournal
    {
        private readonly StrictJournal _inner = new();
        private int _failuresRemaining = 1;

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
            => _inner.Append(sessionId, seq, timestampNanos, frame);
        public void ConfirmPeerAck(uint sessionId, uint uptoSeq)
            => _inner.ConfirmPeerAck(sessionId, uptoSeq);
        public void PruneUpTo(uint sessionId, uint uptoSeq)
            => _inner.PruneUpTo(sessionId, uptoSeq);
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
            => _inner.ReadRange(sessionId, fromSeq, count);
        public uint MaxSeq(uint sessionId)
        {
            if (Interlocked.Exchange(ref _failuresRemaining, 0) == 1)
                throw new IOException("simulated journal reconciliation failure");
            return _inner.MaxSeq(sessionId);
        }
        public long EntryCount(uint sessionId) => _inner.EntryCount(sessionId);
        public void Remove(uint sessionId) => _inner.Remove(sessionId);
        public IReadOnlyCollection<uint> ListSessions() => _inner.ListSessions();
        public void Dispose() => _inner.Dispose();
    }

    private sealed class MemoryStatePersister : IFixpSessionStatePersister
    {
        private readonly object _lock = new();
        private FixpSessionStateSnapshot? _snapshot;

        public int RemoveCalls { get; private set; }

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            lock (_lock) _snapshot = snapshot;
        }

        public FixpSessionStateSnapshot? Load(uint sessionId)
        {
            lock (_lock)
                return _snapshot is { } snapshot && snapshot.SessionId == sessionId
                    ? snapshot
                    : null;
        }

        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
        {
            lock (_lock)
                return _snapshot is { } snapshot
                    ? new[] { snapshot }
                    : Array.Empty<FixpSessionStateSnapshot>();
        }

        public void Remove(uint sessionId)
        {
            lock (_lock)
            {
                if (_snapshot?.SessionId == sessionId)
                {
                    RemoveCalls++;
                    _snapshot = null;
                }
            }
        }

        public void Dispose() { }
    }

    private sealed class BlockingStatePersister : IFixpSessionStatePersister
    {
        private readonly object _lock = new();
        private readonly ManualResetEventSlim _release = new(false);
        private FixpSessionStateSnapshot? _snapshot;

        public TaskCompletionSource<bool> TakeoverSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            if (snapshot.SessionVerId == 101UL)
            {
                TakeoverSaveEntered.TrySetResult(true);
                _release.Wait(TimeSpan.FromSeconds(5));
            }
            lock (_lock) _snapshot = snapshot;
        }

        public void ReleaseTakeoverSave() => _release.Set();

        public FixpSessionStateSnapshot? Load(uint sessionId)
        {
            lock (_lock)
                return _snapshot is { } snapshot && snapshot.SessionId == sessionId
                    ? snapshot
                    : null;
        }

        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
        {
            lock (_lock)
                return _snapshot is { } snapshot
                    ? new[] { snapshot }
                    : Array.Empty<FixpSessionStateSnapshot>();
        }

        public void Remove(uint sessionId)
        {
            lock (_lock)
            {
                if (_snapshot?.SessionId == sessionId)
                    _snapshot = null;
            }
        }

        public void Dispose() => _release.Dispose();
    }

    private sealed class BlockingNanosTimeSource : INanosTimeSource, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ulong NowNanos()
        {
            Entered.TrySetResult(true);
            _release.Wait(TimeSpan.FromSeconds(5));
            return 1;
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private static byte[] BuildFixedBlock(uint sessionId, uint msgSeqNum, ulong clOrdId)
    {
        var fb = new byte[82];
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(0, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(4, 4), msgSeqNum);
        BinaryPrimitives.WriteUInt64LittleEndian(fb.AsSpan(20, 8), clOrdId);
        return fb;
    }

    private static byte[] BuildNegotiate(uint sessionId, ulong sessionVerId)
    {
        var credentials = Encoding.UTF8.GetBytes(
            "{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buffer = new byte[256];
        int length = EntryPointFixpFrameCodec.EncodeNegotiate(
            buffer,
            sessionId,
            sessionVerId,
            timestampNanos: 0,
            enteringFirm: 42,
            onBehalfFirm: null,
            credentials: credentials,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static byte[] BuildEstablish(
        uint sessionId,
        ulong sessionVerId,
        uint nextSeqNo,
        ulong timestampNanos = 0)
    {
        var buffer = new byte[256];
        int length = EntryPointFixpFrameCodec.EncodeEstablish(
            buffer,
            sessionId,
            sessionVerId,
            timestampNanos,
            keepAliveIntervalMillis: 60_000,
            nextSeqNo: nextSeqNo,
            cancelOnDisconnectType: 0,
            codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        return buffer.AsSpan(0, length).ToArray();
    }

    private static byte[] BuildRetransmitRequest(
        uint sessionId,
        ulong timestampNanos,
        uint fromSeqNo,
        uint count)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 20];
        EntryPointFrameReader.WriteHeader(frame,
            messageLength: (ushort)frame.Length,
            blockLength: 20,
            templateId: EntryPointFrameReader.TidRetransmitRequest,
            version: 0);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), timestampNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(12, 4), fromSeqNo);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(16, 4), count);
        return frame;
    }

    private readonly record struct ReadFrame(
        ushort TemplateId,
        uint MsgSeqNum,
        byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
        return new ReadFrame(templateId, msgSeqNum, body);
    }

    private static async Task AssertNoFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var oneByte = new byte[1];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadExactlyAsync(oneByte, cts.Token));
    }

    [Fact]
    public async Task ReconnectingNegotiate_AdoptsPersistedEnvelopeOnlyAfterClaim()
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
                () => listener.ActiveSessions.Any(
                    s => s.SessionId == 1
                        && s.SessionVerId == 101UL
                        && s.State == FixpState.Negotiated),
                TimeSpan.FromSeconds(5));
            Assert.True(registered,
                $"rehydrated session should register (have {listener.ActiveSessions.Count})");

            // The higher-version candidate remained pending until its claim
            // succeeded, then adopted the recoverable sequence envelope.
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

    [Fact]
    public async Task ActiveRehydratedSession_DuplicateNegotiateCleanupCannotEvictRouteOrPersistence()
    {
        var journal = new StrictJournal();
        var statePersister = new MemoryStatePersister();
        var snapshot = new FixpSessionStateSnapshot(
            SessionId: 1,
            SessionVerId: 100UL,
            OutboundMsgSeqNum: 3u,
            LastIncomingSeqNo: 2u,
            EnteringFirm: 42u,
            UpdatedAtNanos: 1_000_000L);
        for (uint seq = 1; seq <= 3; seq++)
            journal.Append(1, seq, seq, new byte[] { (byte)seq });
        statePersister.Save(snapshot);
        var persistedStates = statePersister.LoadAll().ToDictionary(s => s.SessionId, s => s);
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: SessionPolicy.Default),
            });
        var claims = new SessionClaimRegistry();
        claims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);
        var registry = new SessionRegistry();

        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0),
            outboundJournal: journal,
            statePersister: statePersister,
            persistedSessionStates: persistedStates);
        listener.Start();

        using var activeClient = new TcpClient();
        await activeClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        await activeClient.GetStream().WriteAsync(BuildEstablish(
            sessionId: 1, sessionVerId: 100UL, nextSeqNo: 3u));
        Assert.Equal(EntryPointFrameReader.TidEstablishAck,
            (await ReadFrameAsync(activeClient.GetStream())).TemplateId);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Any(
                session => session.SessionId == 1 && session.State == FixpState.Established),
            TimeSpan.FromSeconds(5)));
        var active = listener.ActiveSessions.Single(session => session.SessionId == 1);
        Assert.True(registry.TryGet(new B3.Exchange.Contracts.SessionId("1"), out var currentBefore));
        Assert.Same(active, currentBefore);

        using var duplicateClient = new TcpClient();
        await duplicateClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint.Port);
        await duplicateClient.GetStream().WriteAsync(BuildNegotiate(
            sessionId: 1, sessionVerId: 100UL));
        Assert.Equal(EntryPointFrameReader.TidNegotiateReject,
            (await ReadFrameAsync(duplicateClient.GetStream())).TemplateId);
        Assert.Equal(EntryPointFrameReader.TidTerminate,
            (await ReadFrameAsync(duplicateClient.GetStream())).TemplateId);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1,
            TimeSpan.FromSeconds(5)));

        Assert.True(registry.TryGet(new B3.Exchange.Contracts.SessionId("1"), out var currentAfter));
        Assert.Same(active, currentAfter);
        Assert.Equal(0, statePersister.RemoveCalls);
        Assert.NotNull(statePersister.Load(1));
        Assert.Equal(3u, journal.MaxSeq(1));

        var canceled = new OrderCanceledEvent(
            SecurityId: 123,
            OrderId: 55,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 2,
            Reason: CancelReason.MassCancel,
            RptSeq: 1);
        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        Assert.True(gateway.WriteExecutionReportPassiveCancel(
            new B3.Exchange.Contracts.SessionId("1"),
            ownerClOrdId: 5001,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 7001).IsCommitted);
        var routed = await ReadFrameAsync(activeClient.GetStream());
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, routed.TemplateId);
        Assert.Equal(4u, routed.MsgSeqNum);
        Assert.Equal(4u, journal.MaxSeq(1));
    }

    [Fact]
    public async Task ActiveRehydratedSession_HigherVersionRouteChangesOnlyAfterTakeoverPersistenceCommits()
    {
        var journal = new StrictJournal();
        using var statePersister = new BlockingStatePersister();
        var snapshot = new FixpSessionStateSnapshot(
            SessionId: 1,
            SessionVerId: 100UL,
            OutboundMsgSeqNum: 3u,
            LastIncomingSeqNo: 2u,
            EnteringFirm: 42u,
            UpdatedAtNanos: 1_000_000L);
        for (uint seq = 1; seq <= 3; seq++)
            journal.Append(1, seq, seq, new byte[] { (byte)seq });
        statePersister.Save(snapshot);
        var persistedStates = statePersister.LoadAll().ToDictionary(s => s.SessionId, s => s);
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: SessionPolicy.Default),
            });
        var claims = new SessionClaimRegistry();
        claims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);
        var registry = new SessionRegistry();

        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0),
            outboundJournal: journal,
            statePersister: statePersister,
            persistedSessionStates: persistedStates);
        listener.Start();

        using var activeClient = new TcpClient();
        await activeClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        await activeClient.GetStream().WriteAsync(BuildEstablish(
            sessionId: 1, sessionVerId: 100UL, nextSeqNo: 3u));
        Assert.Equal(EntryPointFrameReader.TidEstablishAck,
            (await ReadFrameAsync(activeClient.GetStream())).TemplateId);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Any(
                session => session.SessionId == 1 && session.State == FixpState.Established),
            TimeSpan.FromSeconds(5)));
        var active = listener.ActiveSessions.Single(session => session.SessionId == 1);

        using var replacementClient = new TcpClient();
        await replacementClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint.Port);
        await replacementClient.GetStream().WriteAsync(BuildNegotiate(
            sessionId: 1, sessionVerId: 101UL));
        await statePersister.TakeoverSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(registry.TryGet(new B3.Exchange.Contracts.SessionId("1"), out var duringCommit));
        Assert.Same(active, duringCommit);
        Assert.Equal(FixpState.Established, active.State);

        statePersister.ReleaseTakeoverSave();
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadFrameAsync(replacementClient.GetStream())).TemplateId);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => registry.TryGet(new B3.Exchange.Contracts.SessionId("1"), out var current)
                && current.SessionVerId == 101UL
                && !ReferenceEquals(current, active),
            TimeSpan.FromSeconds(5)));

        Assert.True(registry.TryGet(new B3.Exchange.Contracts.SessionId("1"), out var replacement));
        Assert.Equal(3u, replacement.OutboundSeq);
        Assert.Equal(3u, journal.MaxSeq(1));
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

    [Fact]
    public async Task ReconnectingNegotiate_BlocksRoutedPassiveReportUntilEstablishAck()
    {
        var journal = new StrictJournal();
        var statePersister = new MemoryStatePersister();
        var snapshot = new FixpSessionStateSnapshot(
            SessionId: 1,
            SessionVerId: 100UL,
            OutboundMsgSeqNum: 3u,
            LastIncomingSeqNo: 2u,
            EnteringFirm: 42u,
            UpdatedAtNanos: 1_000_000L);
        for (uint seq = 1; seq <= 3; seq++)
            journal.Append(1, seq, seq, new byte[] { (byte)seq });
        statePersister.Save(snapshot);
        var persistedStates = statePersister.LoadAll().ToDictionary(s => s.SessionId, s => s);
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: SessionPolicy.Default),
            });
        var claims = new SessionClaimRegistry();
        claims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);
        var registry = new SessionRegistry();

        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0),
            outboundJournal: journal,
            statePersister: statePersister,
            persistedSessionStates: persistedStates);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(BuildNegotiate(sessionId: 1, sessionVerId: 101UL));
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadFrameAsync(stream)).TemplateId);

        var canceled = new OrderCanceledEvent(
            SecurityId: 123,
            OrderId: 55,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 2,
            Reason: CancelReason.MassCancel,
            RptSeq: 1);
        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var routed = gateway.WriteExecutionReportPassiveCancel(
            new B3.Exchange.Contracts.SessionId("1"),
            ownerClOrdId: 5001,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 7001);
        var session = listener.ActiveSessions.Single(s => s.SessionVerId == 101UL);
        Assert.True(routed.IsDeferred);
        Assert.Equal(1, registry.PendingWriteCount(session));
        Assert.Equal(3u, journal.MaxSeq(1));
        await AssertNoFrameAsync(stream);

        await stream.WriteAsync(BuildEstablish(
            sessionId: 1, sessionVerId: 101UL, nextSeqNo: 3u));
        var ack = await ReadFrameAsync(stream);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck, ack.TemplateId);
        Assert.Equal(4u,
            BinaryPrimitives.ReadUInt32LittleEndian(ack.Body.AsSpan(28, 4)));

        Assert.True((await routed.Completion.WaitAsync(
            TimeSpan.FromSeconds(5))).IsTransportEnqueued);
        var report = await ReadFrameAsync(stream);
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, report.TemplateId);
        Assert.Equal(4u, report.MsgSeqNum);
        Assert.Equal(4u, journal.MaxSeq(1));
    }

    [Fact]
    public async Task ReconnectingEstablish_BlocksRoutedPassiveReportUntilEstablishAck()
    {
        var journal = new StrictJournal();
        var statePersister = new MemoryStatePersister();
        using var establishClock = new BlockingNanosTimeSource();
        var snapshot = new FixpSessionStateSnapshot(
            SessionId: 1,
            SessionVerId: 100UL,
            OutboundMsgSeqNum: 3u,
            LastIncomingSeqNo: 2u,
            EnteringFirm: 42u,
            UpdatedAtNanos: 1_000_000L);
        for (uint seq = 1; seq <= 3; seq++)
            journal.Append(1, seq, seq, new byte[] { (byte)seq });
        statePersister.Save(snapshot);
        var persistedStates = statePersister.LoadAll().ToDictionary(s => s.SessionId, s => s);
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: SessionPolicy.Default),
            });
        var claims = new SessionClaimRegistry();
        claims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);
        var registry = new SessionRegistry();

        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(
                establishClock, timestampSkewToleranceNs: ulong.MaxValue),
            outboundJournal: journal,
            statePersister: statePersister,
            persistedSessionStates: persistedStates);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(BuildEstablish(
            sessionId: 1,
            sessionVerId: 100UL,
            nextSeqNo: 3u,
            timestampNanos: 1));
        await establishClock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var canceled = new OrderCanceledEvent(
            SecurityId: 123,
            OrderId: 56,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 2,
            Reason: CancelReason.MassCancel,
            RptSeq: 2);
        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var routed = gateway.WriteExecutionReportPassiveCancel(
            new B3.Exchange.Contracts.SessionId("1"),
            ownerClOrdId: 5002,
            orderId: canceled.OrderId,
            canceled,
            requesterClOrdIdOrZero: 7002);
        var session = listener.ActiveSessions.Single(s => s.SessionVerId == 100UL);
        Assert.True(routed.IsDeferred);
        Assert.Equal(1, registry.PendingWriteCount(session));
        Assert.Equal(3u, journal.MaxSeq(1));
        await AssertNoFrameAsync(stream);

        establishClock.Release();
        var ack = await ReadFrameAsync(stream);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck, ack.TemplateId);
        Assert.Equal(4u,
            BinaryPrimitives.ReadUInt32LittleEndian(ack.Body.AsSpan(28, 4)));

        Assert.True((await routed.Completion.WaitAsync(
            TimeSpan.FromSeconds(5))).IsTransportEnqueued);
        var report = await ReadFrameAsync(stream);
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, report.TemplateId);
        Assert.Equal(4u, report.MsgSeqNum);
        Assert.Equal(4u, journal.MaxSeq(1));
    }

    [Fact]
    public async Task Negotiate_PendingJournalReconcileFails_RestoresWatermarkAndAllowsRetry()
    {
        using var journal = new FailOnceMaxSeqJournal();
        var statePersister = new MemoryStatePersister();
        var snapshot = new FixpSessionStateSnapshot(
            SessionId: 1,
            SessionVerId: 100UL,
            OutboundMsgSeqNum: 3u,
            LastIncomingSeqNo: 2u,
            EnteringFirm: 42u,
            UpdatedAtNanos: 1_000_000L);
        statePersister.Save(snapshot);
        var persistedStates = statePersister.LoadAll().ToDictionary(s => s.SessionId, s => s);
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: SessionPolicy.Default),
            });
        var claims = new SessionClaimRegistry();
        claims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);

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
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0),
            outboundJournal: journal,
            statePersister: statePersister,
            persistedSessionStates: persistedStates);
        listener.Start();

        using (var failedClient = new TcpClient())
        {
            await failedClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            await failedClient.GetStream().WriteAsync(BuildNegotiate(
                sessionId: 1, sessionVerId: 101UL));
            Assert.Equal(EntryPointFrameReader.TidNegotiateReject,
                (await ReadFrameAsync(failedClient.GetStream())).TemplateId);
            Assert.Equal(EntryPointFrameReader.TidTerminate,
                (await ReadFrameAsync(failedClient.GetStream())).TemplateId);
        }

        Assert.False(claims.TryGetActiveClaim(1, out _, out _));
        Assert.Equal(100UL, claims.CurrentSessionVerId(1));

        using var retryClient = new TcpClient();
        await retryClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint.Port);
        await retryClient.GetStream().WriteAsync(BuildNegotiate(
            sessionId: 1, sessionVerId: 101UL));
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadFrameAsync(retryClient.GetStream())).TemplateId);
        Assert.Equal(101UL, claims.CurrentSessionVerId(1));
    }

    [Fact]
    public async Task Negotiate_RouteHandoffFails_RestoresWatermarkAndAllowsRetry()
    {
        var firms = new FirmRegistry(
            new[] { new Firm(Id: "F1", Name: "Firm 1", EnteringFirmCode: 42u) },
            new[]
            {
                new SessionCredential(
                    SessionId: "1",
                    FirmId: "F1",
                    AccessKey: "",
                    AllowedSourceCidrs: null,
                    Policy: SessionPolicy.Default),
            });
        var claims = new SessionClaimRegistry();
        var registry = new SessionRegistry();
        await using var blocker = new FixpSession(
            connectionId: 900,
            enteringFirm: 42,
            sessionId: 1,
            stream: new MemoryStream(),
            sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        registry.Register(blocker);

        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0));
        listener.Start();

        using (var failedClient = new TcpClient())
        {
            await failedClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            await failedClient.GetStream().WriteAsync(BuildNegotiate(
                sessionId: 1, sessionVerId: 100UL));
            Assert.Equal(EntryPointFrameReader.TidNegotiateReject,
                (await ReadFrameAsync(failedClient.GetStream())).TemplateId);
            Assert.Equal(EntryPointFrameReader.TidTerminate,
                (await ReadFrameAsync(failedClient.GetStream())).TemplateId);
        }

        Assert.False(claims.TryGetActiveClaim(1, out _, out _));
        Assert.Equal(0UL, claims.CurrentSessionVerId(1));
        Assert.True(registry.TryGet(
            new B3.Exchange.Contracts.SessionId("1"), out var current));
        Assert.Same(blocker, current);

        registry.Deregister(blocker);
        using var retryClient = new TcpClient();
        await retryClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint.Port);
        await retryClient.GetStream().WriteAsync(BuildNegotiate(
            sessionId: 1, sessionVerId: 100UL));
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadFrameAsync(retryClient.GetStream())).TemplateId);
        Assert.Equal(100UL, claims.CurrentSessionVerId(1));
    }

    private sealed class FaultingStatePersister : IFixpSessionStatePersister
    {
        public int SaveCount;
        public bool FailSaves { get; set; } = true;
        public FixpSessionStateSnapshot? LastSaved { get; private set; }

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            Interlocked.Increment(ref SaveCount);
            if (FailSaves)
                throw new IOException("simulated disk failure");
            LastSaved = snapshot;
        }
        public FixpSessionStateSnapshot? Load(uint sessionId)
            => LastSaved is { } snapshot && snapshot.SessionId == sessionId
                ? snapshot
                : null;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => LastSaved is { } snapshot
                ? new[] { snapshot }
                : Array.Empty<FixpSessionStateSnapshot>();
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
        using var journal = new StrictJournal();
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

        using (var failedClient = new TcpClient())
        {
            await failedClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            await failedClient.GetStream().WriteAsync(BuildNegotiate(
                sessionId: 1, sessionVerId: 100UL));
            Assert.Equal(EntryPointFrameReader.TidNegotiateReject,
                (await ReadFrameAsync(failedClient.GetStream())).TemplateId);
            Assert.Equal(EntryPointFrameReader.TidTerminate,
                (await ReadFrameAsync(failedClient.GetStream())).TemplateId);
        }

        Assert.False(claims.TryGetActiveClaim(1u, out _, out _));
        Assert.Equal(0UL, claims.CurrentSessionVerId(1));

        faulting.FailSaves = false;
        using var retryClient = new TcpClient();
        await retryClient.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint.Port);
        await retryClient.GetStream().WriteAsync(BuildNegotiate(
            sessionId: 1, sessionVerId: 100UL));
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadFrameAsync(retryClient.GetStream())).TemplateId);
        Assert.Equal(100UL, claims.CurrentSessionVerId(1));
        Assert.Equal(100UL, faulting.LastSaved?.SessionVerId);
    }
}
