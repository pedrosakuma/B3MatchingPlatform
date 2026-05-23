namespace B3.Exchange.Gateway;

/// <summary>
/// Issue #405: classifies why a <see cref="FixpSession"/> is being
/// closed, so the close path can decide whether to delete persisted
/// state (envelope snapshot + outbound journal) or preserve it for
/// post-restart resync.
///
/// <para>The FIXP server-flow is declared <c>recoverable</c> by
/// B3 Binary EntryPoint SBE 5.2 §1.5, which obliges the gateway to
/// be able to retransmit any business frame the peer missed across
/// arbitrary disconnect durations. Deleting per-session persistence
/// on every close (today's behavior, pre-#405) silently violates
/// that contract whenever the close was caused by transport loss
/// or by a graceful host shutdown — situations where the peer is
/// expected to come back and resync.</para>
///
/// <para>This commit only introduces the enum and threads it
/// through every <c>Close*</c> call site; the actual persistence
/// delete-vs-keep decision is wired in a later commit so the
/// behavioral change is isolated and easy to review.</para>
/// </summary>
public enum CloseKind
{
    /// <summary>
    /// Peer-initiated graceful logout (received <c>Terminate(Finished)</c>
    /// or local code chose to terminate the session for protocol /
    /// auth reasons). After this close the peer is not expected to
    /// resume the same session; persisted state should be removed.
    /// Default for legacy <c>Close(string)</c> call sites to preserve
    /// pre-#405 behavior until the journal wire-up commit.
    /// </summary>
    PeerTerminate,

    /// <summary>
    /// Host process is shutting down gracefully (SIGTERM,
    /// daily-rollover, etc.). The peer is expected to reconnect when
    /// the host comes back; persisted state must be preserved so the
    /// resumed session can replay every event produced before the
    /// shutdown.
    /// </summary>
    HostShutdown,

    /// <summary>
    /// Underlying TCP transport dropped (read/write error, peer RST,
    /// idle timeout while still Established). The peer is expected
    /// to reconnect via Establish; persisted state must be preserved
    /// so the reconnect can resync from where the disconnect happened.
    /// </summary>
    TransportError,

    /// <summary>
    /// Suspended-state reaper aged the session out after the
    /// configured Suspended-window expired without re-attach. The
    /// session is considered abandoned; persisted state should be
    /// removed.
    /// </summary>
    SuspendedTimeout,
}
