namespace B3.Exchange.Contracts;

/// <summary>
/// Issue #312 (Tier-2 perf): minimal durability surface that the
/// Gateway-side outbound stack consults before writing externally
/// observable bytes (e.g. an ExecutionReport on the wire) to the
/// peer. Implemented by the per-channel Write-Ahead Log; passed
/// down to the encoder + transport via <see cref="DurabilityHandle"/>
/// on every <see cref="ICoreOutbound"/> call so that the WAL's
/// background fsync (under
/// <c>WalFsyncMode.GroupCommit</c>) can batch multiple appends
/// without ever exposing not-yet-durable state to the client.
///
/// <para>The default in-memory / no-WAL paths supply
/// <see cref="DurabilityHandle.None"/> — the transport then writes
/// the frame immediately, preserving the pre-#312 behaviour.</para>
/// </summary>
public interface IDurabilityBarrier
{
    /// <summary>
    /// Blocks the caller until every record up to and including
    /// <paramref name="seq"/> is on durable storage (returns
    /// immediately when the predicate already holds or
    /// <paramref name="seq"/> is &lt;= 0).
    ///
    /// <para>Implementations MUST honour
    /// <paramref name="cancellationToken"/> so the caller (typically
    /// the TCP send loop) can abort on shutdown without leaking the
    /// <para>The default implementation is a no-op (the in-memory
    /// fake reports its writes as immediately durable, matching the
    /// pre-#312 behaviour of the WAL interface).</para>
    /// </summary>
    void WaitForDurable(long seq, CancellationToken cancellationToken = default) { }
}

/// <summary>
/// Pair of <see cref="IDurabilityBarrier"/> reference and the
/// <see cref="WalRecord.Seq"/> the calling outbound write depends
/// on. <see cref="None"/> (the default value) means "no durability
/// gating" — the transport writes the frame immediately. This is
/// the path taken by every call site that does not have a WAL
/// behind it (in-memory tests, channels with WAL disabled, and
/// frames produced outside the dispatch loop such as session
/// keep-alives).
///
/// <para>Carried as a single value type rather than two
/// parameters so future surface widening (e.g. propagating a
/// snapshot durability watermark too) needs only one signature
/// change. The struct is small enough (one reference + one long)
/// to be passed by value without overhead.</para>
/// </summary>
public readonly record struct DurabilityHandle(IDurabilityBarrier? Barrier, long Seq)
{
    /// <summary>The "no-op" handle: <c>WriteXxx</c> implementations
    /// MUST treat this as "send the frame immediately, no
    /// durability wait".</summary>
    public static DurabilityHandle None => default;

    /// <summary><c>true</c> when the handle carries a real barrier
    /// reference and a positive seq — i.e. the transport SHOULD
    /// call <see cref="IDurabilityBarrier.WaitForDurable"/> before
    /// writing the frame. <c>false</c> for <see cref="None"/> (in
    /// which case the wait is skipped).</summary>
    public bool IsActive => Barrier is not null && Seq > 0;
}
