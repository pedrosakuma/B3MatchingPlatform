using System.Runtime.InteropServices;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.EntryPoint.Wire;

/// <summary>
/// Byte-level decoder for the FIXP <c>Negotiate</c> message
/// (templateId=1, BLOCK_LENGTH=28). Splits the buffer into the fixed
/// root block (returned as <see cref="NegotiateRequest"/>) and the four
/// length-prefixed varData segments
/// (<c>credentials</c>, <c>clientIP</c>, <c>clientAppName</c>, <c>clientAppVersion</c>).
///
/// <para>Validation of the JSON payload of <c>credentials</c> is the
/// responsibility of <see cref="NegotiateCredentials.TryParse"/>; this
/// decoder only verifies the SBE wire shape.</para>
/// </summary>
public static class NegotiateDecoder
{
    /// <summary>FIXP Negotiate template id.</summary>
    public const ushort TemplateId = FixpSbe.NegotiateData.MESSAGE_ID;
    public const int BlockLength = FixpSbe.NegotiateData.BLOCK_LENGTH;

    /// <summary>Per-spec maximum varData segment lengths (matches the
    /// SBE schema).</summary>
    public const int MaxCredentialsLength = 128;
    public const int MaxClientIpLength = 30;
    public const int MaxClientAppNameLength = 30;
    public const int MaxClientAppVersionLength = 30;

    public static bool TryDecode(
        ReadOnlySpan<byte> fixedBlock,
        ReadOnlySpan<byte> varData,
        out NegotiateRequest request,
        out ReadOnlySpan<byte> credentials,
        out string? error)
    {
        request = default;
        credentials = default;
        error = null;

        if (fixedBlock.Length < BlockLength)
        {
            error = $"Negotiate: block length {fixedBlock.Length} < {BlockLength}";
            return false;
        }

        ref readonly var data = ref MemoryMarshal.AsRef<FixpSbe.NegotiateData>(fixedBlock);
        request = new NegotiateRequest(
            SessionId: (uint)data.SessionID,
            SessionVerId: (ulong)data.SessionVerID,
            TimestampNanos: data.Timestamp.Time,
            EnteringFirm: (uint)data.EnteringFirm,
            OnBehalfFirm: data.OnbehalfFirm);

        // varData segments: each is length(uint8) + bytes. Per spec the
        // four segments appear in a fixed order; trailing segments may
        // be omitted but if present must obey the maximum lengths.
        // Local copy so we can pass ref-into ref-struct helpers.
        var remaining = varData;
        if (!TryReadSegment(ref remaining, MaxCredentialsLength, "credentials", out credentials, out error))
            return false;
        if (!TryReadSegment(ref remaining, MaxClientIpLength, "clientIP", out _, out error))
            return false;
        if (!TryReadSegment(ref remaining, MaxClientAppNameLength, "clientAppName", out _, out error))
            return false;
        if (!TryReadSegment(ref remaining, MaxClientAppVersionLength, "clientAppVersion", out _, out error))
            return false;
        if (remaining.Length != 0)
        {
            error = $"Negotiate: {remaining.Length} unexpected trailing varData byte(s)";
            return false;
        }
        return true;
    }

    private static bool TryReadSegment(scoped ref ReadOnlySpan<byte> remaining, int maxLength,
        string fieldName, out ReadOnlySpan<byte> segment, out string? error)
    {
        segment = default;
        error = null;
        if (remaining.Length == 0) return true; // segment omitted (allowed)
        byte len = remaining[0];
        if (len > maxLength)
        {
            error = $"Negotiate: {fieldName} length {len} exceeds max {maxLength}";
            return false;
        }
        if (remaining.Length < 1 + len)
        {
            error = $"Negotiate: {fieldName} truncated (declared {len}, available {remaining.Length - 1})";
            return false;
        }
        segment = remaining.Slice(1, len);
        remaining = remaining.Slice(1 + len);
        return true;
    }
}
