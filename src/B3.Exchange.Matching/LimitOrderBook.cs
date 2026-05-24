namespace B3.Exchange.Matching;

/// <summary>
/// One resting order in the book. Mutable: <see cref="RemainingQuantity"/>
/// decreases as it is consumed by aggressors. Doubly-linked within its
/// <see cref="PriceLevel"/> for O(1) FIFO removal.
/// </summary>
internal sealed class RestingOrder
{
    public required long OrderId { get; init; }
    public required string ClOrdId { get; init; }
    public required Side Side { get; init; }
    public required long PriceMantissa { get; init; }
    public required uint EnteringFirm { get; init; }
    public byte OrdTagId { get; init; }
    public string? Asset { get; init; }
    public InvestorId? InvestorId { get; init; }
    public byte[] Memo { get; init; } = [];

    /// <summary>
    /// Wall-clock timestamp at which the order entered (or last re-entered)
    /// the book. Mutable so iceberg replenishment can update it in-place to
    /// the trade time without allocating a fresh <see cref="RestingOrder"/>.
    /// Issue #211 follow-up.
    /// </summary>
    public ulong InsertTimestampNanos;

    /// <summary>
    /// TIF the order was originally accepted with (Day or Gtc — the only
    /// TIFs that can rest). Tracked so a subsequent
    /// <see cref="ReplaceOrderCommand"/> that omits TIF on the wire
    /// preserves the resting order's original TIF instead of silently
    /// downgrading to <see cref="TimeInForce.Day"/>. Issue #204.
    /// </summary>
    public TimeInForce Tif { get; init; } = TimeInForce.Day;

    /// <summary>
    /// Iceberg visible-slice size (FIX MaxFloor). 0 means "not an iceberg"
    /// and <see cref="HiddenQuantity"/> is always 0 in that case. When
    /// non-zero, <see cref="RemainingQuantity"/> represents only the
    /// visible portion currently exposed in the book; the hidden reserve
    /// lives in <see cref="HiddenQuantity"/> and replenishes the visible
    /// slice when it is fully consumed (the order is then re-inserted at
    /// the back of the same price level, losing time priority).
    /// Issue #211.
    /// </summary>
    public long MaxFloor { get; init; }

    /// <summary>
    /// Hidden iceberg reserve. Mutable: decreases each time a fresh
    /// visible slice is taken from it. Always 0 when
    /// <see cref="MaxFloor"/> is 0. Issue #211.
    /// </summary>
    public long HiddenQuantity;

    public long RemainingQuantity;
    public PriceLevel? Level;
    public RestingOrder? Prev;
    public RestingOrder? Next;
}

/// <summary>
/// Doubly-linked FIFO queue of <see cref="RestingOrder"/> at a single price.
/// </summary>
internal sealed class PriceLevel
{
    public required long PriceMantissa { get; init; }
    public RestingOrder? Head;
    public RestingOrder? Tail;
    public long TotalQuantity;
    public int OrderCount;

    public void Append(RestingOrder o)
    {
        o.Level = this;
        o.Prev = Tail;
        o.Next = null;
        if (Tail is null) Head = o; else Tail.Next = o;
        Tail = o;
        TotalQuantity += o.RemainingQuantity;
        OrderCount++;
    }

    public void Remove(RestingOrder o)
    {
        if (o.Prev is null) Head = o.Next; else o.Prev.Next = o.Next;
        if (o.Next is null) Tail = o.Prev; else o.Next.Prev = o.Prev;
        TotalQuantity -= o.RemainingQuantity;
        OrderCount--;
        o.Level = null;
        o.Prev = null;
        o.Next = null;
    }
}

/// <summary>
/// Per-symbol price-time priority limit order book. Single-threaded access only.
/// </summary>
internal sealed class LimitOrderBook
{
    public long SecurityId { get; }
    private readonly SortedDictionary<long, PriceLevel> _bids;   // best = highest price → reverse comparer
    private readonly SortedDictionary<long, PriceLevel> _asks;   // best = lowest price → natural comparer
    private readonly Dictionary<long, RestingOrder> _byOrderId = new();

    // Reusable scratch buffer for OppositeLevels(). The book is single-
    // threaded and the two callers in MatchingEngine (the FOK STP
    // pre-check and the actual cross walk) execute strictly sequentially
    // within one Submit, so a single shared buffer is safe and
    // eliminates a per-aggressor List allocation that previously
    // dominated steady-state Gen0 pressure.
    private readonly List<PriceLevel> _oppositeScratch = new();

    public LimitOrderBook(long securityId)
    {
        SecurityId = securityId;
        _bids = new SortedDictionary<long, PriceLevel>(Comparer<long>.Create((a, b) => b.CompareTo(a)));
        _asks = new SortedDictionary<long, PriceLevel>();
    }

    public int OrderCount => _byOrderId.Count;

