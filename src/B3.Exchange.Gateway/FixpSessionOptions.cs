namespace B3.Exchange.Gateway;

/// <summary>
/// Session-level timing knobs for heartbeats and idle-timeout teardown.
///
/// The simulator implements a simplified FIXP session layer:
///   - server emits a <c>Sequence</c> frame (templateId=9, used as heartbeat
///     by B3) when no other outbound traffic has been sent within
///     <see cref="HeartbeatIntervalMs"/>;
///   - if no inbound traffic arrives for <see cref="IdleTimeoutMs"/>, the
///     server emits a <c>Sequence</c> probe (the FIXP equivalent of a
///     TestRequest) and starts a grace timer of
///     <see cref="TestRequestGraceMs"/>; if no inbound arrives in that grace
///     window the connection is closed.
///   - <see cref="SuspendedTimeoutMs"/> caps how long a Suspended session
///     (transport dropped, awaiting re-attach in #69b) remains in the
///     listener's session list before being fully closed by the reaper.
///     Set to <c>0</c> to disable the reaper (tests sometimes do this to
///     assert pure suspension behavior without timeout interference).
///
/// Defaults match the issue (#9) spec: 30 s heartbeat, 30 s idle, 5 s grace,
/// 5 min suspended-session reap. Tests override with sub-second values to
/// keep the suite fast.
/// </summary>
public sealed record FixpSessionOptions
{
    public int HeartbeatIntervalMs { get; init; } = 30_000;
    public int IdleTimeoutMs { get; init; } = 30_000;
    public int TestRequestGraceMs { get; init; } = 5_000;
    public int SuspendedTimeoutMs { get; init; } = 5 * 60_000;
    /// <summary>
    /// Per-connection budget (ms) for the listener's first-frame router
    /// (issue #69b-2) to read the SOFH+SBE header + body of the first
    /// FIXP message and decide whether to route the new transport into
    /// an existing Suspended <see cref="FixpSession"/> (re-attach) or to
    /// construct a fresh session. Slowloris connections that fail to
    /// emit a complete first frame within this window are closed
    /// without ever instantiating a <see cref="FixpSession"/>. Default
    /// is 5 s; tests override to sub-second.
    /// </summary>
    public int FirstFrameTimeoutMs { get; init; } = 5_000;

    /// <summary>
    /// Per-session capacity (in frames) of the outbound retransmission
    /// ring buffer that backs FIXP <c>RetransmitRequest</c> recovery
    /// (issue #46, spec §4.5.6). Buffered templates are
    /// ExecutionReport_* and BusinessMessageReject (the templates that
    /// carry an <c>OutboundBusinessHeader.MsgSeqNum</c>). Default 1024
    /// satisfies the spec minimum (≥1000 messages per request) with one
    /// extra slot of headroom.
    /// </summary>
    public int RetransmitBufferCapacity { get; init; } = 1024;

    public static FixpSessionOptions Default { get; } = new();

    internal void Validate()
    {
        if (HeartbeatIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(HeartbeatIntervalMs));
        if (IdleTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(IdleTimeoutMs));
        if (TestRequestGraceMs <= 0) throw new ArgumentOutOfRangeException(nameof(TestRequestGraceMs));
        if (SuspendedTimeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(SuspendedTimeoutMs));
        if (FirstFrameTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(FirstFrameTimeoutMs));
        if (RetransmitBufferCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(RetransmitBufferCapacity));
    }
}
