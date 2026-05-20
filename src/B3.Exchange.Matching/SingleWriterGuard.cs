namespace B3.Exchange.Matching.Threading;

/// <summary>
/// Shared latch + assert helper for components that follow ADR 0009's
/// single-writer threading model. Replaces the hand-rolled
/// <c>_ownerThread</c> / <c>_loopThread</c> + per-class
/// <c>Assert*</c> methods previously duplicated across
/// <c>MatchingEngine</c> and <c>ChannelDispatcher</c>.
///
/// <para>
/// Usage:
/// <list type="bullet">
/// <item>
/// Owning component holds a single instance as a private readonly field
/// and calls <see cref="AssertOwnedByCurrentThread"/> as the first
/// statement of every mutation/read entry point.
/// </item>
/// <item>
/// Components whose owner thread is known up front (e.g. the dispatcher's
/// <c>RunLoop</c>) call <see cref="BindToCurrentThread"/> once on entry
/// so the very first assert is meaningful rather than self-latching.
/// </item>
/// <item>
/// Direct-drive unit tests (e.g. <c>TestProbe.DrainInbound</c>) skip
/// the explicit bind; the first assert lazy-latches to the calling
/// thread and every subsequent call must come from the same thread.
/// </item>
/// </list>
/// </para>
///
/// <para>
/// Both <see cref="BindToCurrentThread"/> and
/// <see cref="AssertOwnedByCurrentThread"/> are compiled out in Release
/// builds. This matches the prior hot-path cost contract of the
/// hand-rolled asserts. Components that need to read the owner outside
/// of an assert (e.g. for logging) must do so via their own field, not
/// through this helper.
/// </para>
///
/// <para>
/// Snapshot-replay flows that need to re-bind the owner thread on a
/// different thread call <see cref="RebindForReplay"/> to clear the
/// latch. This is the only path that legitimately re-binds; if you
/// reach for it from anywhere else you are almost certainly violating
/// ADR 0009.
/// </para>
///
/// Issue #384 (architectural review 2026-05-20, refactor #7).
/// </summary>
public sealed class SingleWriterGuard
{
    private readonly string _ownerLabel;
    private Thread? _owner;

    /// <param name="ownerLabel">
    /// Human-readable label included in assertion failure messages
    /// (e.g. <c>"MatchingEngine"</c> or
    /// <c>"ChannelDispatcher[channel=82]"</c>).
    /// </param>
    public SingleWriterGuard(string ownerLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerLabel);
        _ownerLabel = ownerLabel;
    }

    /// <summary>
    /// Bind to <see cref="Thread.CurrentThread"/>. Idempotent when the
    /// same thread binds twice; fires a <see cref="System.Diagnostics.Debug.Assert(bool, string)"/>
    /// in Debug builds if a different thread is already bound. Compiled
    /// out in Release.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public void BindToCurrentThread()
    {
        var current = Thread.CurrentThread;
        var prior = Interlocked.CompareExchange(ref _owner, current, null);
        System.Diagnostics.Debug.Assert(
            prior == null || prior == current,
            $"{_ownerLabel} already bound to a different thread "
            + $"(existing={prior?.ManagedThreadId}, "
            + $"new={current.ManagedThreadId})");
    }

    /// <summary>
    /// Assert the calling thread owns the guard. Lazy-latches to the
    /// current thread if unbound (so direct-drive tests stay coherent
    /// across their lifetime). Compiled out in Release.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public void AssertOwnedByCurrentThread()
    {
        var current = Thread.CurrentThread;
        var owner = Interlocked.CompareExchange(ref _owner, current, null);
        System.Diagnostics.Debug.Assert(
            owner == null || owner == current,
            $"{_ownerLabel} entered off the owner thread "
            + $"(owner={owner?.ManagedThreadId}, "
            + $"actual={current.ManagedThreadId})");
    }

    /// <summary>
    /// Clear the owner latch so a fresh thread can claim ownership.
    /// Intended exclusively for snapshot-replay paths where state is
    /// rebuilt on a different thread before the live dispatch loop
    /// picks it up. Any other use is almost certainly an ADR 0009
    /// violation.
    /// </summary>
    public void RebindForReplay() => Volatile.Write(ref _owner, null);
}
