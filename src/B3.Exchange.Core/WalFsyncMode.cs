namespace B3.Exchange.Core;

/// <summary>
/// Issue #312 (Tier-2 perf review): controls when
/// <see cref="IChannelWriteAheadLog.Append"/> calls <c>fsync</c>
/// on the underlying file.
///
/// <para><see cref="PerWrite"/> (default) fsyncs inside every
/// <see cref="IChannelWriteAheadLog.Append"/> call, giving zero
/// RPO at the cost of one disk-flush per command. This was the
/// pre-#312 behaviour and remains the default so existing
/// deployments see no behavioural change.</para>
///
/// <para><see cref="GroupCommit"/> defers <c>fsync</c> to a
/// background thread that batches multiple appended records into
/// a single flush. <see cref="IChannelWriteAheadLog.Append"/>
/// returns as soon as the bytes have hit the OS buffer; callers
/// that need durability before publishing externally observable
/// effects (e.g. an ExecutionReport on the wire) MUST call
/// <see cref="IChannelWriteAheadLog.WaitForDurable(long, System.Threading.CancellationToken)"/>
/// with the record's <c>Seq</c> before doing so. This trades a
/// small bounded RPO (the inter-flush interval) for a large
/// throughput win under load: while one batch is being fsynced,
/// the dispatch thread can keep appending the next batch.</para>
///
/// <para>The <c>fsync</c>-batching logic itself is implementation
/// defined; <see cref="Persistence.FileChannelWriteAheadLog"/>
/// uses a signalled background thread with a fixed maximum
/// inter-flush interval. The <see cref="GroupCommit"/> mode is
/// safe under abrupt termination: records that were appended but
/// not yet fsynced are simply absent from the post-restart WAL,
/// and the dispatcher's replay sees the same state it would have
/// seen had those commands never reached the host.</para>
/// </summary>
public enum WalFsyncMode
{
    /// <summary>Synchronous fsync inside every <c>Append</c> call (default; pre-#312 behaviour).</summary>
    PerWrite = 0,

    /// <summary>Batched fsync on a background thread; callers that need durability gating must call <c>WaitForDurable</c>.</summary>
    GroupCommit = 1,
}
