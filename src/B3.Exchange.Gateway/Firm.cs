namespace B3.Exchange.Gateway;

/// <summary>
/// Static identity for a participant (corretora / brokerage). Loaded once
/// at startup from <c>HostConfig.firms[]</c>; immutable for the life of the
/// process. See <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §4.1.
/// </summary>
/// <param name="Id">Stable string identifier (e.g. <c>"FIRM01"</c>).</param>
/// <param name="Name">Human-readable name; used for diagnostics only.</param>
/// <param name="EnteringFirmCode">
/// Numeric code stamped on outbound SBE headers as <c>EnteringFirm</c>. Must
/// match what the upstream B3 systems expect for this corretora.
/// </param>
public sealed record Firm(string Id, string Name, uint EnteringFirmCode);

/// <summary>
/// Per-session policy knobs. Loaded with the parent
/// <see cref="SessionCredential"/>; immutable. Defaults match today's
/// global TCP defaults for backwards-compatibility.
/// </summary>
public sealed record SessionPolicy(
    int ThrottleMessagesPerSecond = 0,
    int KeepAliveIntervalMs = 30_000,
    int IdleTimeoutMs = 30_000,
    int TestRequestGraceMs = 5_000,
    int RetransmitBufferSize = 10_000)
{
    public static SessionPolicy Default { get; } = new();

    public void Validate()
    {
        if (KeepAliveIntervalMs <= 0)
            throw new InvalidOperationException("SessionPolicy.KeepAliveIntervalMs must be > 0");
        if (IdleTimeoutMs <= 0)
            throw new InvalidOperationException("SessionPolicy.IdleTimeoutMs must be > 0");
        if (TestRequestGraceMs <= 0)
            throw new InvalidOperationException("SessionPolicy.TestRequestGraceMs must be > 0");
        if (RetransmitBufferSize <= 0)
            throw new InvalidOperationException("SessionPolicy.RetransmitBufferSize must be > 0");
        if (ThrottleMessagesPerSecond < 0)
            throw new InvalidOperationException("SessionPolicy.ThrottleMessagesPerSecond must be >= 0 (0 disables)");
    }
}

/// <summary>
/// Per-session credential record. One per <c>(firmId, sessionId)</c> pair.
/// Loaded once at startup from <c>HostConfig.sessions[]</c>; immutable.
/// See <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §4.2.
///
/// <para>Phase 2 #67 introduces this type as part of the multi-firm /
/// multi-session model. The <c>FixpSession</c> Negotiate handler (#42)
/// resolves a credential by matching <c>Negotiate.sessionID</c> against
/// <see cref="SessionId"/> here and validating <c>credentials.access_key</c>
/// against <see cref="AccessKey"/> (when <c>auth.devMode</c> is false).</para>
/// </summary>
/// <param name="SessionId">Stable string identifier matching FIXP <c>sessionID</c>.</param>
/// <param name="FirmId">Owning firm — must resolve via <see cref="FirmRegistry"/>.</param>
/// <param name="AccessKey">
/// Plain shared secret (dev). Production hashing is deferred per
/// architecture §7. Empty string disables credential validation for this
/// session even outside dev mode (use only for tests).
/// </param>
/// <param name="AllowedSourceCidrs">
/// Optional whitelist of CIDR ranges. <c>null</c> or empty disables
/// source-IP filtering. Future enforcement; today informational only.
/// </param>
/// <param name="Policy">Per-session throttle / keep-alive overrides.</param>
public sealed record SessionCredential(
    string SessionId,
    string FirmId,
    string AccessKey,
    IReadOnlyList<string>? AllowedSourceCidrs,
    SessionPolicy Policy);
