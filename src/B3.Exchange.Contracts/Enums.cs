namespace B3.Exchange.Contracts;

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
}

/// <summary>Time-in-force. Wire-neutral mirror of FIX tag 59.</summary>
public enum TimeInForce : byte
{
    Day = 0,
    IOC = 3,
    FOK = 4,
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
    Other = 99,
}
