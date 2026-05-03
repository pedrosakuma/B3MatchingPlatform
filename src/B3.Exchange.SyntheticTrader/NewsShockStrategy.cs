namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Stateful news-shock strategy (issue #117). Cycles through three phases:
///
/// <list type="number">
///   <item><b>Idle</b>: emits no orders. Waits a random number of ticks
///   (<c>meanIntervalTicks</c> ± <c>jitterTicks</c>) until the next
///   trigger fires.</item>
///   <item><b>Shock</b>: lasts <c>shockDurationTicks</c> ticks. On each
///   tick, picks a side (biased by <see cref="DirectionBias"/>) and
///   emits one marketable IOC limit sized
///   <c>burstLots × lotSize</c> at <c>levelsToSweep × tickSize</c>
///   through the mid, designed to walk several resting levels.</item>
///   <item><b>Fade</b>: lasts <c>fadeDurationTicks</c> ticks. Burst
///   quantity decays linearly from full to zero over the window so the
///   shock tapers rather than ending abruptly. Side is unchanged.</item>
/// </list>
///
/// <para>The strategy is deterministic for a given (RNG, tick-sequence)
/// pair — no wall-clock dependency. All ms-based config is converted to
/// tick counts using the <c>tickIntervalMs</c> from the runner so the
/// strategy can advance its phase machine purely on tick-count math.</para>
///
/// <para>The shock-side draw uses <see cref="Random.NextDouble"/> against
/// <see cref="DirectionBias"/>: a value &lt; bias means BUY (positive
/// shock), otherwise SELL. <c>directionBias = 0.5</c> ⇒ symmetric.</para>
///
/// <para>Pairs naturally with a <see cref="MarketMakerStrategy"/> on the
/// same instrument: the maker provides the resting book that the shock
/// then walks through. Without a maker, the shock orders will simply
/// rest as aggressive limits and exercise the cancel/expiry path.</para>
/// </summary>
public sealed class NewsShockStrategy : IStrategy
{
    public enum Phase { Idle, Shock, Fade }

    private readonly int _meanIntervalTicks;
    private readonly int _jitterTicks;
    private readonly int _shockDurationTicks;
    private readonly int _fadeDurationTicks;

    private Phase _phase;
    private int _phaseTicksRemaining;
    private int _shockDurationActual;
    private int _fadeDurationActual;
    private OrderSide _shockSide;
    private long _seq;

    public string Name { get; }
    public int LevelsToSweep { get; }
    public long BurstLots { get; }
    public double DirectionBias { get; }

    /// <summary>
    /// Read-only view of the current phase, for tests and logging.
    /// </summary>
    public Phase CurrentPhase => _phase;

    public NewsShockStrategy(
        string name,
        int tickIntervalMs,
        int meanIntervalMs,
        int jitterMs,
        int shockDurationMs,
        int fadeDurationMs,
        int levelsToSweep,
        long burstLots,
        double directionBias)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        if (tickIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(tickIntervalMs));
        if (meanIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(meanIntervalMs));
        if (jitterMs < 0) throw new ArgumentOutOfRangeException(nameof(jitterMs));
        if (jitterMs >= meanIntervalMs) throw new ArgumentOutOfRangeException(nameof(jitterMs),
            "jitterMs must be < meanIntervalMs so the trigger interval stays positive");
        if (shockDurationMs <= 0) throw new ArgumentOutOfRangeException(nameof(shockDurationMs));
        if (fadeDurationMs < 0) throw new ArgumentOutOfRangeException(nameof(fadeDurationMs));
        if (levelsToSweep <= 0) throw new ArgumentOutOfRangeException(nameof(levelsToSweep));
        if (burstLots <= 0) throw new ArgumentOutOfRangeException(nameof(burstLots));
        if (directionBias < 0 || directionBias > 1) throw new ArgumentOutOfRangeException(nameof(directionBias));

        Name = name;
        LevelsToSweep = levelsToSweep;
        BurstLots = burstLots;
        DirectionBias = directionBias;

        // Convert ms to tick counts; floor to >= 1 tick for non-zero windows.
        _meanIntervalTicks = Math.Max(1, meanIntervalMs / tickIntervalMs);
        _jitterTicks = jitterMs / tickIntervalMs;
        _shockDurationTicks = Math.Max(1, shockDurationMs / tickIntervalMs);
        _fadeDurationTicks = fadeDurationMs == 0 ? 0 : Math.Max(1, fadeDurationMs / tickIntervalMs);

        _phase = Phase.Idle;
        _phaseTicksRemaining = -1; // first tick will roll an interval
    }

    public IEnumerable<OrderIntent> Tick(in MarketState state, Random rng)
    {
        switch (_phase)
        {
            case Phase.Idle:
                if (_phaseTicksRemaining < 0)
                    _phaseTicksRemaining = RollIdleTicks(rng);
                if (_phaseTicksRemaining > 1)
                {
                    _phaseTicksRemaining--;
                    return Array.Empty<OrderIntent>();
                }
                // Trigger fires this tick: choose side, transition to Shock.
                _shockSide = rng.NextDouble() < DirectionBias ? OrderSide.Buy : OrderSide.Sell;
                _shockDurationActual = _shockDurationTicks;
                _fadeDurationActual = _fadeDurationTicks;
                _phase = Phase.Shock;
                _phaseTicksRemaining = _shockDurationTicks - 1; // current tick counts as one
                if (_phaseTicksRemaining <= 0)
                    EnterFadeOrIdle(rng);
                return EmitBurst(state, fadeFraction: 1.0);

            case Phase.Shock:
                {
                    var burst = EmitBurst(state, fadeFraction: 1.0);
                    _phaseTicksRemaining--;
                    if (_phaseTicksRemaining <= 0)
                        EnterFadeOrIdle(rng);
                    return burst;
                }

            case Phase.Fade:
                {
                    double fraction = _fadeDurationActual <= 0 ? 0
                        : (double)(_phaseTicksRemaining + 1) / _fadeDurationActual;
                    var burst = EmitBurst(state, fadeFraction: fraction);
                    _phaseTicksRemaining--;
                    if (_phaseTicksRemaining < 0)
                        BackToIdle(rng);
                    return burst;
                }
        }
        return Array.Empty<OrderIntent>();
    }

    private void EnterFadeOrIdle(Random rng)
    {
        if (_fadeDurationTicks == 0)
        {
            BackToIdle(rng);
        }
        else
        {
            _phase = Phase.Fade;
            _phaseTicksRemaining = _fadeDurationTicks - 1;
        }
    }

    private int RollIdleTicks(Random rng)
    {
        if (_jitterTicks == 0) return _meanIntervalTicks;
        // Uniform in [mean - jitter, mean + jitter], floored at 1.
        int span = 2 * _jitterTicks + 1;
        int n = _meanIntervalTicks - _jitterTicks + rng.Next(span);
        return Math.Max(1, n);
    }

    private void BackToIdle(Random rng)
    {
        _phase = Phase.Idle;
        _phaseTicksRemaining = RollIdleTicks(rng);
    }

    private IEnumerable<OrderIntent> EmitBurst(in MarketState state, double fadeFraction)
    {
        long tick = state.TickSize <= 0 ? 1 : state.TickSize;
        long lot = state.LotSize <= 0 ? 1 : state.LotSize;
        long fullQty = BurstLots * lot;
        long qty = fadeFraction >= 1.0 ? fullQty : (long)Math.Round(fullQty * fadeFraction);
        if (qty < lot) return Array.Empty<OrderIntent>();
        long offset = (long)LevelsToSweep * tick;
        long px = _shockSide == OrderSide.Buy ? state.MidMantissa + offset : state.MidMantissa - offset;
        if (px <= 0) return Array.Empty<OrderIntent>();
        string sideTag = _shockSide == OrderSide.Buy ? "B" : "S";
        string tag = $"{Name}-{state.SecurityId}-{sideTag}-{++_seq}";
        return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, _shockSide, qty, px, tag) };
    }
}
