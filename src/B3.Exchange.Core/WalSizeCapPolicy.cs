namespace B3.Exchange.Core;

/// <summary>
/// Issue #291: policy applied by
/// <see cref="IChannelWriteAheadLog.Append"/> when accepting the
/// next record would push the on-disk WAL size past its
/// configured <c>maxBytes</c> cap.
///
/// <para>The cap exists to bound disk consumption when snapshots
/// stop succeeding (disk-full elsewhere, persister bug,
/// permission flip): without it the WAL grows until the
/// filesystem is full, at which point unrelated subsystems also
/// start failing. The cap turns "WAL eats the disk" into a loud,
/// channel-scoped failure that the operator can react to.</para>
///
/// <para><see cref="Halt"/> (default) refuses the append by
/// throwing — the dispatcher catches the cap-specific exception
/// and marks the channel WAL-halted regardless of
/// <see cref="WalAppendFailurePolicy"/>, because a cap breach is
/// a configuration / capacity bug rather than a transient IO
/// fault and we never want to silently degrade durability when
/// the operator has explicitly opted into a hard cap.</para>
///
/// <para><see cref="Drop"/> silently skips the append, logs at
/// <c>Warning</c>, and bumps <c>exch_wal_drops_on_full_total</c>.
/// Same trade-off as <see cref="WalAppendFailurePolicy.Continue"/>
/// on append failure but explicit: the engine still mutates so
/// the live consumer view stays consistent, but the command is
/// not durable — a host crash before the next successful
/// snapshot will silently lose it on replay.</para>
/// </summary>
public enum WalSizeCapPolicy
{
    /// <summary>Throw, refuse the command, mark the channel unhealthy (default).</summary>
    Halt = 0,

    /// <summary>Log + metric, skip the WAL write, let the command run anyway.</summary>
    Drop = 1,
}
