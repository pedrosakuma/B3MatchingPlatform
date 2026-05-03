namespace B3.EntryPoint.Wire;

/// <summary>
/// Decoded fixed-block fields of a FIXP <c>Negotiate</c> message (V6
/// schema, MESSAGE_ID = 1, BLOCK_LENGTH = 28). The credentials varData
/// segment is parsed separately into <see cref="NegotiateCredentials"/>.
/// </summary>
public readonly record struct NegotiateRequest(
    uint SessionId,
    ulong SessionVerId,
    ulong TimestampNanos,
    uint EnteringFirm,
    uint? OnBehalfFirm);
