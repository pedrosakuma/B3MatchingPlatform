namespace B3.Exchange.Core;

/// <summary>
/// Issue #291: thrown by
/// <see cref="IChannelWriteAheadLog.Append"/> when the configured
/// <c>maxBytes</c> cap would be exceeded by the incoming record
/// AND the resolved <see cref="WalSizeCapPolicy"/> is
/// <see cref="WalSizeCapPolicy.Halt"/>. The dispatcher catches
/// this exception specifically (before its
/// <see cref="WalAppendFailurePolicy"/> dispatch) and always
/// marks the channel WAL-halted — a cap breach is a
/// configuration / capacity bug, not a transient IO fault, so we
/// never silently degrade to the Continue path.
/// </summary>
public sealed class WalSizeCapExceededException : System.IO.IOException
{
    /// <summary>Current on-disk size in bytes.</summary>
    public long CurrentSizeBytes { get; }

    /// <summary>Configured cap in bytes.</summary>
    public long MaxBytes { get; }

    /// <summary>Size of the record that would have pushed past the cap.</summary>
    public int IncomingRecordBytes { get; }

    public WalSizeCapExceededException(long currentSizeBytes, long maxBytes, int incomingRecordBytes)
        : base($"WAL size cap exceeded: current={currentSizeBytes}B + incoming={incomingRecordBytes}B > maxBytes={maxBytes}B")
    {
        CurrentSizeBytes = currentSizeBytes;
        MaxBytes = maxBytes;
        IncomingRecordBytes = incomingRecordBytes;
    }
}