    /// <summary>
    /// Removes every resting order from this book without emitting any
    /// per-order events. Intended for operator-initiated channel resets
    /// (issue #6) where consumers receive a single <c>ChannelReset</c>
    /// frame and drop their local state — per-order DeleteOrder frames
    /// would be redundant and risk consumers seeing cancels for orders
    /// they have already discarded.
    /// </summary>
    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
        _byOrderId.Clear();
    }

    // Reusable scratch buffer for SnapshotOrders() (round-2 perf #10).
    // The book is single-threaded and the only production caller
    // (MatchingEngine.UncrossAuction's auction-survivor expiration
    // sweep) iterates the snapshot once, sequentially, without
    // nested calls back into this book. Reusing a single buffer
    // eliminates the per-uncross List allocation that the previous
    // ToList() implementation incurred.
    private readonly List<RestingOrder> _snapshotScratch = new();

    private SortedDictionary<long, PriceLevel> SideMap(Side side) => side == Side.Buy ? _bids : _asks;

    public bool TryGet(long orderId, out RestingOrder order) => _byOrderId.TryGetValue(orderId, out order!);

    /// <summary>
    /// Snapshot of all resting orders, taken eagerly so callers can mutate
    /// the book (cancel orders) while iterating. Used by the auction
    /// expiration sweep in <see cref="MatchingEngine.UncrossAuction"/> to
    /// find <see cref="TimeInForce.GoodForAuction"/> /
    /// <see cref="TimeInForce.AtClose"/> survivors. Issue #232 / Onda M5.
    ///
    /// <para>Returns a buffer owned by this book; the contents remain
    /// valid until the next call to <see cref="SnapshotOrders"/> on
    /// the same book. The single-thread invariant on the engine
    /// guarantees no concurrent or nested invocation. Round-2 perf #10
    /// — eliminates the per-call List allocation that
    /// <c>_byOrderId.Values.ToList()</c> incurred.</para>
    /// </summary>
    public IReadOnlyList<RestingOrder> SnapshotOrders()
    {
        var buf = _snapshotScratch;
        buf.Clear();
        foreach (var o in _byOrderId.Values)
            buf.Add(o);
        return buf;
    }

    public void Insert(RestingOrder o)
    {
        var map = SideMap(o.Side);
        if (!map.TryGetValue(o.PriceMantissa, out var level))
        {
            level = new PriceLevel { PriceMantissa = o.PriceMantissa };
            map.Add(o.PriceMantissa, level);
        }
        level.Append(o);
        _byOrderId.Add(o.OrderId, o);
    }

    public void Remove(RestingOrder o)
    {
        var level = o.Level ?? throw new InvalidOperationException("Order not on a level");
        var map = SideMap(o.Side);
        level.Remove(o);
        if (level.OrderCount == 0)
            map.Remove(level.PriceMantissa);
        _byOrderId.Remove(o.OrderId);
    }

    /// <summary>
    /// Enumerates aggregated price levels on <paramref name="side"/> in match
    /// priority order (best first). Each entry carries the price mantissa
    /// and the total resting quantity at that level. Used by the auction
    /// TOP / imbalance computation (#229) and any consumer that wants a
    /// price-aggregated view without iterating every order.
    /// </summary>
    public IEnumerable<(long PriceMantissa, long TotalQuantity)> EnumerateLevels(Side side)
    {
        foreach (var kv in SideMap(side))
            yield return (kv.Key, kv.Value.TotalQuantity);
    }

    /// <summary>Returns the best (top) price level on the given side, or null if empty.</summary>
    public PriceLevel? BestLevel(Side side)
    {
        var map = SideMap(side);
        if (map.Count == 0) return null;
        // SortedDictionary keys are sorted; First() yields the smallest key per the comparer
        // (which for bids is the LARGEST price thanks to the reversed comparer).
        foreach (var kv in map) return kv.Value;
        return null;
    }

    /// <summary>
    /// Total quantity available on the opposite side at prices that would cross
    /// against an aggressor of <paramref name="aggressorSide"/> with limit
    /// <paramref name="limitPriceMantissa"/> (or <see cref="long.MaxValue"/>/0
    /// to mean "any price" for market orders).
    /// </summary>
    public long FillableQuantityAgainst(Side aggressorSide, long limitPriceMantissa, bool isMarket)
    {
        var oppositeMap = SideMap(Opposite(aggressorSide));
        long sum = 0;
        foreach (var kv in oppositeMap)
        {
            long price = kv.Key;
            if (!isMarket && !PriceCrosses(aggressorSide, price, limitPriceMantissa)) break;
            sum += kv.Value.TotalQuantity;
        }
        return sum;
    }

    /// <summary>
    /// Iterates the opposite side level-by-level in match priority order. Used
    /// only by <see cref="MatchingEngine"/> during a cross.
    ///
    /// <para><b>Buffer reuse:</b> the returned list is the book's shared
    /// <c>_oppositeScratch</c> field, populated fresh on every call.
    /// Callers MUST iterate-to-completion (or copy the contents) before
    /// invoking <see cref="OppositeLevels"/> again on the same book —
    /// the next call will <c>Clear()</c> + refill the same instance.
    /// In practice the only callers are the two sequential phases of a
    /// single <c>MatchingEngine.Submit</c> (FOK STP pre-check, then the
    /// cross walk), and the engine is single-threaded per book, so this
    /// invariant holds without callers needing to defend.</para>
    /// </summary>
    internal List<PriceLevel> OppositeLevels(Side aggressorSide)
    {
        // Snapshot to a list because the engine mutates levels while iterating.
        var oppositeMap = SideMap(Opposite(aggressorSide));
        _oppositeScratch.Clear();
        if (_oppositeScratch.Capacity < oppositeMap.Count)
            _oppositeScratch.Capacity = oppositeMap.Count;
        foreach (var kv in oppositeMap) _oppositeScratch.Add(kv.Value);
        return _oppositeScratch;
    }

    /// <summary>
    /// Price-time-priority enumeration of every resting order on <paramref name="side"/>.
    /// Used by the snapshot generator.
    /// </summary>
    public IEnumerable<RestingOrderView> EnumerateOrders(Side side)
    {
        foreach (var kv in SideMap(side))
        {
            for (var o = kv.Value.Head; o is not null; o = o.Next)
            {
                yield return new RestingOrderView(
                    OrderId: o.OrderId,
                    Side: o.Side,
                    PriceMantissa: o.PriceMantissa,
                    RemainingQuantity: o.RemainingQuantity,
                    EnteringFirm: o.EnteringFirm,
                    InsertTimestampNanos: o.InsertTimestampNanos);
            }
        }
    }

    /// <summary>
    /// Persistence-friendly enumeration of every resting order on this book
    /// (issue #260). Yields in price-time priority and includes iceberg /
    /// TimeInForce fields that <see cref="RestingOrderView"/> intentionally
    /// hides. Used only by the snapshot capture path.
    /// </summary>
    public IEnumerable<RestingOrderRecord> EnumerateAllOrdersForSnapshot()
    {
        foreach (var side in new[] { Side.Buy, Side.Sell })
        {
            foreach (var kv in SideMap(side))
            {
                for (var o = kv.Value.Head; o is not null; o = o.Next)
                {
                    yield return new RestingOrderRecord(
                        OrderId: o.OrderId,
                        ClOrdId: o.ClOrdId,
                        Side: o.Side,
                        PriceMantissa: o.PriceMantissa,
                        RemainingQuantity: o.RemainingQuantity,
                        EnteringFirm: o.EnteringFirm,
                        InsertTimestampNanos: o.InsertTimestampNanos,
                        Tif: o.Tif,
                        MaxFloor: o.MaxFloor,
                        HiddenQuantity: o.HiddenQuantity,
                        OrdTagId: o.OrdTagId,
                        Asset: o.Asset,
                        InvestorId: o.InvestorId)
                    { Memo = o.Memo };
                }
            }
        }
    }

    /// <summary>
    /// Inserts a resting order rebuilt from a snapshot (issue #260).
    /// Insertion happens at the back of the price level, so callers MUST
    /// feed records in the order produced by
    /// <see cref="EnumerateAllOrdersForSnapshot"/> (price-time priority)
    /// to preserve FIFO ordering across restart.
    /// </summary>
    public void RestoreOrder(RestingOrderRecord record)
    {
        if (record.RemainingQuantity <= 0)
            throw new ArgumentException("RemainingQuantity must be positive", nameof(record));
        var order = new RestingOrder
        {
            OrderId = record.OrderId,
            ClOrdId = record.ClOrdId,
            Side = record.Side,
            PriceMantissa = record.PriceMantissa,
            EnteringFirm = record.EnteringFirm,
            OrdTagId = record.OrdTagId,
            Asset = record.Asset,
            InvestorId = record.InvestorId,
            InsertTimestampNanos = record.InsertTimestampNanos,
            Tif = record.Tif,
            MaxFloor = record.MaxFloor,
            HiddenQuantity = record.HiddenQuantity,
            RemainingQuantity = record.RemainingQuantity,
            Memo = record.Memo,
        };
        Insert(order);
    }

    public static Side Opposite(Side s) => s == Side.Buy ? Side.Sell : Side.Buy;

    public static bool PriceCrosses(Side aggressorSide, long oppositePrice, long aggressorLimit)
        => aggressorSide == Side.Buy ? oppositePrice <= aggressorLimit : oppositePrice >= aggressorLimit;
}

/// <summary>
/// Public, immutable view of a resting order — used by snapshot enumeration so
/// the engine never leaks its mutable internal node.
/// </summary>
public readonly record struct RestingOrderView(
    long OrderId,
    Side Side,
    long PriceMantissa,
    long RemainingQuantity,
    uint EnteringFirm,
    ulong InsertTimestampNanos);
