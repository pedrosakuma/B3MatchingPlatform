namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// One scheduled action in a replay script. Parsed from a JSONL line in the
/// scenario file. <see cref="AtMs"/> is the monotonic offset, in milliseconds,
/// from the start of the run; the runner serialises events in
/// (<see cref="AtMs"/>, file-order) order.
/// </summary>
public sealed record ScriptEvent(
    long AtMs,
    ScriptEventKind Kind,
    ulong ClOrdId,
    long SecurityId,
    Side Side,
    OrderType Type,
    Tif Tif,
    long Quantity,
    long PriceMantissa,
    ulong OrigClOrdId,
    int LineNumber,
    string? Session = null);

public enum ScriptEventKind { New, Cancel }
public enum Side : byte { Buy, Sell }
public enum OrderType : byte { Limit, Market }
public enum Tif : byte { Day, IOC, FOK }
