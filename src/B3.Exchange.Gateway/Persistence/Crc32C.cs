namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// Castagnoli Crc32C (polynomial <c>0x1EDC6F41</c>, reflected
/// <c>0x82F63B78</c>) used to detect bit-rot in the per-session
/// retransmit ring file (issue #289). Mirrors
/// <c>B3.Exchange.Persistence.Crc32C</c> verbatim — duplicated here
/// to avoid having Gateway take a project reference on Persistence
/// purely for one 30-line static class. Both copies are
/// software-only (no <c>System.IO.Hashing</c> dependency); a future
/// consolidation would extract this to a shared low-level package.
/// </summary>
internal static class Crc32C
{
    private const uint ReflectedPolynomial = 0x82F63B78u;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1u) != 0 ? (c >> 1) ^ ReflectedPolynomial : c >> 1;
            }
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < bytes.Length; i++)
        {
            crc = (crc >> 8) ^ Table[(crc ^ bytes[i]) & 0xFF];
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
