namespace B3.Exchange.Contracts;

// CONVENTION (#156): The enum families in B3.Exchange.Contracts use
// FIX wire numbering (e.g. Side.Buy = '1' = byte 1). The parallel
// enums in B3.Exchange.Matching (Side, OrderType, TimeInForce) use
// internal 0-indexed declaration order. The two families are NOT
// directly castable — translation goes through the canonical mapping
// table pinned by EnumMappingTests in B3.Exchange.Core.Tests. Adding
// or renumbering a value in either family requires updating the
// table, otherwise CI breaks.

/// <summary>Side of an order. Wire-neutral mirror of FIX tag 54.</summary>
public enum Side : byte
{
    Buy = 1,
    Sell = 2,
}

/// <summary>Order type. Wire-neutral mirror of FIX tag 40.</summary>
public enum OrderType : byte
{
    Market = 1,
    Limit = 2,
    StopLoss = 3,
    StopLimit = 4,
    /// <summary>
    /// Market with leftover-as-limit (FIX OrdType 'K' / B3
    /// MARKET_WITH_LEFTOVER_AS_LIMIT). Sweeps the opposite side like a
    /// Market; any unfilled remainder rests at the last execution price
    /// as a Day Limit. Issue #215.
    /// </summary>
    MarketWithLeftover = 5,
}

/// <summary>Time-in-force. Wire-neutral mirror of FIX tag 59.</summary>
public enum TimeInForce : byte
{
    Day = 0,
    IOC = 3,
    FOK = 4,
    Gtc = 1,
    Gtd = 6,
    AtClose = 7,
    GoodForAuction = (byte)'A',
}

/// <summary>
/// Type of execution event reported by Core. Mirrors FIX
/// <c>ExecType</c> (tag 150) values used by the EntryPoint protocol.
/// </summary>
public enum ExecType : byte
{
    New = 0,
    PartialFill = 1,
    Fill = 2,
    Cancelled = 4,
    Replaced = 5,
    Rejected = 8,
    Expired = 12,
}

/// <summary>
/// Reason for a Core-side reject. Maps 1:1 to FIX
/// <c>OrdRejReason</c> (tag 103) wire codes per spec §3 — keeps Core
/// independent of the EntryPoint encoder while preserving the wire
/// numbering for downstream mapping.
/// </summary>
public enum OrdRejReason : byte
{
    BrokerExchangeOption = 0,
    UnknownSymbol = 1,
    OrderExceedsLimit = 3,
    UnknownOrder = 5,
    DuplicateOrder = 6,
    UnsupportedOrderCharacteristic = 11,
    ExchangeClosed = 13,
    Other = 99,
}
