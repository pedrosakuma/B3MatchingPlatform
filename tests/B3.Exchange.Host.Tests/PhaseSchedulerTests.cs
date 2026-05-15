using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #321: unit tests for <see cref="PhaseScheduler"/>. Uses a fake
/// <see cref="IPhaseScheduledTarget"/> and the <c>RunDueNow</c> test seam
/// so we never wait on real timers or wall-clock.
/// </summary>
public class PhaseSchedulerTests
{
    private sealed class FakeTarget : IPhaseScheduledTarget
    {
        public List<(long SecId, TradingPhase Target, bool Uncross)> Calls { get; } = new();
        private readonly long[] _ids;
        public FakeTarget(params long[] ids) { _ids = ids; }
        public IReadOnlyCollection<long> RoutedSecurityIds => _ids;
        public bool EnqueueSetPhase(long securityId, TradingPhase target)
        {
            Calls.Add((securityId, target, false));
            return true;
        }
        public bool EnqueueUncrossAuction(long securityId, TradingPhase target)
        {
            Calls.Add((securityId, target, true));
            return true;
        }
    }

    private static PhaseSchedulerConfig CfgWith(params PhaseSchedulerEntry[] schedule)
        => new()
        {
            Enabled = true,
            Groups = new[]
            {
                new PhaseSchedulerGroup
                {
                    Name = "test",
                    Timezone = "UTC",
                    Schedule = schedule,
                },
            },
        };

    [Fact]
    public async Task RunDueNow_FiresEntryAtItsLocalTime_RoutingByAction()
    {
        var fake = new FakeTarget(101L, 102L);
        var cfg = CfgWith(
            new PhaseSchedulerEntry { At = "10:00", Action = "UncrossAuction", Target = "Open" },
            new PhaseSchedulerEntry { At = "12:00", Action = "EnterPhase", Target = "Pause" });
        // utcNow == 10:00:00.5 UTC: only the 10:00 entry should fire.
        var clock = new DateTimeOffset(2025, 1, 6, 10, 0, 0, 500, TimeSpan.Zero);
        await using var sched = new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance, utcNowOverride: () => clock);

        var fired = sched.RunDueNow();

        Assert.Equal(1, fired); // one entry fires; instrument fan-out is in Calls
        Assert.Equal(2, fake.Calls.Count);
        Assert.All(fake.Calls, c => Assert.True(c.Uncross));
        Assert.All(fake.Calls, c => Assert.Equal(TradingPhase.Open, c.Target));
        Assert.Contains(fake.Calls, c => c.SecId == 101L);
        Assert.Contains(fake.Calls, c => c.SecId == 102L);
    }

    [Fact]
    public async Task RunDueNow_FiresOnlyEntriesInCurrentSecondWindow()
    {
        var fake = new FakeTarget(7L);
        var cfg = CfgWith(
            new PhaseSchedulerEntry { At = "09:00", Action = "EnterPhase", Target = "Reserved" },
            new PhaseSchedulerEntry { At = "10:00", Action = "UncrossAuction", Target = "Open" });
        var clock = new DateTimeOffset(2025, 1, 6, 9, 0, 0, 500, TimeSpan.Zero);
        await using var sched = new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance, utcNowOverride: () => clock);

        Assert.Equal(1, sched.RunDueNow());
        Assert.Single(fake.Calls);
        Assert.Equal(TradingPhase.Reserved, fake.Calls[0].Target);
        Assert.False(fake.Calls[0].Uncross);

        // Advance clock to the second entry's window.
        clock = new DateTimeOffset(2025, 1, 6, 10, 0, 0, 500, TimeSpan.Zero);
        Assert.Equal(1, sched.RunDueNow());
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(TradingPhase.Open, fake.Calls[1].Target);
        Assert.True(fake.Calls[1].Uncross);
    }

    [Fact]
    public async Task RunDueNow_FiresNothing_BeforeFirstEntry()
    {
        var fake = new FakeTarget(1L);
        var cfg = CfgWith(
            new PhaseSchedulerEntry { At = "10:00", Action = "EnterPhase", Target = "Open" });
        var clock = new DateTimeOffset(2025, 1, 6, 9, 30, 0, TimeSpan.Zero);
        await using var sched = new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance, utcNowOverride: () => clock);

        Assert.Equal(0, sched.RunDueNow());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task RunDueNow_ExplicitInstrumentList_OverridesRoutedDefault()
    {
        var fake = new FakeTarget(1L, 2L, 3L);
        var cfg = new PhaseSchedulerConfig
        {
            Enabled = true,
            Groups = new[]
            {
                new PhaseSchedulerGroup
                {
                    Name = "subset",
                    Timezone = "UTC",
                    Instruments = new[] { 2L },
                    Schedule = new[]
                    {
                        new PhaseSchedulerEntry { At = "10:00", Action = "EnterPhase", Target = "Open" },
                    },
                },
            },
        };
        var clock = new DateTimeOffset(2025, 1, 6, 10, 0, 0, 500, TimeSpan.Zero);
        await using var sched = new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance, utcNowOverride: () => clock);

        sched.RunDueNow();

        Assert.Single(fake.Calls);
        Assert.Equal(2L, fake.Calls[0].SecId);
    }

    [Fact]
    public void Ctor_RejectsBadHHmm()
    {
        var fake = new FakeTarget(1L);
        var cfg = CfgWith(new PhaseSchedulerEntry { At = "25:99", Action = "EnterPhase", Target = "Open" });
        Assert.ThrowsAny<Exception>(() => new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance));
    }

    [Fact]
    public void Ctor_RejectsBadAction()
    {
        var fake = new FakeTarget(1L);
        var cfg = CfgWith(new PhaseSchedulerEntry { At = "10:00", Action = "Bogus", Target = "Open" });
        Assert.Throws<FormatException>(() => new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance));
    }

    [Fact]
    public void Ctor_RejectsBadPhase()
    {
        var fake = new FakeTarget(1L);
        var cfg = CfgWith(new PhaseSchedulerEntry { At = "10:00", Action = "EnterPhase", Target = "Bogus" });
        Assert.Throws<FormatException>(() => new PhaseScheduler(cfg, fake, metrics: null,
            logger: NullLogger<PhaseScheduler>.Instance));
    }
}
