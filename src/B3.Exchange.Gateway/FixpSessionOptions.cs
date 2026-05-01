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
///
/// Defaults match the issue (#9) spec: 30 s heartbeat, 30 s idle, 5 s grace.
/// Tests override with sub-second values to keep the suite fast.
/// </summary>
public sealed record FixpSessionOptions
{
    public int HeartbeatIntervalMs { get; init; } = 30_000;
    public int IdleTimeoutMs { get; init; } = 30_000;
    public int TestRequestGraceMs { get; init; } = 5_000;

    public static FixpSessionOptions Default { get; } = new();

    internal void Validate()
    {
        if (HeartbeatIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(HeartbeatIntervalMs));
        if (IdleTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(IdleTimeoutMs));
        if (TestRequestGraceMs <= 0) throw new ArgumentOutOfRangeException(nameof(TestRequestGraceMs));
    }
}
