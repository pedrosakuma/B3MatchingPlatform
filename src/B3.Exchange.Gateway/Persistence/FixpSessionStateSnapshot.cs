namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// Issue #405: durable snapshot of the FIXP envelope state owned by
/// a single <see cref="FixpSession"/>. Captured on every meaningful
/// transition (Establish/Terminate/keepalive ack/business-frame
/// dispatch) and reloaded at boot so a reconnecting peer sees a
/// session-version monotonically greater than any it has seen
/// before, with <c>LastIncomingSeqNo</c> reflecting the last business
/// frame the matching-platform actually applied — matching the
/// recoverable-server-flow contract of B3 Binary EntryPoint SBE 5.2
/// §1.5.
///
/// <para>The snapshot intentionally does <b>not</b> include the
/// retransmit ring — that lives in <see cref="IFixpOutboundJournal"/>,
/// the unbounded source-of-truth. Persisting both here would
/// duplicate state and create a tear window when the two updates
/// race.</para>
/// </summary>
/// <param name="SessionId">FIXP wire SessionID (uint32) — also the
/// persistence key.</param>
/// <param name="SessionVerId">SessionVerID negotiated on the latest
/// successful Establish. Restoring this at boot is what guarantees
/// the spec-mandated intraweek monotonicity:
/// "duplicate SessionVerID's intraweek ... can affect subsequent
/// retransmission" (SBE 5.2 lines 522/624). The seed for the next
/// Establish is <c>SessionVerId + 1</c>.</param>
/// <param name="OutboundMsgSeqNum">Last outbound business
/// <c>MsgSeqNum</c> the encoder assigned on this session. The
/// post-boot allocator resumes at <c>OutboundMsgSeqNum + 1</c>. Held
/// in sync with the journal's <c>MaxSeq</c> at append time; the
/// boot path reconciles the two and picks <c>max(snapshot, journal)</c>
/// as the authoritative resume point.</param>
/// <param name="LastIncomingSeqNo">Last inbound business
/// <c>MsgSeqNum</c> the matching-engine has confirmed applied.
/// Sent in <c>EstablishmentAck.lastIncomingSeqNo</c> so the
/// reconnecting peer knows where to resume its outbound retransmit
/// (SBE 5.2 line 663).</param>
/// <param name="EnteringFirm">B3 EnteringFirm code resolved during
/// Negotiate; persisted so a boot-time rehydrated session can route
/// commands to the correct firm without re-running
/// <c>FirmRegistry</c> lookup.</param>
/// <param name="UpdatedAtNanos">Nanoseconds since Unix epoch when
/// the snapshot was written. Diagnostic only; not used for ordering.</param>
public readonly record struct FixpSessionStateSnapshot(
    uint SessionId,
    ulong SessionVerId,
    uint OutboundMsgSeqNum,
    uint LastIncomingSeqNo,
    uint EnteringFirm,
    long UpdatedAtNanos);
