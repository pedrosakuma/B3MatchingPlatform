namespace B3.Exchange.Core;

/// <summary>
/// Configures how aggressively <see cref="ChannelDispatcher"/> persists
/// channel snapshots after each flushed command (issue #267). The default
/// (<see cref="AlwaysPersist"/>) preserves the PR #261 behaviour — every
/// command flush triggers an immediate snapshot — and is appropriate for
/// low-throughput operator deployments where RPO must be zero. Higher-
/// throughput channels can throttle snapshots by command count, time
/// since last successful persist, or both (whichever fires first).
///
/// <para>Operator commands (BumpVersion / TradeBust / SetTradingPhase)
/// always persist immediately regardless of policy — they are
/// low-frequency, high-consequence events whose effect on
/// <c>SequenceVersion</c>/<c>RptSeq</c>/trading phase must survive a
/// crash even if the throttle window has not elapsed.</para>
///
/// <para>The dispatcher tracks a <c>_pendingDirty</c> flag whenever it
/// skips a persist; on cooperative shutdown the loop forces a final
/// snapshot so a quiet period does not silently lose the most recent
/// commands.</para>
/// </summary>
public sealed record SnapshotThrottlePolicy
{
    /// <summary>
    /// Persist after <em>every</em> command (default; matches PR #261
    /// behaviour). Equivalent to <c>EveryNCommands = 1</c>,
    /// <c>MinIntervalMs = 0</c>.
    /// </summary>
    public static readonly SnapshotThrottlePolicy AlwaysPersist =
        new() { EveryNCommands = 1, MinIntervalMs = 0 };

    /// <summary>
    /// Persist after this many non-operator commands have been flushed
    /// since the last successful persist. <c>0</c> disables the
    /// command-count trigger entirely (rely solely on
    /// <see cref="MinIntervalMs"/>); <c>1</c> persists every command.
    /// </summary>
    public int EveryNCommands { get; init; } = 1;

    /// <summary>
    /// Persist when the wall-clock time since the last successful persist
    /// reaches this many milliseconds. <c>0</c> disables the time
    /// trigger entirely (rely solely on <see cref="EveryNCommands"/>).
    /// Evaluated at command-flush boundaries — does not require its own
    /// timer.
    /// </summary>
    public int MinIntervalMs { get; init; } = 0;

    /// <summary>
    /// Returns <c>true</c> if a non-operator flush should persist now
    /// given the per-channel counters, or <c>false</c> if the snapshot
    /// should be skipped (and the dispatcher should mark itself dirty so
    /// shutdown forces a final flush).
    /// </summary>
    public bool ShouldPersist(long commandsSinceLastSave, long msSinceLastSave)
    {
        if (EveryNCommands > 0 && commandsSinceLastSave >= EveryNCommands) return true;
        if (MinIntervalMs > 0 && msSinceLastSave >= MinIntervalMs) return true;
        // Defensive: both knobs disabled would mean "never persist on
        // the command path"; treat that as the legacy behaviour rather
        // than silently breaking durability.
        if (EveryNCommands <= 0 && MinIntervalMs <= 0) return true;
        return false;
    }
}
