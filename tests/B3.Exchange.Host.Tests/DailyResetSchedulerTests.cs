using B3.Exchange.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Pure unit tests for <see cref="DailyResetScheduler.ComputeNextFiringUtc"/>.
/// Schedules and timezones are deterministic given a fixed "now"; we use
/// America/Sao_Paulo because that's the production default and because
/// it currently does NOT observe DST (offset is fixed UTC-3 since 2019),
/// which keeps the assertions stable. UTC is also exercised as a sanity
/// check that requires no tzdata fixups.
/// </summary>
public class DailyResetSchedulerTests
{
    [Fact]
    public void NextFiring_BeforeTodaysSchedule_ReturnsTodayInUtc()
    {
        var brt = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        // 2026-04-30 12:00 BRT == 15:00Z. Schedule is 18:00 BRT == 21:00Z
        // same day. So we expect today.
        var now = new DateTimeOffset(2026, 4, 30, 15, 0, 0, TimeSpan.Zero);
        var next = DailyResetScheduler.ComputeNextFiringUtc(now, TimeSpan.FromHours(18), brt);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 21, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextFiring_AfterTodaysSchedule_ReturnsTomorrowInUtc()
    {
        var brt = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        // 2026-04-30 22:00 BRT == 2026-05-01 01:00Z. Schedule 18:00 BRT
        // already passed today, so expect tomorrow at 18:00 BRT == 21:00Z.
        var now = new DateTimeOffset(2026, 5, 1, 1, 0, 0, TimeSpan.Zero);
        var next = DailyResetScheduler.ComputeNextFiringUtc(now, TimeSpan.FromHours(18), brt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 21, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextFiring_ExactBoundary_RollsToNextDay()
    {
        var brt = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        // Now is exactly the schedule instant — must be STRICTLY after,
        // so we get tomorrow.
        var now = new DateTimeOffset(2026, 4, 30, 21, 0, 0, TimeSpan.Zero);
        var next = DailyResetScheduler.ComputeNextFiringUtc(now, TimeSpan.FromHours(18), brt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 21, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextFiring_Utc_ReturnsExpectedInstant()
    {
        var utc = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 4, 30, 5, 0, 0, TimeSpan.Zero);
        var next = DailyResetScheduler.ComputeNextFiringUtc(now, TimeSpan.FromHours(18), utc);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 18, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Constructor_RejectsBadSchedule()
    {
        Action a = () => new DailyResetScheduler(
            "25:00", "UTC", () => { }, NullLogger<DailyResetScheduler>.Instance);
        Assert.Throws<FormatException>(a);
    }

    [Fact]
    public void Constructor_RejectsBadTimezone()
    {
        Action a = () => new DailyResetScheduler(
            "18:00", "Mars/Olympus", () => { }, NullLogger<DailyResetScheduler>.Instance);
        Assert.Throws<ArgumentException>(a);
    }

    [Fact]
    public void NextFiring_DstFallBack_PicksEarlierUtcInstant()
    {
        // America/New_York fall-back 2026: clocks move 02:00 EDT (UTC-4)
        // back to 01:00 EST (UTC-5) on 2026-11-01. A schedule of "01:30"
        // local is therefore ambiguous on that date — there is one
        // 01:30 EDT (= 05:30 UTC) and one 01:30 EST (= 06:30 UTC). The
        // scheduler must pick the EARLIER UTC instant (05:30 UTC) so
        // the rollover does not fire twice during the repeated hour.
        TimeZoneInfo ny;
        try { ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return; /* tzdata unavailable */ }
        var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero); // 2026-11-01 00:00Z
        var next = DailyResetScheduler.ComputeNextFiringUtc(now, TimeSpan.FromMinutes(90), ny);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextFiring_DstSpringForward_SkipsInvalidLocalTimeToNextDay()
    {
        // America/New_York spring-forward 2026: 02:00 → 03:00 on
        // 2026-03-08, so 02:30 local does not exist that day. The
        // scheduler must skip to the next valid day's 02:30 EDT.
        TimeZoneInfo ny;
        try { ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch (TimeZoneNotFoundException) { return; /* tzdata unavailable */ }
        var now = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero); // 00:00 EST
        var next = DailyResetScheduler.ComputeNextFiringUtc(now, TimeSpan.FromMinutes(150), ny);
        // 02:30 on 2026-03-09 is EDT (UTC-4) → 06:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Trigger_InvokesAction()
    {
        int count = 0;
        var s = new DailyResetScheduler("18:00", "UTC", () => Interlocked.Increment(ref count),
            NullLogger<DailyResetScheduler>.Instance);
        s.Trigger();
        s.Trigger();
        Assert.Equal(2, count);
    }
}
