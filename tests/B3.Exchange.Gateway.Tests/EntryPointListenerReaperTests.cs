using B3.Exchange.Contracts;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;
using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #69b-1: <see cref="EntryPointListener"/> must reap FIXP sessions
/// that have lingered in <see cref="FixpState.Suspended"/> beyond
/// <see cref="FixpSessionOptions.SuspendedTimeoutMs"/>. Without this, every
/// transport drop while Established (#69a) leaks the session, its claim,
/// and the engine sink reference until process exit. The reaper exists
/// as a stop-gap; #69b-2 (re-attach) is expected to recover most
/// suspended sessions before the timeout fires.
/// </summary>
public class EntryPointListenerReaperTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public int SessionClosedCalls;
        public bool EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { return true; }
        public bool EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public bool EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) => Interlocked.Increment(ref SessionClosedCalls);
    }

    private sealed class FakeJournal : IFixpOutboundJournal
    {
        private readonly Dictionary<uint, SortedDictionary<uint, byte[]>> _data = new();
        public int RemoveCalls { get; private set; }

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
        {
            if (!_data.TryGetValue(sessionId, out var session))
                _data[sessionId] = session = new();
            if (session.Count > 0 && seq <= session.Keys.Max())
                throw new InvalidOperationException(
                    $"journal append for session 0x{sessionId:x8} seq={seq} is not strictly greater than last persisted seq {session.Keys.Max()}");
            session[seq] = frame.ToArray();
        }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }

        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
        {
            if (!_data.TryGetValue(sessionId, out var session))
                return Array.Empty<OutboundJournalEntry>();
            var list = new List<OutboundJournalEntry>(count);
            for (int i = 0; i < count; i++)
            {
                uint seq = fromSeq + (uint)i;
                if (!session.TryGetValue(seq, out var frame))
                    break;
                list.Add(new OutboundJournalEntry(seq, 0L, frame));
            }
            return list;
        }

        public void PruneUpTo(uint sessionId, uint uptoSeq) { }

        public uint MaxSeq(uint sessionId)
            => _data.TryGetValue(sessionId, out var session) && session.Count > 0
                ? session.Keys.Max()
                : 0u;

        public long EntryCount(uint sessionId)
            => _data.TryGetValue(sessionId, out var session) ? session.Count : 0L;

        public void Remove(uint sessionId)
        {
            RemoveCalls++;
            _data.Remove(sessionId);
        }

        public IReadOnlyCollection<uint> ListSessions() => _data.Keys.ToArray();

        public void Dispose() { }
    }

    private sealed class FakeStatePersister : IFixpSessionStatePersister
    {
        private readonly Dictionary<uint, FixpSessionStateSnapshot> _data = new();
        public int RemoveCalls { get; private set; }

        public void Save(in FixpSessionStateSnapshot snapshot) => _data[snapshot.SessionId] = snapshot;

        public FixpSessionStateSnapshot? Load(uint sessionId)
            => _data.TryGetValue(sessionId, out var snapshot) ? snapshot : null;

        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll() => _data.Values.ToArray();

        public void Remove(uint sessionId)
        {
            RemoveCalls++;
            _data.Remove(sessionId);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Stand up a listener, accept one client, return the resulting
    /// <see cref="FixpSession"/> driven to <see cref="FixpState.Suspended"/>
    /// by closing the client transport while we have already nudged the
    /// state machine to Established (test seam — <c>ApplyTransition</c>
    /// is internal so we don't need to wire the full Negotiate+Establish
    /// handshake just to exercise the reaper).
    /// </summary>
    private static async Task<(EntryPointListener listener, NoOpEngineSink sink, TcpClient client, FixpSession session, List<string> closures)>
        SetupSuspendedSessionAsync(int suspendedTimeoutMs)
    {
        var sink = new NoOpEngineSink();
        var closures = new List<string>();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = suspendedTimeoutMs,
        };
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (_, reason) => { lock (closures) closures.Add(reason); });
        listener.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);

        // Wait for the listener's accept loop to register the session.
        var registered = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1,
            TimeSpan.FromSeconds(2));
        Assert.True(registered, "listener never registered the accepted session");
        var session = listener.ActiveSessions[0];

        // Drive state to Established, then drop client → recv loop EOF →
        // OnTransportClosed → Suspend.
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        Assert.Equal(FixpState.Established, session.State);
        client.Close();
        var suspended = await TestUtil.WaitUntilAsync(
            () => session.State == FixpState.Suspended && session.SuspendedSinceMs is not null,
            TimeSpan.FromSeconds(2));
        Assert.True(suspended, $"session never became Suspended (state={session.State})");
        return (listener, sink, client, session, closures);
    }

    private readonly record struct ReadFrame(ushort TemplateId, byte[] Body);

    private static async Task<TcpClient> ConnectAndEstablishAsync(
        EntryPointListener listener,
        uint sessionId,
        ulong sessionVerId)
    {
        var client = await ConnectAndSendNegotiateAsync(listener, sessionId, sessionVerId);
        var stream = client.GetStream();
        var buffer = new byte[512];

        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadOneFrameAsync(stream)).TemplateId);

        int length = EntryPointFixpFrameCodec.EncodeEstablish(buffer,
            sessionId: sessionId,
            sessionVerId: sessionVerId,
            timestampNanos: 0,
            keepAliveIntervalMillis: 10_000,
            nextSeqNo: 1,
            cancelOnDisconnectType: 0,
            codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(buffer.AsMemory(0, length));
        Assert.Equal(EntryPointFrameReader.TidEstablishAck,
            (await ReadOneFrameAsync(stream)).TemplateId);
        return client;
    }

    private static async Task<TcpClient> ConnectAndSendNegotiateAsync(
        EntryPointListener listener,
        uint sessionId,
        ulong sessionVerId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var credentials = Encoding.UTF8.GetBytes(
            "{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buffer = new byte[512];
        int length = EntryPointFixpFrameCodec.EncodeNegotiate(buffer,
            sessionId: sessionId,
            sessionVerId: sessionVerId,
            timestampNanos: 0,
            enteringFirm: 42,
            onBehalfFirm: null,
            credentials: credentials,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await client.GetStream().WriteAsync(buffer.AsMemory(0, length));
        return client;
    }

    private static async Task<ReadFrame> ReadOneFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        var body = new byte[messageLength - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, body);
    }

    private static async Task ReadExactAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count <= 0) throw new EndOfStreamException();
            read += count;
        }
    }

    [Fact]
    public async Task Reaper_ClosesSession_WhenSuspendedLongerThanTimeout()
    {
        var (listener, sink, client, session, closures) = await SetupSuspendedSessionAsync(suspendedTimeoutMs: 200);
        try
        {
            // Background reaper polls every ~50ms (floor) at 200ms timeout,
            // so it should observe the session past timeout within ~250ms.
            //
            // Synchronize on the *terminal* effect of the close, not an
            // intermediate one: CloseLocked flips IsOpen/SuspendedSinceMs and
            // fires sink.OnSessionClosed early, but only invokes the
            // listener's onSessionClosed reason callback (which populates
            // `closures` and removes the session from ActiveSessions) as its
            // very last step, after per-session persistence cleanup. Under
            // full-suite parallel load that cleanup window is wide enough for
            // a test that waits on the earlier state to observe an empty
            // `closures` and flake (issue #539). Waiting for `closures` to be
            // populated guarantees every prior step has completed.
            var reaped = await TestUtil.WaitUntilAsync(
                () =>
                {
                    lock (closures)
                        return !session.IsOpen
                            && session.SuspendedSinceMs is null
                            && Volatile.Read(ref sink.SessionClosedCalls) == 1
                            && closures.Count == 1;
                },
                TimeSpan.FromSeconds(2));
            Assert.True(reaped, "reaper did not fully close the suspended session within 2s");
            Assert.Equal(1, Volatile.Read(ref sink.SessionClosedCalls));
            lock (closures)
            {
                Assert.Single(closures);
                Assert.Equal("suspended-timeout", closures[0]);
            }
            // Listener must remove the closed session from its tracking list.
            Assert.DoesNotContain(session, listener.ActiveSessions);
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reaper_DoesNotClose_BeforeTimeout()
    {
        // Long timeout so the background loop never fires during the test.
        var (listener, sink, client, session, closures) = await SetupSuspendedSessionAsync(suspendedTimeoutMs: 60_000);
        try
        {
            // Drive one synchronous reaper pass with the actual current
            // tick: should be a no-op because the session has been
            // suspended for a few ms only.
            listener.ReapSuspendedOnce(Environment.TickCount64);
            Assert.True(session.IsOpen || session.State == FixpState.Suspended);
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
            Assert.Empty(closures);
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reaper_DoesNotTouch_NonSuspendedSessions()
    {
        var sink = new NoOpEngineSink();
        var closures = new List<string>();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = 100,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (_, reason) => { lock (closures) closures.Add(reason); });
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        await TestUtil.WaitUntilAsync(() => listener.ActiveSessions.Count == 1, TimeSpan.FromSeconds(2));
        var session = listener.ActiveSessions[0];

        // Session is Idle (never suspended). Sleep past the suspend
        // timeout; the background reaper must NOT touch it.
        await Task.Delay(400);
        Assert.True(session.IsOpen);
        Assert.Equal(FixpState.Idle, session.State);
        Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
        Assert.Empty(closures);
    }

    [Fact]
    public async Task ReaperDisabled_WhenSuspendedTimeoutMsIsZero()
    {
        // SuspendedTimeoutMs=0 must skip starting the reaper Task and skip
        // the synchronous pass entirely. This is the "preserve forever"
        // mode used by tests that want pure suspend semantics.
        var (listener, sink, client, session, closures) = await SetupSuspendedSessionAsync(suspendedTimeoutMs: 0);
        try
        {
            // Even past a generous wait, no reap.
            await Task.Delay(300);
            Assert.False(session.IsOpen); // transport is down (Suspended)
            Assert.Equal(FixpState.Suspended, session.State);
            Assert.NotNull(session.SuspendedSinceMs);
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
            Assert.Empty(closures);
            // Direct call must also be a no-op.
            listener.ReapSuspendedOnce(long.MaxValue);
            Assert.NotNull(session.SuspendedSinceMs);
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reaper_increments_LifecycleMetrics_Reaped_counter()
    {
        var sink = new NoOpEngineSink();
        var metrics = new SessionLifecycleMetrics();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = 200,
            LifecycleMetrics = metrics,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: options);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var registered = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1, TimeSpan.FromSeconds(2));
        Assert.True(registered);
        var session = listener.ActiveSessions[0];
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        Assert.Equal(1, metrics.Established);
        client.Close();
        var suspended = await TestUtil.WaitUntilAsync(
            () => session.State == FixpState.Suspended, TimeSpan.FromSeconds(2));
        Assert.True(suspended);
        Assert.Equal(1, metrics.Suspended);

        var reaped = await TestUtil.WaitUntilAsync(
            () => metrics.Reaped >= 1, TimeSpan.FromSeconds(2));
        Assert.True(reaped, $"reaper did not increment Reaped within 2s (Reaped={metrics.Reaped})");
        // Sanity: Rebound stays zero (we never re-attached).
        Assert.Equal(0, metrics.Rebound);
    }

    [Fact]
    public async Task Reaper_preserves_outbound_journal_but_removes_state_snapshot()
    {
        const uint sessionId = 0x594u;
        var sink = new NoOpEngineSink();
        var closures = new List<string>();
        var journal = new FakeJournal();
        var state = new FakeStatePersister();
        journal.Append(sessionId, 1, 0L, new byte[] { 0x59, 0x40 });
        state.Save(new FixpSessionStateSnapshot(
            SessionId: sessionId,
            SessionVerId: 7UL,
            OutboundMsgSeqNum: 1u,
            LastIncomingSeqNo: 0u,
            EnteringFirm: 11u,
            UpdatedAtNanos: 0L));
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = 200,
        };

        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            new SessionRegistry(),
            NullLoggerFactory.Instance,
            identityFactory: _ => new EntryPointListener.AcceptedConnection(1, EnteringFirm: 11, SessionId: sessionId),
            sessionOptions: options,
            onSessionClosed: (_, reason) => { lock (closures) closures.Add(reason); },
            outboundJournal: journal,
            statePersister: state);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var registered = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1,
            TimeSpan.FromSeconds(2));
        Assert.True(registered, "listener never registered the accepted session");

        var session = listener.ActiveSessions[0];
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        client.Close();

        var reaped = await TestUtil.WaitUntilAsync(
            () =>
            {
                lock (closures)
                    return !session.IsOpen
                        && session.SuspendedSinceMs is null
                        && closures.Count == 1;
            },
            TimeSpan.FromSeconds(2));
        Assert.True(reaped, "reaper did not fully close the suspended session within 2s");

        Assert.DoesNotContain(session, listener.ActiveSessions);
        Assert.Equal(0, journal.RemoveCalls);
        Assert.Equal(1L, journal.EntryCount(sessionId));
        Assert.Single(journal.ReadRange(sessionId, 1, 10));
        Assert.Equal(1, state.RemoveCalls);
        Assert.Null(state.Load(sessionId));

        using var resumedClient = await ConnectAndEstablishAsync(listener, sessionId, sessionVerId: 8);
        var resumedRegistered = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1 && listener.ActiveSessions[0].IsOpen,
            TimeSpan.FromSeconds(2));
        Assert.True(resumedRegistered, "listener never registered the replacement session");

        var resumedSession = listener.ActiveSessions[0];
        var result = resumedSession.WriteOrderMassActionReport(
            clOrdIdValue: 7003,
            massActionResponse: OrderMassActionReportEncoder.MassActionResponseAccepted,
            massActionRejectReason: null,
            side: null,
            securityId: 0,
            transactTimeNanos: 2);
        Assert.True(result.IsCommitted);
        Assert.Equal(2u, resumedSession.OutboundSeq);
        Assert.Equal(new uint[] { 1u, 2u }, journal.ReadRange(sessionId, 1, 10).Select(x => x.Seq).ToArray());
    }
}
