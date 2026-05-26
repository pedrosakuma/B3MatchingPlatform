using B3.Exchange.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// OPT-03 / ADR 0013 — unit tests for <see cref="OptionExpirySweeper"/>.
/// Uses a fake <see cref="OptionExpirySweeper.IExpireSink"/> so the test
/// never touches a live <see cref="ChannelDispatcher"/>.
/// </summary>
public class OptionExpirySweeperTests
{
    private sealed class FakeSink : OptionExpirySweeper.IExpireSink
    {
        public long SecurityId { get; }
        public string Symbol { get; }
        public DateOnly ExpirationDate { get; }
        public byte ChannelNumber { get; }
        public int EnqueueCount { get; private set; }
        public bool ResultOk { get; init; } = true;
        public Exception? ThrowOnEnqueue { get; init; }

        public FakeSink(long secId, DateOnly expiry, string symbol = "OPT", byte channel = 1)
        {
            SecurityId = secId;
            Symbol = symbol;
            ExpirationDate = expiry;
            ChannelNumber = channel;
        }

        public bool EnqueueExpire(TaskCompletionSource<ChannelDispatcher.ExpireSecurityOutcome>? completion)
        {
            EnqueueCount++;
            if (ThrowOnEnqueue != null) throw ThrowOnEnqueue;
            return ResultOk;
        }
    }

    private static readonly DateOnly Today = new(2026, 5, 25);

    private static OptionExpirySweeper Sweeper(params OptionExpirySweeper.IExpireSink[] sinks)
        => new(sinks, NullLogger<OptionExpirySweeper>.Instance, todayUtcOverride: () => Today);

    [Fact]
    public void Sweep_EnqueuesSeriesExpiringToday()
    {
        var s = new FakeSink(secId: 100, expiry: Today);
        var sweeper = Sweeper(s);
        var n = sweeper.SweepExpiredSeries("test");
        Assert.Equal(1, n);
        Assert.Equal(1, s.EnqueueCount);
    }

    [Fact]
    public void Sweep_EnqueuesSeriesAlreadyPastDue()
    {
        var s = new FakeSink(secId: 100, expiry: Today.AddDays(-3));
        var sweeper = Sweeper(s);
        var n = sweeper.SweepExpiredSeries("test");
        Assert.Equal(1, n);
        Assert.Equal(1, s.EnqueueCount);
    }

    [Fact]
    public void Sweep_DoesNotEnqueueFutureSeries()
    {
        var s = new FakeSink(secId: 100, expiry: Today.AddDays(1));
        var sweeper = Sweeper(s);
        var n = sweeper.SweepExpiredSeries("test");
        Assert.Equal(0, n);
        Assert.Equal(0, s.EnqueueCount);
    }

    [Fact]
    public void Sweep_PartitionsBetweenDueAndFuture()
    {
        var due1 = new FakeSink(secId: 101, expiry: Today);
        var due2 = new FakeSink(secId: 102, expiry: Today.AddDays(-1));
        var future = new FakeSink(secId: 103, expiry: Today.AddDays(2));
        var sweeper = Sweeper(due1, due2, future);

        var n = sweeper.SweepExpiredSeries("test");
        Assert.Equal(2, n);
        Assert.Equal(1, due1.EnqueueCount);
        Assert.Equal(1, due2.EnqueueCount);
        Assert.Equal(0, future.EnqueueCount);
    }

    [Fact]
    public void Sweep_ContinuesWhenSinkReturnsFalse()
    {
        var rejected = new FakeSink(secId: 101, expiry: Today) { ResultOk = false };
        var accepted = new FakeSink(secId: 102, expiry: Today) { ResultOk = true };
        var sweeper = Sweeper(rejected, accepted);

        var n = sweeper.SweepExpiredSeries("test");
        // Only the accepted sink counts toward the enqueued total —
        // but both were attempted (no early abort).
        Assert.Equal(1, n);
        Assert.Equal(1, rejected.EnqueueCount);
        Assert.Equal(1, accepted.EnqueueCount);
    }

    [Fact]
    public void Sweep_ContinuesWhenSinkThrows()
    {
        var bad = new FakeSink(secId: 101, expiry: Today)
        {
            ThrowOnEnqueue = new InvalidOperationException("boom"),
        };
        var good = new FakeSink(secId: 102, expiry: Today);
        var sweeper = Sweeper(bad, good);

        var n = sweeper.SweepExpiredSeries("test");
        Assert.Equal(1, n);
        Assert.Equal(1, good.EnqueueCount);
    }

    [Fact]
    public void Sweep_WithNoSinks_IsNoop()
    {
        var sweeper = Sweeper();
        Assert.False(sweeper.HasOptionSeries);
        Assert.Equal(0, sweeper.SweepExpiredSeries("test"));
    }
}
