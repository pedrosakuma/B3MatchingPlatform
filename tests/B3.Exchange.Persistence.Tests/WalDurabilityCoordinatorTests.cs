using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

public sealed class WalDurabilityCoordinatorTests
{
    [Fact]
    public void WaitForDurable_zero_returns_immediately()
    {
        using var coord = new WalDurabilityCoordinator(
            1, groupCommit: false, TimeSpan.Zero, () => 0, NullLogger.Instance);

        coord.WaitForDurable(0); // must not block
    }

    [Fact]
    public void PerWrite_mode_records_pending_and_durable()
    {
        using var coord = new WalDurabilityCoordinator(
            1, groupCommit: false, TimeSpan.Zero, () => 0, NullLogger.Instance);

        coord.RecordPendingAndDurable(5);

        Assert.Equal(5, coord.PendingSeq);
        Assert.Equal(5, coord.DurableSeq);

        // No-op (already at 5).
        coord.WaitForDurable(5);
        // Early-out: not groupCommit, so never blocks even on higher seq.
        coord.WaitForDurable(6);
    }

    [Fact]
    public void GroupCommit_advances_durable_via_callback()
    {
        long pendingFromCallback = 0;
        using var callbackFired = new ManualResetEventSlim(false);
        long Callback()
        {
            callbackFired.Set();
            return Interlocked.Read(ref pendingFromCallback);
        }

        using var coord = new WalDurabilityCoordinator(
            1, groupCommit: true, TimeSpan.FromMilliseconds(5),
            Callback, NullLogger.Instance);

        Interlocked.Exchange(ref pendingFromCallback, 7);
        coord.RecordPending(7);

        Assert.True(callbackFired.Wait(TimeSpan.FromSeconds(2)));
        coord.WaitForDurable(7, CancellationToken.None);
        Assert.Equal(7, coord.DurableSeq);
    }

    [Fact]
    public async Task AdvanceDurableSeqOnTruncate_pulses_waiters()
    {
        // groupCommit=true to enable parking, but the callback never
        // advances anything; AdvanceDurableSeqOnTruncate should.
        using var coord = new WalDurabilityCoordinator(
            1, groupCommit: true, TimeSpan.FromMilliseconds(50),
            () => 0, NullLogger.Instance);

        coord.RecordPending(9);
        Assert.Equal(0, coord.DurableSeq);

        var waiter = Task.Run(() => coord.WaitForDurable(9, CancellationToken.None));

        // Give the waiter a moment to park.
        await Task.Delay(50);
        coord.AdvanceDurableSeqOnTruncate();

        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(9, coord.DurableSeq);
    }

    [Fact]
    public async Task Dispose_joins_background_thread()
    {
        var coord = new WalDurabilityCoordinator(
            1, groupCommit: true, TimeSpan.FromMilliseconds(5),
            () => 0, NullLogger.Instance);

        var disposed = Task.Run(coord.Dispose);
        await disposed.WaitAsync(TimeSpan.FromSeconds(6));
    }
}
