namespace B3.Exchange.Host.Tests;

/// <summary>
/// #504 — unit tests for <see cref="GtdExpirySweeper.ToLocalMktDate"/>, the
/// calendar-date → B3 wire <c>LocalMktDate</c> (days since Unix epoch)
/// converter shared by the boundary sweep and the decode-time stale-GTD
/// guard.
/// </summary>
public class GtdExpirySweeperToLocalMktDateTests
{
    [Fact]
    public void UnixEpoch_IsZero()
    {
        Assert.Equal((ushort)0, GtdExpirySweeper.ToLocalMktDate(new DateOnly(1970, 1, 1)));
    }

    [Fact]
    public void KnownDate_MatchesDayNumberDelta()
    {
        var date = new DateOnly(2024, 1, 1);
        int expected = date.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        Assert.Equal((ushort)expected, GtdExpirySweeper.ToLocalMktDate(date));
    }

    [Fact]
    public void BeforeEpoch_ReturnsNull()
    {
        Assert.Null(GtdExpirySweeper.ToLocalMktDate(new DateOnly(1969, 12, 31)));
    }

    [Fact]
    public void BeyondUshortRange_ReturnsNull()
    {
        // 1970-01-01 + 65536 days is one past the ushort ceiling.
        var overflow = new DateOnly(1970, 1, 1).AddDays(ushort.MaxValue + 1);
        Assert.Null(GtdExpirySweeper.ToLocalMktDate(overflow));
    }

    [Fact]
    public void UshortCeiling_IsRepresentable()
    {
        var ceiling = new DateOnly(1970, 1, 1).AddDays(ushort.MaxValue);
        Assert.Equal(ushort.MaxValue, GtdExpirySweeper.ToLocalMktDate(ceiling));
    }
}
