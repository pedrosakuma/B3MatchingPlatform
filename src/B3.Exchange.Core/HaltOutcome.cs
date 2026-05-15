using B3.Exchange.Matching;

namespace B3.Exchange.Core;

/// <summary>
/// Issue #322: structured outcome of an operator-driven halt or resume
/// command (<see cref="ChannelDispatcher.EnqueueOperatorHalt"/> /
/// <see cref="ChannelDispatcher.EnqueueOperatorResume"/>) returned to
/// the HTTP admin endpoint via a
/// <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>.
/// </summary>
public readonly record struct HaltOutcome(
    bool StateChanged,
    bool IsHaltedNow,
    HaltReason? Reason,
    ulong HaltedAtNanos,
    string? Note);

/// <summary>
/// Issue #322: cross-thread snapshot of the dispatcher's view of the
/// per-instrument halt overlay. Mirrors the engine's <c>HaltState</c>
/// but lives in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// so callers off the dispatch thread (HTTP admin handler) can resolve
/// the current halt state without piercing the engine.
/// </summary>
public readonly record struct HaltSnapshot(
    HaltReason Reason,
    ulong HaltedAtNanos,
    string? Note);
