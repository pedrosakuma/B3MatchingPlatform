namespace B3.Exchange.Matching;

/// <summary>
/// Issue #322: per-instrument administrative-halt overlay carried by the
/// <see cref="MatchingEngine"/>. Public so the dispatcher / HTTP layer
/// can resolve halt details when reporting outcomes back to operators.
/// </summary>
public readonly record struct HaltState(HaltReason Reason, ulong HaltedAtNanos, string? Note);
