using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

/// <summary>ADR 0008 PR-2: validator decision matrix.</summary>
public class BustValidatorTests : IDisposable
{
    private readonly string _root;
    public BustValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BustValidatorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly ulong Day0Nanos = (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
    private static readonly DateOnly Day0 = new(2026, 5, 18);
    private const long SecId = 900_000_000_777L;

    private static PostTradeRecord Fill(uint id) => new(
        TradeId: id, TransactTimeNanos: Day0Nanos, SecurityId: SecId, AggressorSide: Side.Buy,
        Quantity: 100, PriceMantissa: 10_0000L,
        BuyClOrdId: 1, SellClOrdId: 2, BuyFirm: 10, SellFirm: 20, BuyOrderId: 5, SellOrderId: 6);

    private void Seed(byte channel, params uint[] tradeIds)
    {
        using var w = new FileAuditLogWriter(_root, channelNumber: channel);
        foreach (var id in tradeIds) w.OnTrade(Fill(id));
    }

    private static BustRequest Req(uint tradeId, ulong corr, long? echo = null) =>
        new(tradeId, Day0, corr, echo, ReasonCode: 1, BusterFirm: 99, AttemptTransactTimeNanos: Day0Nanos + 1);

    [Fact]
    public void Accept_WhenTradeIdExistsAndNoPriorBust()
    {
        Seed(channel: 1, 100u);
        var dedup = new BustDedupIndex();
        var result = BustValidator.Validate(_root, 1, Req(100u, corr: 5UL, echo: SecId), dedup);
        Assert.Equal(BustValidationKind.Accept, result.Kind);
        Assert.Equal(100u, result.MatchedFill.TradeId);
        Assert.Equal(SecId, result.MatchedFill.SecurityId);
    }

    [Fact]
    public void IdempotentReplay_WhenSameTripleSeenBefore()
    {
        Seed(channel: 2, 100u);
        var dedup = new BustDedupIndex();
        dedup.Add(100u, correlationId: 5UL, tradeDate: Day0);
        var result = BustValidator.Validate(_root, 2, Req(100u, corr: 5UL), dedup);
        Assert.Equal(BustValidationKind.IdempotentReplay, result.Kind);
    }

    [Fact]
    public void AlreadyBustedDifferentCorrelation_WhenCorrIdDiffers()
    {
        Seed(channel: 3, 100u);
        var dedup = new BustDedupIndex();
        dedup.Add(100u, correlationId: 5UL, tradeDate: Day0);
        var result = BustValidator.Validate(_root, 3, Req(100u, corr: 999UL), dedup);
        Assert.Equal(BustValidationKind.AlreadyBustedDifferentCorrelation, result.Kind);
        Assert.Equal(5UL, result.ExistingCorrelationId);
    }

    [Fact]
    public void MissingDay_WhenLogFileNotPresent()
    {
        var dedup = new BustDedupIndex();
        var result = BustValidator.Validate(_root, 4, Req(100u, corr: 5UL), dedup);
        Assert.Equal(BustValidationKind.MissingDay, result.Kind);
    }

    [Fact]
    public void UnknownTradeId_WhenLogExistsButTradeIdMissing()
    {
        Seed(channel: 5, 1u, 2u, 3u);
        var dedup = new BustDedupIndex();
        var result = BustValidator.Validate(_root, 5, Req(99u, corr: 5UL), dedup);
        Assert.Equal(BustValidationKind.UnknownTradeId, result.Kind);
    }

    [Fact]
    public void SecurityIdMismatch_WhenEchoDoesNotMatchFill()
    {
        Seed(channel: 6, 100u);
        var dedup = new BustDedupIndex();
        var result = BustValidator.Validate(_root, 6, Req(100u, corr: 5UL, echo: SecId + 1), dedup);
        Assert.Equal(BustValidationKind.SecurityIdMismatch, result.Kind);
    }

    [Fact]
    public void SecurityIdEcho_Optional_AcceptedWhenAbsent()
    {
        Seed(channel: 7, 100u);
        var dedup = new BustDedupIndex();
        var result = BustValidator.Validate(_root, 7, Req(100u, corr: 5UL, echo: null), dedup);
        Assert.Equal(BustValidationKind.Accept, result.Kind);
    }
}
