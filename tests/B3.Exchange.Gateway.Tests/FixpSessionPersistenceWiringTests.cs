using System.Net;
using System.Net.Sockets;
using B3.Exchange.Contracts;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #405 (Commit 6): wires the
/// <see cref="IFixpOutboundJournal"/> + <see cref="IFixpSessionStatePersister"/>
/// into <see cref="FixpSession"/>. Tests cover:
/// <list type="bullet">
///   <item>Rehydration: a persisted snapshot seeds SessionId /
///   SessionVerId / EnteringFirm / LastIncomingSeqNo and resumes
///   the outbound seq at <c>max(snapshot, journal.MaxSeq)</c>.</item>
///   <item>CloseKind-gated removal: terminal kinds (PeerTerminate,
///   SuspendedTimeout) erase journal + state; preserving kinds
///   (HostShutdown, TransportError) keep them and save a final
///   snapshot.</item>
///   <item>Establish/Suspend save the snapshot opportunistically.</item>
/// </list>
/// </summary>
public sealed class FixpSessionPersistenceWiringTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId s, uint f, ulong c) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId s, uint f, ulong c, ulong o) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId s, uint f, ulong c, ulong o) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId s, uint f) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId s, uint f) => true;
        public void OnDecodeError(SessionId s, string e) { }
        public void OnSessionClosed(SessionId s) { }
    }

    private sealed class FakeJournal : IFixpOutboundJournal
    {
        private readonly Dictionary<uint, SortedDictionary<uint, byte[]>> _data = new();
        public int RemoveCalls { get; private set; }
        public void Append(uint sessionId, uint seq, long ts, ReadOnlySpan<byte> frame)
        {
            if (!_data.TryGetValue(sessionId, out var s))
                _data[sessionId] = s = new();
            s[seq] = frame.ToArray();
        }
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count)
        {
            if (!_data.TryGetValue(sessionId, out var s)) return Array.Empty<OutboundJournalEntry>();
            var list = new List<OutboundJournalEntry>(count);
            for (int i = 0; i < count; i++)
            {
                uint k = fromSeq + (uint)i;
                if (!s.TryGetValue(k, out var f)) break;
                list.Add(new OutboundJournalEntry(k, 0L, f));
            }
            return list;
        }
        public void PruneUpTo(uint sessionId, uint uptoSeq) { }
        public uint MaxSeq(uint sessionId)
            => _data.TryGetValue(sessionId, out var s) && s.Count > 0 ? s.Keys.Max() : 0u;
        public long EntryCount(uint sessionId)
            => _data.TryGetValue(sessionId, out var s) ? s.Count : 0;
        public void Remove(uint sessionId)
        {
            RemoveCalls++;
            _data.Remove(sessionId);
        }
        public IReadOnlyCollection<uint> ListSessions() => _data.Keys.ToList();
        public void Dispose() { }
    }

    private sealed class FakeStatePersister : IFixpSessionStatePersister
    {
        private readonly Dictionary<uint, FixpSessionStateSnapshot> _data = new();
        public int SaveCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public void Save(in FixpSessionStateSnapshot snapshot)
        {
            SaveCalls++;
            _data[snapshot.SessionId] = snapshot;
        }
        public FixpSessionStateSnapshot? Load(uint sessionId)
            => _data.TryGetValue(sessionId, out var s) ? s : null;
        public IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll() => _data.Values.ToList();
        public void Remove(uint sessionId)
        {
            RemoveCalls++;
            _data.Remove(sessionId);
        }
        public void Dispose() { }
    }

    private static async Task<(TcpListener tcp, NetworkStream serverSide, TcpClient client)> ConnectPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var clientTask = Task.Run(() =>
        {
            var tc = new TcpClient();
            tc.Connect(IPAddress.Loopback, endpoint.Port);
            return tc;
        });
        var server = await listener.AcceptTcpClientAsync();
        var client = await clientTask;
        return (listener, server.GetStream(), client);
    }

    [Fact]
    public async Task PersistedState_seeds_identity_and_seq()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var snapshot = new FixpSessionStateSnapshot(
                SessionId: 4242,
                SessionVerId: 7UL,
                OutboundMsgSeqNum: 100u,
                LastIncomingSeqNo: 55u,
                EnteringFirm: 999,
                UpdatedAtNanos: 0L);

            var session = new FixpSession(
                connectionId: 1, enteringFirm: 0, sessionId: 0,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state,
                persistedState: snapshot);

            Assert.Equal(4242u, session.SessionId);
            Assert.Equal(7UL, session.SessionVerId);
            Assert.Equal(999u, session.EnteringFirm);
            Assert.Equal(55u, session.LastIncomingSeqNo);
            // OutboundSeq tracks the LAST seq assigned; resumed at 100.
            Assert.Equal(100u, session.OutboundSeq);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task PersistedState_reconciles_with_journal_max_seq()
    {
        // Snapshot says 100, but the journal has 150 (snapshot lost
        // some advances before the crash). The resume point must be
        // max(snapshot, journal.MaxSeq) to avoid handing out a seq
        // that already exists in the journal.
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            for (uint i = 1; i <= 150; i++) journal.Append(4242, i, 0L, new byte[] { (byte)i });
            var state = new FakeStatePersister();
            var snapshot = new FixpSessionStateSnapshot(
                SessionId: 4242, SessionVerId: 7UL,
                OutboundMsgSeqNum: 100u,  // stale
                LastIncomingSeqNo: 0u, EnteringFirm: 999, UpdatedAtNanos: 0L);

            var session = new FixpSession(
                connectionId: 1, enteringFirm: 0, sessionId: 0,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state,
                persistedState: snapshot);

            Assert.Equal(150u, session.OutboundSeq);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task PeerTerminate_close_removes_journal_and_state()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var snapshot = new FixpSessionStateSnapshot(
                SessionId: 99, SessionVerId: 1UL,
                OutboundMsgSeqNum: 0, LastIncomingSeqNo: 0,
                EnteringFirm: 1, UpdatedAtNanos: 0L);
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 1, sessionId: 99,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state,
                persistedState: snapshot);

            session.Close("peer terminate", CloseKind.PeerTerminate);

            Assert.Equal(1, journal.RemoveCalls);
            Assert.Equal(1, state.RemoveCalls);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task SuspendedTimeout_close_removes_journal_and_state()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 1, sessionId: 88,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state);

            session.Close("idle reap", CloseKind.SuspendedTimeout);

            Assert.Equal(1, journal.RemoveCalls);
            Assert.Equal(1, state.RemoveCalls);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task DailyReset_close_removes_journal_and_state()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 1, sessionId: 88,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state);

            session.Close("daily reset", CloseKind.DailyReset);

            Assert.Equal(1, journal.RemoveCalls);
            Assert.Equal(1, state.RemoveCalls);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostShutdown_close_preserves_journal_and_saves_final_snapshot()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 1, sessionId: 77,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state);

            int saveCallsBefore = state.SaveCalls;
            session.Close("host shutdown", CloseKind.HostShutdown);

            // Journal + state file MUST survive — peer needs them on reconnect.
            Assert.Equal(0, journal.RemoveCalls);
            Assert.Equal(0, state.RemoveCalls);
            // Final snapshot is saved so LastIncomingSeqNo etc reflect reality.
            Assert.True(state.SaveCalls > saveCallsBefore);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task TransportError_close_preserves_journal_and_saves_final_snapshot()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 1, sessionId: 66,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state);

            int saveCallsBefore = state.SaveCalls;
            session.Close("io error", CloseKind.TransportError);

            Assert.Equal(0, journal.RemoveCalls);
            Assert.Equal(0, state.RemoveCalls);
            Assert.True(state.SaveCalls > saveCallsBefore);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task SessionId_zero_skips_persister_calls()
    {
        // Defensive: a session that never completed Negotiate has
        // SessionId == 0; saving / removing such an identity would
        // overwrite or wipe the wrong session.
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var journal = new FakeJournal();
            var state = new FakeStatePersister();
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 0, sessionId: 0,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state);

            session.Close("test", CloseKind.HostShutdown);

            // No SessionId yet → no save, no remove.
            Assert.Equal(0, state.SaveCalls);
            Assert.Equal(0, state.RemoveCalls);
            Assert.Equal(0, journal.RemoveCalls);

            // And the terminal kind also no-ops removal when sid==0.
            var session2 = new FixpSession(
                connectionId: 2, enteringFirm: 0, sessionId: 0,
                stream: new MemoryStream(),
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                outboundJournal: journal,
                statePersister: state);
            session2.Close("term", CloseKind.PeerTerminate);
            Assert.Equal(0, journal.RemoveCalls);
            Assert.Equal(0, state.RemoveCalls);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task No_persistence_wired_preserves_pre_405_behavior()
    {
        // Sanity: when no journal / persister is wired, the new code
        // paths are no-ops; existing tests should not regress.
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 1, sessionId: 55,
                stream: serverSide,
                sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance);
            session.Close("test", CloseKind.HostShutdown);
            Assert.Equal(CloseKind.HostShutdown, session.LastCloseKind);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }
}
