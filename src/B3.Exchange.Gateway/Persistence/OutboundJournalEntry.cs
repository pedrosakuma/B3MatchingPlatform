namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// One record in a per-session FIXP outbound journal: the FIXP
/// <c>MsgSeqNum</c> assigned to a business frame, the wall-clock
/// timestamp (nanoseconds since Unix epoch) at append time, and the
/// full wire frame bytes (SOFH + SBE header + body) exactly as they
/// were enqueued on the transport.
///
/// <para>The timestamp is informational — useful for retention
/// policy (e.g., "drop entries older than 24h") and for diagnostic
/// log correlation. The protocol-relevant identity of an entry is
/// the <see cref="Seq"/>.</para>
/// </summary>
public readonly record struct OutboundJournalEntry(
    uint Seq,
    long TimestampNanos,
    byte[] Frame);
