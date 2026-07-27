using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

public class FixpSessionMassActionTakeoverTests
{
    private sealed class ControlledSink : IInboundCommandSink
    {
        public TaskCompletionSource<Action<MassCancelOutcome>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;

        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm,
            Action<MassCancelOutcome> onCompleted)
        {
            Completion.TrySetResult(onCompleted);
            return true;
        }

        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private sealed class StrictJournal : IFixpOutboundJournal
    {
        private readonly object _lock = new();
        private readonly SortedDictionary<uint, OutboundJournalEntry> _entries = new();
        private readonly ManualResetEventSlim _releaseMaxSeq = new(false);

        public IReadOnlyList<OutboundJournalEntry> Entries
        {
            get { lock (_lock) return _entries.Values.ToArray(); }
        }

        public bool BlockMaxSeq { get; set; }
        public TaskCompletionSource<bool> MaxSeqEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
        {
            lock (_lock)
            {
                if (_entries.Count > 0 && seq <= _entries.Keys.Max())
                    throw new InvalidOperationException("outbound sequence must be strictly monotonic");
                _entries.Add(seq, new OutboundJournalEntry(seq, timestampNanos, frame.ToArray()));
            }
        }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }
        public void PruneUpTo(uint sessionId, uint uptoSeq) { }
        public uint MaxSeq(uint sessionId)
        {
            if (BlockMaxSeq)
            {
                MaxSeqEntered.TrySetResult(true);
                _releaseMaxSeq.Wait(TimeSpan.FromSeconds(5));
            }
            lock (_lock) return _entries.Count == 0 ? 0u : _entries.Keys.Max();
        }
        public long EntryCount(uint sessionId)
        {
            lock (_lock) return _entries.Count;
        }
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
        {
            lock (_lock)
            {
                return _entries.Values
                    .Where(entry => entry.Seq >= fromSeq)
                    .Take(count)
                    .ToArray();
            }
        }
        public void Remove(uint sessionId)
        {
            lock (_lock) _entries.Clear();
        }
        public void ReleaseMaxSeq() => _releaseMaxSeq.Set();
        public IReadOnlyCollection<uint> ListSessions() => new[] { 1u };
        public void Dispose()
        {
            _releaseMaxSeq.Set();
            _releaseMaxSeq.Dispose();
        }
    }

    private sealed class BlockingStatePersister : IFixpSessionStatePersister
    {
        private readonly object _lock = new();
        private readonly ManualResetEventSlim _releaseTakeOverSave = new(false);
        private FixpSessionStateSnapshot? _lastSaved;

        public bool BlockTakeOverSave { get; set; }
        public bool PersistBeforeBlocking { get; set; }
        public TaskCompletionSource<bool> TakeOverSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FixpSessionStateSnapshot? LastSaved
        {
            get { lock (_lock) return _lastSaved; }
        }

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            if (BlockTakeOverSave && snapshot.SessionVerId == 3)
            {
                if (PersistBeforeBlocking)
                {
                    lock (_lock) _lastSaved = snapshot;
                }
                TakeOverSaveEntered.TrySetResult(true);
                _releaseTakeOverSave.Wait(TimeSpan.FromSeconds(5));
                if (PersistBeforeBlocking)
                    return;
            }
            lock (_lock) _lastSaved = snapshot;
        }

        public void ReleaseTakeOverSave() => _releaseTakeOverSave.Set();
        public FixpSessionStateSnapshot? Load(uint sessionId) => LastSaved;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => LastSaved is { } saved ? new[] { saved } : Array.Empty<FixpSessionStateSnapshot>();
        public void Remove(uint sessionId)
        {
            lock (_lock) _lastSaved = null;
        }
        public void Dispose() => _releaseTakeOverSave.Dispose();
    }

    private sealed class FailingTakeOverStatePersister : IFixpSessionStatePersister
    {
        private readonly object _lock = new();
        private FixpSessionStateSnapshot? _lastSaved;
        private bool _replacementSaveFailed;

        public bool PersistReplacementBeforeFailure { get; init; }
        public bool FailRollbackSave { get; init; }

        public FixpSessionStateSnapshot? LastSaved
        {
            get { lock (_lock) return _lastSaved; }
        }

        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            lock (_lock)
            {
                if (snapshot.SessionVerId == 3 && !_replacementSaveFailed)
                {
                    _replacementSaveFailed = true;
                    if (PersistReplacementBeforeFailure)
                        _lastSaved = snapshot;
                    throw new IOException("injected replacement snapshot failure");
                }
                if (_replacementSaveFailed
                    && snapshot.SessionVerId == 2
                    && FailRollbackSave)
                {
                    throw new IOException("injected rollback snapshot failure");
                }
                _lastSaved = snapshot;
            }
        }

        public FixpSessionStateSnapshot? Load(uint sessionId)
            => LastSaved is { } saved && saved.SessionId == sessionId ? saved : null;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll()
            => LastSaved is { } saved ? new[] { saved } : Array.Empty<FixpSessionStateSnapshot>();
        public void Remove(uint sessionId)
        {
            lock (_lock)
            {
                if (_lastSaved?.SessionId == sessionId)
                    _lastSaved = null;
            }
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task TakeoverDuringMassCancel_BlocksRoutedOutputUntilEstablishAck()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        await using var listener = BuildListener(sink, registry, claims);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        await oldClient.GetStream().WriteAsync(BuildMassActionRequest(clOrdId: 7001));
        var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var replacementClient = await ConnectAndSendNegotiateAsync(listener, sessionVerId: 3);
        var replacementStream = replacementClient.GetStream();
        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadOneFrameAsync(replacementStream)).TemplateId);

        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var canceled = new OrderCanceledEvent(
            SecurityId: 123,
            OrderId: 55,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 1,
            Reason: CancelReason.MassCancel,
            RptSeq: 1);

        var routed = Task.Run(() =>
        {
            var result = gateway.WriteExecutionReportPassiveCancel(
                new SessionId("1"),
                ownerClOrdId: 5001,
                orderId: canceled.OrderId,
                canceled,
                requesterClOrdIdOrZero: 7001);
            complete(MassCancelOutcome.Completed(1));
            return result;
        });
        var replacement = listener.ActiveSessions.Single(
            session => session.SessionVerId == 3);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => replacement.BusinessAdmissionWaiterCount == 1,
            TimeSpan.FromSeconds(5)));
        Assert.False(routed.IsCompleted);
        Assert.Equal(0u, replacement.OutboundSeq);
        await AssertNoFrameAsync(replacementStream);

        var establish = new byte[256];
        int establishLength = EntryPointFixpFrameCodec.EncodeEstablish(
            establish,
            sessionId: 1,
            sessionVerId: 3,
            timestampNanos: 0,
            keepAliveIntervalMillis: 10_000,
            nextSeqNo: 1,
            cancelOnDisconnectType: 0,
            codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await replacementStream.WriteAsync(establish.AsMemory(0, establishLength));

        var ack = await ReadOneFrameAsync(replacementStream);
        Assert.Equal(EntryPointFrameReader.TidEstablishAck, ack.TemplateId);
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(ack.Body.AsSpan(28, 4)));

        Assert.True((await routed.WaitAsync(TimeSpan.FromSeconds(5))).IsTransportEnqueued);
        var cancel = await ReadOneFrameAsync(replacementStream);
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel.TemplateId);
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(cancel.Body.AsSpan(4, 4)));
        var report = await ReadOneFrameAsync(replacementStream);
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal(2u,
            BinaryPrimitives.ReadUInt32LittleEndian(report.Body.AsSpan(4, 4)));
        Assert.Equal(7001UL,
            BinaryPrimitives.ReadUInt64LittleEndian(report.Body.AsSpan(20, 8)));
        Assert.Equal(2u, replacement.OutboundSeq);
    }

    [Fact]
    public async Task CompletionFromClosedLogicalSession_DoesNotRouteToLaterSessionReuse()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        await using var listener = BuildListener(sink, registry, claims);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        await oldClient.GetStream().WriteAsync(BuildMassActionRequest(clOrdId: 7002));
        var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        listener.ActiveSessions.Single(s => s.SessionId == 1).Close("test-logical-session-ended");
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.All(s => s.SessionId != 1),
            TimeSpan.FromSeconds(5)));

        using var unrelatedClient = await ConnectAndEstablishAsync(listener, sessionVerId: 3);
        complete(MassCancelOutcome.Completed(0));

        await AssertNoFrameAsync(unrelatedClient.GetStream());
    }

    [Fact]
    public async Task PersistenceEnabledTakeover_ContinuesSequenceAcrossCancelAndTerminalReport()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var journal = new StrictJournal();
        var state = new BlockingStatePersister();
        await using var listener = BuildListener(
            sink, registry, claims, journal, state);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        await oldClient.GetStream().WriteAsync(BuildMassActionRequest(clOrdId: 7101));
        var complete = await sink.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var gateway = new GatewayRouter(registry, NullLogger<GatewayRouter>.Instance);
        var canceled = CreateMassCancelEvent(orderId: 56, rptSeq: 1);
        var cancelResult = gateway.WriteExecutionReportPassiveCancel(
            new SessionId("1"), ownerClOrdId: 5002, orderId: canceled.OrderId,
            canceled, requesterClOrdIdOrZero: 7101);
        Assert.True(cancelResult.IsCommitted);
        var cancel = await ReadOneFrameAsync(oldClient.GetStream());
        Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel, cancel.TemplateId);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(cancel.Body.AsSpan(4, 4)));

        using var replacementClient = await ConnectAndEstablishAsync(listener, sessionVerId: 3);
        complete(MassCancelOutcome.Completed(1));

        var report = await ReadOneFrameAsync(replacementClient.GetStream());
        Assert.Equal(EntryPointFrameReader.TidOrderMassActionReport, report.TemplateId);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(report.Body.AsSpan(4, 4)));

        var entries = journal.Entries;
        Assert.Equal(new uint[] { 1, 2 }, entries.Select(entry => entry.Seq));
        Assert.Equal(
            new[]
            {
                EntryPointFrameReader.TidExecutionReportCancel,
                EntryPointFrameReader.TidOrderMassActionReport,
            },
            entries.Select(entry => BinaryPrimitives.ReadUInt16LittleEndian(
                entry.Frame.AsSpan(EntryPointFrameReader.SofhSize + 2, 2))));
        Assert.Equal(2u, listener.ActiveSessions.Single(
            session => session.SessionVerId == 3).OutboundSeq);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementCloseAfterSeal_CommitsDurableVersionAndNeverRestoresVictim(
        bool persistBeforeBlocking)
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var state = new BlockingStatePersister
        {
            BlockTakeOverSave = true,
            PersistBeforeBlocking = persistBeforeBlocking,
        };
        await using var listener = BuildListener(
            sink, registry, claims, outboundJournal: null, statePersister: state);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        var oldSession = listener.ActiveSessions.Single(
            session => session.SessionVerId == 2);

        using var replacementClient = await ConnectAndSendNegotiateAsync(
            listener, sessionVerId: 3);
        await state.TakeOverSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(persistBeforeBlocking ? 3UL : 2UL, state.LastSaved?.SessionVerId);
        var replacement = listener.ActiveSessions.Single(
            session => session.SessionVerId == 3);
        var closeTask = Task.Run(() =>
            replacement.Close("test-close-during-takeover", CloseKind.TransportError));
        Assert.True(await TestUtil.WaitUntilAsync(
            () => !replacement.IsLiveTakeOverCandidate,
            TimeSpan.FromSeconds(5)));

        state.ReleaseTakeOverSave();
        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await TestUtil.WaitUntilAsync(
            () => !listener.ActiveSessions.Contains(oldSession)
                && !listener.ActiveSessions.Contains(replacement),
            TimeSpan.FromSeconds(5)));
        Assert.False(registry.TryGet(new SessionId("1"), out _));
        Assert.False(claims.TryGetActiveClaim(1, out _, out _));
        Assert.Equal(3UL, claims.CurrentSessionVerId(1));
        Assert.Equal(3UL, state.LastSaved?.SessionVerId);
        Assert.Equal(CloseKind.SessionTakeOver, oldSession.LastCloseKind);
        Assert.Equal(CloseKind.TransportError, replacement.LastCloseKind);
        AssertRestartRejectsVersionAndAcceptsNext(state.LastSaved!.Value);
    }

    [Fact]
    public async Task ReplacementCloseBeforeSeal_RestoresVictimWithoutPersistingRejectedVersion()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        using var journal = new StrictJournal();
        var state = new BlockingStatePersister();
        await using var listener = BuildListener(
            sink, registry, claims, journal, state);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        var oldSession = listener.ActiveSessions.Single(
            session => session.SessionVerId == 2);
        journal.BlockMaxSeq = true;

        using var replacementClient = await ConnectAndSendNegotiateAsync(
            listener, sessionVerId: 3);
        await journal.MaxSeqEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = listener.ActiveSessions.Single(
            session => session.SessionVerId == 3);
        var closeTask = Task.Run(() =>
            replacement.Close("test-close-before-takeover-seal", CloseKind.TransportError));
        Assert.True(await TestUtil.WaitUntilAsync(
            () => !replacement.IsLiveTakeOverCandidate,
            TimeSpan.FromSeconds(5)));

        journal.ReleaseMaxSeq();
        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Contains(oldSession)
                && !listener.ActiveSessions.Contains(replacement),
            TimeSpan.FromSeconds(5)));
        Assert.True(registry.TryGet(new SessionId("1"), out var current));
        Assert.Same(oldSession, current);
        Assert.True(claims.TryGetActiveClaim(1, out var holder, out var version));
        Assert.Same(oldSession, holder);
        Assert.Equal(2UL, version);
        Assert.Equal(2UL, state.LastSaved?.SessionVerId);
        AssertRestartAcceptsVersion(state.LastSaved!.Value, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TakeoverSaveFailure_RestoresVictimOnlyAfterDurableRollback(
        bool persistReplacementBeforeFailure)
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var state = new FailingTakeOverStatePersister
        {
            PersistReplacementBeforeFailure = persistReplacementBeforeFailure,
        };
        await using var listener = BuildListener(
            sink, registry, claims, outboundJournal: null, statePersister: state);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        var oldSession = listener.ActiveSessions.Single(
            session => session.SessionVerId == 2);
        using var replacementClient = await ConnectAndSendNegotiateAsync(
            listener, sessionVerId: 3);

        Assert.Equal(EntryPointFrameReader.TidNegotiateReject,
            (await ReadOneFrameAsync(replacementClient.GetStream())).TemplateId);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Contains(oldSession)
                && listener.ActiveSessions.All(session => session.SessionVerId != 3),
            TimeSpan.FromSeconds(5)));
        Assert.True(registry.TryGet(new SessionId("1"), out var current));
        Assert.Same(oldSession, current);
        Assert.True(claims.TryGetActiveClaim(1, out var holder, out var version));
        Assert.Same(oldSession, holder);
        Assert.Equal(2UL, version);
        Assert.Equal(2UL, state.LastSaved?.SessionVerId);
        AssertRestartAcceptsVersion(state.LastSaved!.Value, 3);
    }

    [Fact]
    public async Task TakeoverRollbackSaveFailure_FailsClosedWithReplacementVersionDurable()
    {
        var sink = new ControlledSink();
        var registry = new SessionRegistry();
        var claims = new SessionClaimRegistry();
        var state = new FailingTakeOverStatePersister
        {
            PersistReplacementBeforeFailure = true,
            FailRollbackSave = true,
        };
        await using var listener = BuildListener(
            sink, registry, claims, outboundJournal: null, statePersister: state);
        listener.Start();

        using var oldClient = await ConnectAndEstablishAsync(listener, sessionVerId: 2);
        var oldSession = listener.ActiveSessions.Single(
            session => session.SessionVerId == 2);
        using var replacementClient = await ConnectAndSendNegotiateAsync(
            listener, sessionVerId: 3);

        Assert.Equal(EntryPointFrameReader.TidNegotiateReject,
            (await ReadOneFrameAsync(replacementClient.GetStream())).TemplateId);
        Assert.True(await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.All(session => session.SessionVerId != 2),
            TimeSpan.FromSeconds(5)));
        Assert.False(registry.TryGet(new SessionId("1"), out _));
        Assert.False(claims.TryGetActiveClaim(1, out _, out _));
        Assert.Equal(3UL, claims.CurrentSessionVerId(1));
        Assert.Equal(3UL, state.LastSaved?.SessionVerId);
        Assert.Equal(CloseKind.SessionTakeOver, oldSession.LastCloseKind);
        AssertRestartRejectsVersionAndAcceptsNext(state.LastSaved!.Value);
    }

    [Fact]
    public async Task TerminalRouteInvocation_LinearizesWithLogicalTransfer()
    {
        var registry = new SessionRegistry();
        var sink = new ControlledSink();
        await using var oldSession = new FixpSession(
            connectionId: 10, enteringFirm: 42, sessionId: 1,
            stream: new MemoryStream(), sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            sessionRegistry: registry);
        await using var replacement = new FixpSession(
            connectionId: 11, enteringFirm: 42, sessionId: 1,
            stream: new MemoryStream(), sink: sink,
            logger: NullLogger<FixpSession>.Instance,
            sessionRegistry: registry);
        registry.Register(oldSession);
        var route = registry.CaptureRoute(oldSession);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);

        var invocation = Task.Run(() => route.TryInvoke(current =>
        {
            Assert.Same(oldSession, current);
            entered.TrySetResult(true);
            release.Wait(TimeSpan.FromSeconds(5));
            return OrderedStreamWriteResult.Committed;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var transfer = Task.Run(() =>
            registry.ExecuteExclusive(oldSession, () => route.SetCurrent(replacement)));
        await Task.Delay(50);
        Assert.False(transfer.IsCompleted);

        release.Set();
        Assert.True((await invocation).IsCommitted);
        await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(replacement, route.Current);
    }

    private static EntryPointListener BuildListener(
        IInboundCommandSink sink,
        SessionRegistry registry,
        SessionClaimRegistry claims,
        IFixpOutboundJournal? outboundJournal = null,
        IFixpSessionStatePersister? statePersister = null)
    {
        var firms = new FirmRegistry(
            new[] { new Firm("F1", "Firm 1", 42) },
            new[]
            {
                new SessionCredential("1", "F1", "",
                    AllowedSourceCidrs: null, Policy: SessionPolicy.Default),
            });
        return new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            registry,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
                FirstFrameTimeoutMs = 5_000,
                SendingTimeSkewToleranceNs = 0,
            },
            negotiationValidator: new NegotiationValidator(
                firms, claims, devMode: true, timestampSkewToleranceNs: 0),
            sessionClaims: claims,
            establishValidator: new EstablishValidator(timestampSkewToleranceNs: 0),
            outboundJournal: outboundJournal,
            statePersister: statePersister);
    }

    private static OrderCanceledEvent CreateMassCancelEvent(long orderId, uint rptSeq) =>
        new(
            SecurityId: 123,
            OrderId: orderId,
            Side: Side.Buy,
            PriceMantissa: 100_000,
            RemainingQuantityAtCancel: 100,
            TransactTimeNanos: 1,
            Reason: CancelReason.MassCancel,
            RptSeq: rptSeq);

    private static async Task<TcpClient> ConnectAndEstablishAsync(
        EntryPointListener listener,
        ulong sessionVerId)
    {
        var client = await ConnectAndSendNegotiateAsync(listener, sessionVerId);
        var stream = client.GetStream();
        var buffer = new byte[512];

        Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
            (await ReadOneFrameAsync(stream)).TemplateId);

        int length = EntryPointFixpFrameCodec.EncodeEstablish(buffer,
            sessionId: 1,
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
        ulong sessionVerId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var credentials = Encoding.UTF8.GetBytes(
            "{\"auth_type\":\"basic\",\"username\":\"1\",\"access_key\":\"\"}");
        var buffer = new byte[512];
        int length = EntryPointFixpFrameCodec.EncodeNegotiate(buffer,
            sessionId: 1,
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

    private static void AssertRestartAcceptsVersion(
        FixpSessionStateSnapshot snapshot,
        ulong sessionVerId)
    {
        var restartedClaims = new SessionClaimRegistry();
        restartedClaims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            restartedClaims.TryClaim(snapshot.SessionId, sessionVerId, new object()));
    }

    private static void AssertRestartRejectsVersionAndAcceptsNext(
        FixpSessionStateSnapshot snapshot)
    {
        var restartedClaims = new SessionClaimRegistry();
        restartedClaims.SeedLastVersion(snapshot.SessionId, snapshot.SessionVerId);
        Assert.Equal(SessionClaimRegistry.ClaimResult.StaleVersion,
            restartedClaims.TryClaim(snapshot.SessionId, snapshot.SessionVerId, new object()));
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted,
            restartedClaims.TryClaim(snapshot.SessionId, snapshot.SessionVerId + 1, new object()));
    }

    private static byte[] BuildMassActionRequest(ulong clOrdId)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 52];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 52,
            templateId: EntryPointFrameReader.TidOrderMassActionRequest,
            version: 6);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), 1);
        body[18] = 3;
        body[19] = 255;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        body[28] = 255;
        body[46] = (byte)'B';
        body[47] = (byte)'V';
        body[48] = (byte)'M';
        body[49] = (byte)'F';
        return frame;
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

    private readonly record struct ReadFrame(ushort TemplateId, byte[] Body);

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

    private static async Task AssertNoFrameAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var oneByte = new byte[1];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadExactlyAsync(oneByte, cts.Token));
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
}
