namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Per-instrument strategy. Pure-ish: <see cref="Tick"/> consumes a snapshot
/// and a seeded RNG and emits intents.
///
/// <para><b>Threading contract.</b> <see cref="Tick"/> is invoked on the
/// <see cref="SyntheticTraderRunner"/>'s tick task, while
/// <see cref="OnAck"/>, <see cref="OnFill"/>, and <see cref="OnRemoved"/>
/// are dispatched from the <see cref="EntryPointClient"/>'s receive task.
/// These run concurrently, so any state shared between <see cref="Tick"/>
/// and the event callbacks <b>must be guarded</b> (e.g. with the per-strategy
/// lock the runner holds when mutating its own per-strategy maps, or with
/// the strategy's own synchronization). Implementations that do not share
/// mutable state across these methods can safely ignore this constraint.</para>
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
