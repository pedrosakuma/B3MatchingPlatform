using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using OrderType = B3.Exchange.Matching.OrderType;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #348: <see cref="ChannelDispatcher.OnAsyncSnapshotSaved"/>
/// MUST prefix-truncate the WAL (keep records with
/// <c>Seq &gt; snap.LastAppliedSeq</c>) so a record appended by the
/// dispatch thread between <c>BackgroundSnapshotWriter.Submit</c> and
/// the writer thread's <c>onSaved</c> callback firing is preserved.
/// Pre-fix a full <see cref="IChannelWriteAheadLog.Truncate"/> would
/// drop the tail and a subsequent crash would silently lose it.
/// </summary>
public class WalAsyncSnapshotTruncateRaceTests
{
    private const long Sec = 900_000_000_002L;

    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Sec,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private static long Px(decimal p) => (long)(p * 10_000m);

    private sealed class NoOpPacketSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private sealed class NoOpOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c, DurabilityHandle d = default) => true;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wal-race-" + Guid.NewGuid().ToString("N"));

        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private sealed class DispatcherDriver : IDisposable
    {
        private readonly ChannelDispatcher.TestProbe _probe;
        private readonly System.Collections.Concurrent.BlockingCollection<DrainRequest> _requests = new();
        private readonly Thread _thread;

        public DispatcherDriver(ChannelDispatcher.TestProbe probe)
        {
            _probe = probe;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "wal-race-dispatcher-test-driver",
            };
            _thread.Start();
        }

        public void DrainInbound()
        {
            var request = new DrainRequest();
            _requests.Add(request);
            request.Done.Wait();
            if (request.Error is { } error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        public void Dispose()
        {
            _requests.CompleteAdding();
            _thread.Join();
            _requests.Dispose();
        }

        private void Run()
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    _probe.DrainInbound();
                }
                catch (Exception ex)
                {
                    request.Error = ex;
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }

        private sealed class DrainRequest
        {
            public ManualResetEventSlim Done { get; } = new();
            public Exception? Error { get; set; }
        }
    }

    /// <summary>
    /// Persister whose <c>Save</c> blocks on a counting gate. The test
    /// releases one save at a time so it can observe the post-fix WAL
    /// state after the FIRST onSaved fires, before the writer thread
    /// proceeds to drain the coalesced second snapshot.
    /// </summary>
    private sealed class BlockingPersister : IChannelStatePersister
    {
        private readonly SemaphoreSlim _gate = new(0, int.MaxValue);
        private readonly TaskCompletionSource _firstSaveEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saveEnteredCount;

        public Task FirstSaveEntered => _firstSaveEntered.Task;
        public ChannelStateSnapshot? TryLoad(byte channelNumber) => null;
        public long Save(ChannelStateSnapshot snapshot)
        {
            if (Interlocked.Increment(ref _saveEnteredCount) == 1)
            {
                _firstSaveEntered.TrySetResult();
            }
            _gate.Wait();
            return 0;
        }
        public void ReleaseOne() => _gate.Release();
    }

    private static bool EnqueueOrder(ChannelDispatcher disp, SessionId session,
        string clOrdId, ulong clOrdIdValue, ulong nanos)
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdId, Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, nanos),
            session, enteringFirm: 700, clOrdIdValue: clOrdIdValue);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return condition();
    }

    private static async Task<bool> WaitForTaskAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == task;
    }

    [Fact]
    public async Task AsyncSnapshotSaved_PrefixTruncatesWal_KeepsRecordsBeyondSnapshotSeq()
    {
        using var dir = new TempDir();
        var blocking = new BlockingPersister();
        var metrics = new ChannelMetrics(84);
        var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var disp = new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = new NoOpOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Metrics = metrics,
                Persister = blocking,
                SnapshotThrottle = null,
                UseAsyncSnapshotWriter = true,
                Wal = wal,
            });
        var probe = disp.CreateTestProbe();
        using var driver = new DispatcherDriver(probe);
        var session = new SessionId("80801");

        // Cmd 1 → WAL seq=1 → submits snap@1 to the writer thread.
        // Wait until the writer thread observably entered Save (and is
        // now parked on the gate) BEFORE enqueueing cmd 2. Without this
        // sync the dispatch thread may finish cmd 2 first on slow CI
        // runners, causing both snapshots to coalesce into snap@2 →
        // TruncateThrough(2) → empty WAL → wrong race exercised.
        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x1, 1UL));
        driver.DrainInbound();
        Assert.Single(wal.ReadAll());
        Assert.True(await WaitForTaskAsync(blocking.FirstSaveEntered, TimeSpan.FromSeconds(5)),
            "writer thread never entered Save(snap@1) — async writer may not be running");

        // Cmd 2 → WAL seq=2 appended while the writer is parked inside
        // Save(snap@1). The dispatch thread's snapshot Submit lands in
        // the single-slot mailbox; the writer will drain it after we
        // release the first save.
        Assert.True(EnqueueOrder(disp, session, "CL-2", 0x2, 2UL));
        driver.DrainInbound();
        Assert.Equal(2, wal.ReadAll().Count);

        // Release ONLY the first blocked Save. The writer completes
        // snap@1 → fires onSaved(snap@1) → TruncateThrough(1). The
        // coalesced snap@2 is the next item in the mailbox; the writer
        // will pick it up and block again on the gate (since we don't
        // release a second time), giving us a stable window to assert
        // the WAL state after the first prefix truncate.
        blocking.ReleaseOne();
        // Issue #396: async onSaved now marshals audit-checkpoint prepare
        // through the dispatch inbox before truncating. This test drives the
        // dispatcher via TestProbe, so pump that work item while waiting.
        // Pre-fix (full Truncate): WAL ends up empty → seq=2 is lost.
        // Post-fix (TruncateThrough): seq=2 survives.
        Assert.True(await WaitForAsync(() =>
        {
            driver.DrainInbound();
            return metrics.WalTruncations >= 1;
        },
            TimeSpan.FromSeconds(2)), "async snapshot writer did not truncate WAL before timeout");
        var kept = wal.ReadAll();
        Assert.True(kept.Count == 1 && kept[0].Seq == 2,
            $"expected WAL to contain only seq=2 after prefix truncate, got [{string.Join(",", kept.Select(r => r.Seq))}]");

        // Drain the writer cleanly before disposing.
        blocking.ReleaseOne();
        driver.DrainInbound();
        await disp.DisposeAsync();
        wal.Dispose();
    }
}
