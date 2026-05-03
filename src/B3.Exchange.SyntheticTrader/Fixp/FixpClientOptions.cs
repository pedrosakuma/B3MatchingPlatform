namespace B3.Exchange.SyntheticTrader.Fixp;

/// <summary>
/// Configuration for the FIXP session layer when the synthetic trader's
/// <c>EntryPointClient</c> talks to a host that has
/// <c>auth.requireFixpHandshake=true</c>. When the caller does not supply
/// these options the client stays in its legacy "raw SBE business frames,
/// no handshake" mode for back-compat with existing soak/tests.
/// </summary>
public sealed record FixpClientOptions
{
    /// <summary>
    /// Decimal uint32 string identifying the FIXP session, as required by
    /// the gateway's <c>FirmRegistry</c> (no leading zeros, &gt; 0).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Wire-format firm code sent in the Negotiate's
    /// <c>enteringFirm</c> field; usually matches <c>tcp.enteringFirm</c>
    /// in the host config.
    /// </summary>
    public required uint EnteringFirm { get; init; }

    /// <summary>
    /// Optional access key sent in the Negotiate credentials varData
    /// alongside the session id and the literal <c>auth_type=basic</c>.
    /// In dev/test mode (host's <c>auth.devMode=true</c>) the gateway
    /// ignores the access key, so the default empty string suffices.
    /// </summary>
    public string AccessKey { get; init; } = "";

    /// <summary>
    /// Idle interval in milliseconds the client advertises in
    /// <c>Establish</c>. The client emits a <c>Sequence</c> heartbeat
    /// after <see cref="KeepAliveIntervalMillis"/>/2 of outbound silence
    /// and disconnects the session if it sees no inbound traffic for
    /// 1.5×<see cref="KeepAliveIntervalMillis"/>.
    /// </summary>
    public uint KeepAliveIntervalMillis { get; init; } = 5000;

    /// <summary>
    /// When true, the Establish carries
    /// <c>cancelOnDisconnectType=4</c> (cancel on disconnect &amp; on
    /// terminate); otherwise <c>0</c> (do not cancel). The corresponding
    /// timeout window is 0 (immediate) per spec when CoD is requested.
    /// </summary>
    public bool CancelOnDisconnect { get; init; } = false;

    /// <summary>
    /// When true, the client sends a <c>RetransmitRequest</c> on detected
    /// inbound application sequence gaps. When false, gaps are logged and
    /// the missing messages are skipped.
    /// </summary>
    public bool RetransmitOnGap { get; init; } = true;

    /// <summary>
    /// Timeout for the Negotiate→NegotiateResponse and
    /// Establish→EstablishAck synchronous handshake legs.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional "client app name" varData segment (≤30 bytes). Defaults
    /// to "synth".
    /// </summary>
    public string ClientAppName { get; init; } = "synth";

    /// <summary>
    /// Optional "client app version" varData segment (≤30 bytes).
    /// </summary>
    public string ClientAppVersion { get; init; } = "1.0";
}
