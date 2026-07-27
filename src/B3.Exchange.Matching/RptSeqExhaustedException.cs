namespace B3.Exchange.Matching;

/// <summary>
/// Fatal sequencing error raised before the engine would emit the SBE null
/// sentinel (RptSeq=0). The dispatcher terminates the channel so no later
/// command can observe or publish partially-mutated state.
/// </summary>
public sealed class RptSeqExhaustedException : InvalidOperationException
{
    public long SecurityId { get; }

    public RptSeqExhaustedException(long securityId)
        : base($"RptSeq exhausted for securityId {securityId}; channel reset required before more incremental events can be emitted")
    {
        SecurityId = securityId;
    }
}
