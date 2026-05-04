using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using B3.Exchange.Gateway;
using B3.Exchange.Instruments;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Issue #218 (Onda L · L5) integration tests for the dispatcher's
/// expanded NewOrderCross handling: <see cref="CrossPrioritization"/>
/// affects submission order and <see cref="CrossType.AgainstBook"/> with
/// <see cref="CrossOrderCommand.MaxSweepQty"/> sweeps the opposing public
/// book before printing the residual internally between the two legs.
/// </summary>
public class CrossOrderSemanticsTests
{
    private const long Petr = 900_000_000_001L;
    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Petr,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };
    private static long Px(decimal p) => (long)(p * 10_000m);

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Add(packet.ToArray());
    }

    private sealed class FakeSession
    {
        private static int _nextId;
        public B3.Exchange.Contracts.SessionId Id { get; } = new("cs" + System.Threading.Interlocked.Increment(ref _nextId));
        public uint EnteringFirm { get; init; } = 7;
        public List<TradeEvent> Trades { get; } = new();
        public List<bool> TradeIsAggressor { get; } = new();
        public List<OrderAcceptedEvent> News { get; } = new();
        public List<OrderCanceledEvent> Cancels { get; } = new();
        public FakeSession(RecordingOutbound o) { o.Register(this); }
    }

    private sealed class RecordingOutbound : ICoreOutbound
    {
        private readonly Dictionary<B3.Exchange.Contracts.SessionId, FakeSession> _sessions = new();
        public void Register(FakeSession s) => _sessions[s.Id] = s;

        public bool WriteExecutionReportNew(SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue)
        {
            if (_sessions.TryGetValue(session, out var s)) s.News.Add(e);
            return true;
        }

        public bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
        {
            if (_sessions.TryGetValue(session, out var s)) { s.Trades.Add(e); s.TradeIsAggressor.Add(isAggressor); }
            return true;
        }

        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty)
        {
            if (_sessions.TryGetValue(ownerSession, out var s)) { s.Trades.Add(e); s.TradeIsAggressor.Add(false); }
            return true;
        }

        public bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue)
        {
            if (_sessions.TryGetValue(ownerSession, out var s)) s.Cancels.Add(e);
            return true;
        }

        public bool WriteExecutionReportModify(SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue) => true;

        public bool WriteExecutionReportReject(SessionId session, in B3.Exchange.Matching.RejectEvent e, ulong clOrdIdValue) => true;
    }

    private static (ChannelDispatcher disp, RecordingPacketSink pkt, RecordingOutbound outbound) NewDispatcher()
    {
        var pkt = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            outbound: outbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            nowNanos: () => 1_000_000_000UL, tradeDate: 19_000);
        return (disp, pkt, outbound);
    }

    private static void DrainInbound(ChannelDispatcher disp) => disp.CreateTestProbe().DrainInbound();

    private static NewOrderCommand Buy(long qty, decimal price, ulong clOrdId = 1UL)
        => new("B" + clOrdId, Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(price), qty, 7, 1_000UL);
    private static NewOrderCommand Sell(long qty, decimal price, ulong clOrdId = 2UL)
        => new("S" + clOrdId, Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(price), qty, 7, 1_000UL);

    [Fact]
    public void Aon_NoPrioritization_BuyRestsThenSellCrossesInternally()
    {
        // Default behavior path: AON cross + None prioritization → Buy
        // submits first and rests, Sell crosses against it → 1 internal trade.
        var (disp, _, outbound) = NewDispatcher();
        var s = new FakeSession(outbound);
        var cross = new CrossOrderCommand(Buy(100, 10m), Sell(100, 10m), 1UL, 2UL, CrossId: 999UL);
        disp.EnqueueCross(cross, s.Id, s.EnteringFirm);
        DrainInbound(disp);

        // Buy News (rests) + 1 Trade event delivered to BOTH aggressor (Sell)
        // and resting (Buy) — both legs are owned by the same FakeSession.
        Assert.Single(s.News);
        Assert.Equal(2, s.Trades.Count); // aggressor + passive
        Assert.Equal(Px(10m), s.Trades[0].PriceMantissa);
        Assert.Equal(100, s.Trades[0].Quantity);
    }

    [Fact]
    public void Aon_SellPrioritized_SellRestsThenBuyCrosses()
    {
        var (disp, _, outbound) = NewDispatcher();
        var s = new FakeSession(outbound);
        var cross = new CrossOrderCommand(Buy(100, 10m), Sell(100, 10m), 1UL, 2UL, 999UL)
        {
            CrossPrioritization = CrossPrioritization.SellPrioritized,
        };
        disp.EnqueueCross(cross, s.Id, s.EnteringFirm);
        DrainInbound(disp);

        // The single News event must be the Sell leg (rested first).
        Assert.Single(s.News);
        Assert.Equal(Side.Sell, s.News[0].Side);
        // 1 internal trade still happens.
        Assert.Equal(2, s.Trades.Count);
    }

    [Fact]
    public void AgainstBook_NoOpposingLiquidity_BehavesLikeAon()
    {
        // AgainstBook + MaxSweepQty=50, but the opposing book is empty:
        // sweep IOC executes nothing, residual = full qty, internal print
        // takes the entire cross qty.
        var (disp, _, outbound) = NewDispatcher();
        var s = new FakeSession(outbound);
        var cross = new CrossOrderCommand(Buy(100, 10m), Sell(100, 10m), 1UL, 2UL, 999UL)
        {
            CrossType = CrossType.AgainstBook,
            MaxSweepQty = 50,
        };
        disp.EnqueueCross(cross, s.Id, s.EnteringFirm);
        DrainInbound(disp);

        // No external book → sweep produced 0 trades; residual = 100; the
        // other leg crosses for 100 internally.
        var internalTrades = s.Trades.Where(t => t.Quantity == 100).ToList();
        Assert.NotEmpty(internalTrades);
        // Residual sell leg is fully consumed by internal print → no
        // resting sell remains. The buy cross-residual rested first then
        // was filled by sell — final: book empty, no News for sell rests.
    }

    [Fact]
    public void AgainstBook_WithExternalLiquidity_SweepsBookThenPrintsResidual()
    {
        // External seller resting at a better price than the cross.
        var (disp, _, outbound) = NewDispatcher();
        var external = new FakeSession(outbound);
        var crosser = new FakeSession(outbound);

        // External Sell 100 @ 9.99 (better than cross price 10.00 for the buyer).
        // Use multiples of lot size (100) — engine rejects fractional lots.
        disp.EnqueueNewOrder(
            new NewOrderCommand("EXT", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, external.EnteringFirm, 1_000UL),
            external.Id, external.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Single(external.News);

        // Cross Buy=Sell=200 @ 10.00 with MaxSweepQty=100, Buy prioritized:
        // Phase 1: Buy IOC 100 @ 10 sweeps the external sell 100 @ 9.99.
        // Phase 2: Buy residual 100 @ 10 rests (book empty after sweep).
        // Phase 3: Sell 200 @ 10 fills 100 internally vs the rested Buy and
        // 100 rests on the sell side.
        var cross = new CrossOrderCommand(Buy(200, 10m, 10UL), Sell(200, 10m, 11UL), 10UL, 11UL, 999UL)
        {
            CrossType = CrossType.AgainstBook,
            CrossPrioritization = CrossPrioritization.BuyPrioritized,
            MaxSweepQty = 100,
        };
        disp.EnqueueCross(cross, crosser.Id, crosser.EnteringFirm);
        DrainInbound(disp);

        // External resting seller got hit for 100 at 9.99.
        var externalFills = external.Trades.Where(t => t.PriceMantissa == Px(9.99m)).ToList();
        Assert.Single(externalFills);
        Assert.Equal(100, externalFills[0].Quantity);

        // The crosser sees: (a) sweep aggressor fill of 100 at 9.99,
        // (b) internal cross prints at 10.00 between the two cross legs.
        var crosserSweepFills = crosser.Trades.Where(t => t.PriceMantissa == Px(9.99m)).ToList();
        Assert.Single(crosserSweepFills);
        Assert.Equal(100, crosserSweepFills[0].Quantity);

        var crosserInternalFills = crosser.Trades.Where(t => t.PriceMantissa == Px(10m)).ToList();
        // Internal print: residual buy of 100 against sell leg of 200 →
        // emitted as aggressor Trade (sell aggressing) AND passive Trade
        // (buy resting) on the same FakeSession (crosser owns both legs).
        Assert.NotEmpty(crosserInternalFills);
        Assert.Contains(crosserInternalFills, t => t.Quantity == 100);
    }

    [Fact]
    public void AgainstBook_MaxSweepQtyZero_StillBehavesLikeAon()
    {
        // Defensive: AgainstBook with MaxSweepQty=0 falls through to the
        // AON path (no sweep phase performed).
        var (disp, _, outbound) = NewDispatcher();
        var s = new FakeSession(outbound);
        var cross = new CrossOrderCommand(Buy(100, 10m), Sell(100, 10m), 1UL, 2UL, 999UL)
        {
            CrossType = CrossType.AgainstBook,
            MaxSweepQty = 0,
        };
        disp.EnqueueCross(cross, s.Id, s.EnteringFirm);
        DrainInbound(disp);

        Assert.Single(s.News);
        Assert.Equal(2, s.Trades.Count);
        Assert.Equal(100, s.Trades[0].Quantity);
    }
}
