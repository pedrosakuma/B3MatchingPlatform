using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.TestSupport;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host.Tests;

public sealed class MassCancelPassiveCommitFailureTests
{
    private const long SecurityId = 900_000_000_001L;

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, Entries);

        public void Dispose() { }

        private sealed class RecordingLogger(
            string category,
            ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Error)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class RecordingPacketSink(ConcurrentQueue<string> ordering) : IUmdfPacketSink
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _packets = new();

        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (_gate)
                _packets.Add(packet.ToArray());
            ordering.Enqueue("umdf");
        }

        public void Clear()
        {
            lock (_gate)
                _packets.Clear();
        }

        public byte[][] Snapshot()
        {
            lock (_gate)
                return _packets.Select(static packet => packet.ToArray()).ToArray();
        }
    }

    private sealed class FailFirstCancelAppendJournal(
        ConcurrentQueue<string> ordering) : IFixpOutboundJournal
    {
        private readonly object _gate = new();
        private readonly List<OutboundJournalEntry> _entries = new();
        private int _failedCancel;

        public IReadOnlyList<OutboundJournalEntry> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }

        public void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame)
        {
            ushort templateId = ReadTemplateId(frame);
            if (templateId == EntryPointFrameReader.TidExecutionReportCancel
                && Interlocked.CompareExchange(ref _failedCancel, 1, 0) == 0)
            {
                ordering.Enqueue("cancel-failed");
                throw new IOException("injected passive cancel journal failure");
            }

            lock (_gate)
                _entries.Add(new OutboundJournalEntry(seq, timestampNanos, frame.ToArray()));

            ordering.Enqueue(templateId switch
            {
                EntryPointFrameReader.TidExecutionReportCancel => "cancel-committed",
                EntryPointFrameReader.TidBusinessMessageReject => "system-busy",
                EntryPointFrameReader.TidOrderMassActionReport => "accepted",
                _ => "other",
            });
        }

        public void RollGeneration(uint sessionId)
        {
            lock (_gate)
                _entries.Clear();
        }

        public void RestoreLatestGeneration(uint sessionId) { }

        public void ReleaseActive(uint sessionId) { }

        public void EnforceRetention(uint sessionId, long nowNanos) { }

        public void ConfirmPeerAck(uint sessionId, uint uptoSeq) { }
        public void PruneUpTo(uint sessionId, uint uptoSeq) { }

        public IReadOnlyList<OutboundJournalEntry> ReadRange(
            uint sessionId, uint fromSeq, int count) =>
            Entries.Where(entry => entry.Seq >= fromSeq).Take(count).ToArray();

        public uint MaxSeq(uint sessionId) =>
            Entries.Count == 0 ? 0u : Entries.Max(static entry => entry.Seq);

        public long EntryCount(uint sessionId) => Entries.Count;

        public void Remove(uint sessionId)
        {
            lock (_gate)
                _entries.Clear();
        }

        public IReadOnlyCollection<uint> ListSessions() => [100u];
        public void Dispose() { }
    }

    [Fact]
    public async Task PassiveCancelJournalFailure_PublishesAppliedDeletesThenSystemBusy_AndDispatcherContinues()
    {
        var ordering = new ConcurrentQueue<string>();
        var packetSink = new RecordingPacketSink(ordering);
        var journal = new FailFirstCancelAppendJournal(ordering);
        var retransmitMetrics = new RetransmitMetrics();
        var channelMetrics = new ChannelMetrics(channelNumber: 1);
        var logProvider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logProvider));
        var registry = new SessionRegistry();
        var gateway = new GatewayRouter(
            registry, loggerFactory.CreateLogger<GatewayRouter>());
        MatchingEngine? engine = null;
        var dispatcher = new ChannelDispatcher(
            channelNumber: 1,
            engineFactory: sink => engine = new MatchingEngine(
                [CreateInstrument()], sink, loggerFactory.CreateLogger<MatchingEngine>()),
            options: new ChannelDispatcherOptions
            {
                PacketSink = packetSink,
                Outbound = gateway,
                Logger = loggerFactory.CreateLogger<ChannelDispatcher>(),
                TimeSource = new FakeNanosTimeSource(1_000_000_000UL),
                Metrics = channelMetrics,
            });
        var router = new HostRouter(
            new Dictionary<long, ChannelDispatcher> { [SecurityId] = dispatcher },
            gateway,
            loggerFactory.CreateLogger<HostRouter>(),
            new FakeNanosTimeSource(1_000_000_000UL));
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            router,
            registry,
            loggerFactory,
            identityFactory: _ => new EntryPointListener.AcceptedConnection(
                ConnectionId: 1, EnteringFirm: 7, SessionId: 100),
            retransmitMetrics: retransmitMetrics,
            outboundJournal: journal);

        dispatcher.Start();
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(listener.LocalEndpoint!.Address,
                listener.LocalEndpoint.Port);
            var stream = client.GetStream();

            await stream.WriteAsync(BuildSimpleNewOrder(
                clOrdId: 7001, quantity: 100, priceMantissa: 100_000));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew,
                (await ReadFrameAsync(stream)).TemplateId);
            await stream.WriteAsync(BuildSimpleNewOrder(
                clOrdId: 7002, quantity: 200, priceMantissa: 99_000));
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew,
                (await ReadFrameAsync(stream)).TemplateId);

            Assert.True(dispatcher.TryResolveByClOrdId(
                firm: 7, origClOrdId: 7001, out _, out _));
            Assert.True(dispatcher.TryResolveByClOrdId(
                firm: 7, origClOrdId: 7002, out _, out _));
            packetSink.Clear();
            while (ordering.TryDequeue(out _)) { }

            await stream.WriteAsync(BuildOrderMassActionRequest(
                clOrdId: 7100, msgSeqNum: 3));

            var committedCancel = await ReadFrameAsync(stream);
            Assert.Equal(EntryPointFrameReader.TidExecutionReportCancel,
                committedCancel.TemplateId);
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(
                committedCancel.Body.AsSpan(4, 4)));

            var terminal = await ReadFrameAsync(stream);
            AssertSystemBusy(terminal, expectedRefSeqNum: 3,
                expectedClOrdId: 7100);
            Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(
                terminal.Body.AsSpan(4, 4)));

            Assert.Equal(0, engine!.OrderCount(SecurityId));
            Assert.False(dispatcher.TryResolveByClOrdId(
                firm: 7, origClOrdId: 7001, out _, out _));
            Assert.False(dispatcher.TryResolveByClOrdId(
                firm: 7, origClOrdId: 7002, out _, out _));

            var massCancelPacket = Assert.Single(packetSink.Snapshot());
            Assert.Equal(2, CountTemplate(massCancelPacket, templateId: 51));

            string[] ordered = ordering.ToArray();
            AssertOrdered(ordered,
                "cancel-failed", "cancel-committed", "umdf", "system-busy");
            Assert.DoesNotContain("accepted", ordered);
            Assert.DoesNotContain(journal.Entries,
                static entry => ReadTemplateId(entry.Frame)
                    == EntryPointFrameReader.TidOrderMassActionReport);
            await Task.Delay(100);
            Assert.Equal(0, client.Available);

            Assert.Equal(1, retransmitMetrics.OutboundCommitFailures);
            Assert.Equal(1, channelMetrics.MassCancelReportFailures);
            Assert.Equal(0, channelMetrics.DispatcherCrashes);
            Assert.Contains(logProvider.Entries,
                static entry => entry.Level == LogLevel.Error
                    && entry.Error is IOException
                    && entry.Message.Contains(
                        "failed to commit passive ExecutionReport_Cancel",
                        StringComparison.Ordinal));

            packetSink.Clear();
            await stream.WriteAsync(BuildSimpleNewOrder(
                clOrdId: 7003, quantity: 300, priceMantissa: 98_000));
            var unrelated = await ReadFrameAsync(stream);
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew,
                unrelated.TemplateId);
            Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(
                unrelated.Body.AsSpan(4, 4)));
            Assert.Equal(1, engine.OrderCount(SecurityId));
            Assert.True(dispatcher.TryResolveByClOrdId(
                firm: 7, origClOrdId: 7003, out _, out _));
            Assert.Equal(0, channelMetrics.DispatcherCrashes);
            Assert.True(Assert.Single(listener.ActiveSessions).IsOpen);
        }
        finally
        {
            await listener.DisposeAsync();
            await dispatcher.DisposeAsync();
        }
    }

    private static Instrument CreateInstrument() => new()
    {
        Symbol = "TEST",
        SecurityId = SecurityId,
        TickSize = 0.01m,
        LotSize = 1,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "X",
        SecurityType = "CS",
    };

    private static byte[] BuildSimpleNewOrder(
        ulong clOrdId, long quantity, long priceMantissa)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(
            frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 82,
            templateId: EntryPointFrameReader.TidSimpleNewOrder,
            version: 2);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), SecurityId);
        body[56] = (byte)'1';
        body[57] = (byte)'2';
        body[58] = (byte)'0';
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), quantity);
        BinaryPrimitives.WriteInt64LittleEndian(
            body.Slice(68, 8), priceMantissa);
        return frame;
    }

    private static byte[] BuildOrderMassActionRequest(
        ulong clOrdId, uint msgSeqNum)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 52];
        EntryPointFrameReader.WriteHeader(
            frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 52,
            templateId: EntryPointFrameReader.TidOrderMassActionRequest,
            version: 6);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), msgSeqNum);
        body[18] = 3;
        body[19] = 255;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        body[28] = 255;
        BinaryPrimitives.WriteUInt64LittleEndian(
            body.Slice(38, 8), (ulong)SecurityId);
        body[46] = (byte)'B';
        body[47] = (byte)'V';
        body[48] = (byte)'M';
        body[49] = (byte)'F';
        return frame;
    }

    private static ushort ReadTemplateId(ReadOnlySpan<byte> frame) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            frame.Slice(EntryPointFrameReader.SofhSize + 2, 2));

    private static int CountTemplate(byte[] packet, ushort templateId)
    {
        int count = 0;
        int cursor = WireOffsets.PacketHeaderSize;
        while (cursor + WireOffsets.FramingHeaderSize
               + WireOffsets.SbeMessageHeaderSize <= packet.Length)
        {
            ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(cursor, 2));
            if (messageLength < WireOffsets.FramingHeaderSize
                + WireOffsets.SbeMessageHeaderSize
                || cursor + messageLength > packet.Length)
            {
                break;
            }

            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(cursor + WireOffsets.FramingHeaderSize + 2, 2));
            if (actual == templateId)
                count++;
            cursor += messageLength;
        }
        return count;
    }

    private static void AssertOrdered(
        string[] events, params string[] expected)
    {
        int prior = -1;
        foreach (string value in expected)
        {
            int current = Array.IndexOf(events, value);
            Assert.True(current > prior,
                $"expected '{value}' after index {prior}; events=[{string.Join(", ", events)}]");
            prior = current;
        }
    }

    private static void AssertSystemBusy(
        ReadFrame frame, uint expectedRefSeqNum, ulong expectedClOrdId)
    {
        Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject,
            frame.TemplateId);
        Assert.Equal((byte)29, frame.Body[18]);
        Assert.Equal(expectedRefSeqNum,
            BinaryPrimitives.ReadUInt32LittleEndian(
                frame.Body.AsSpan(20, 4)));
        Assert.Equal(expectedClOrdId,
            BinaryPrimitives.ReadUInt64LittleEndian(
                frame.Body.AsSpan(24, 8)));
        Assert.Equal(8u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                frame.Body.AsSpan(32, 4)));
    }

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, timeout.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(0, 2));
        ushort templateId = ReadTemplateId(header);
        var body = new byte[messageLength - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, body, timeout.Token);
        return new ReadFrame(templateId, body);
    }

    private static async Task ReadExactAsync(
        NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(
                buffer.AsMemory(read), cancellationToken);
            if (count <= 0)
                throw new EndOfStreamException("connection closed");
            read += count;
        }
    }

    private readonly record struct ReadFrame(ushort TemplateId, byte[] Body);
}
