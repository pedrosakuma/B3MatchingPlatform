using B3.Umdf.Mbo.Sbe.V16;

namespace B3.Umdf.WireEncoder;

/// <summary>
/// Maps the FIX-style <c>securityType</c> string carried by configured
/// instruments (e.g. "CS", "OPT", "FUT") to the SBE <see cref="SecurityType"/>
/// uint8 enum value emitted in <c>SecurityDefinition_12</c> frames.
///
/// Lookup is case-insensitive. A small set of legacy aliases is honoured so
/// existing instrument files (and ad-hoc tests) that use names like
/// <c>"EQUITY"</c>, <c>"FUTURE"</c> or <c>"OPTION"</c> map to the closest
/// schema enum value. Unknown strings map to <c>0</c> — the SBE NULL
/// sentinel for this type — so consumers see "unspecified" rather than a
/// silent mis-mapping.
/// </summary>
public static class SecurityTypeMap
{
    /// <summary>SBE NULL sentinel for the SecurityType uint8 enum.</summary>
    public const byte Unspecified = 0;

    /// <summary>
    /// Returns the SBE enum byte for <paramref name="securityType"/>, or
    /// <see cref="Unspecified"/> when the string is null/empty/unknown.
    /// </summary>
    public static byte ToSbeByte(string? securityType)
    {
        if (string.IsNullOrWhiteSpace(securityType)) return Unspecified;

        // Direct enum names first.
        if (Enum.TryParse<SecurityType>(securityType, ignoreCase: true, out var direct))
            return (byte)direct;

        // Common legacy aliases used in this repo's instrument files / tests.
        return securityType.ToUpperInvariant() switch
        {
            "EQUITY" => (byte)SecurityType.CS,
            "STOCK" => (byte)SecurityType.CS,
            "PREFERRED" => (byte)SecurityType.PS,
            "FUTURE" => (byte)SecurityType.FUT,
            "OPTION" => (byte)SecurityType.OPT,
            "INDEXOPTION" => (byte)SecurityType.INDEXOPT,
            _ => Unspecified,
        };
    }
}
