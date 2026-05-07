namespace B3.Exchange.Core;

/// <summary>
/// Policy for what the dispatcher does when the per-channel
/// Write-Ahead Log refuses an <c>Append</c> call (disk full, EIO,
/// permission flip — anything that throws out of
/// <see cref="IChannelWriteAheadLog.Append"/>). Issue #286.
///
/// <para>The default <see cref="Continue"/> matches the pre-#286
/// behaviour: log the failure, bump
/// <c>exch_wal_append_failures_total</c>, and let the command
/// execute against the engine. This favours availability — the
/// live consumer view never sees a gap — at the cost of a window
/// where the command is not durable: a host crash before the next
/// successful snapshot will silently lose it on replay.</para>
///
/// <para><see cref="Halt"/> trades availability for durability: on
/// the first failure the channel is marked WAL-halted, the
/// dispatch loop refuses to execute the offending command (no
/// engine mutation, no UMDF emission, no ExecutionReport), and the
/// per-host readiness probe flips to NOT_READY so load balancers
/// stop routing new connections. Subsequent
/// <c>Enqueue*</c> calls are rejected at the producer side. The
/// halt is sticky and clears only on host restart, after the
/// operator has fixed the underlying disk fault.</para>
/// </summary>
public enum WalAppendFailurePolicy
{
    /// <summary>Log + metric, then run the command anyway (default).</summary>
    Continue = 0,

    /// <summary>Log + metric, refuse the command, mark the channel unhealthy.</summary>
    Halt = 1,
}
