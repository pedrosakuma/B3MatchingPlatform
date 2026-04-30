namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Per-instrument strategy. Pure-ish: <see cref="Tick"/> consumes a snapshot
/// and a seeded RNG and emits intents. The runner calls the event callbacks
/// before <see cref="Tick"/> on the same dispatch thread, so strategies do
/// not need to be thread-safe internally.
///
/// This shape is intentionally minimal so future strategies (issue #13)
/// can plug in without touching the runner.
/// </summary>
public interface IStrategy
{
    /// <summary>
    /// Stable identifier for logs.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Produce zero or more intents for this tick. Must be deterministic
    /// for a given (state, rng-state) pair.
    /// </summary>
    IEnumerable<OrderIntent> Tick(in MarketState state, Random rng);

    /// <summary>Called when the runner sees an ER_New for one of our orders.</summary>
    void OnAck(string clientTag, long orderId) { }

    /// <summary>Called for each ER_Trade on one of our orders (active or passive).</summary>
    void OnFill(in FillEvent fill) { }

    /// <summary>Called when one of our orders is cancelled (ER_Cancel) or rejected.</summary>
    void OnRemoved(string clientTag) { }
}
