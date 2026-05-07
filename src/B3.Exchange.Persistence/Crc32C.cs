namespace B3.Exchange.Persistence;

/// <summary>
/// Software Crc32C (Castagnoli, polynomial <c>0x1EDC6F41</c> in
/// reflected form <c>0x82F63B78</c>) used by the persistence stack
/// to detect bit-rot in WAL records and binary snapshot files
/// (issue #285).
///
/// <para>Software-only implementation deliberately avoids adding a
/// new NuGet dependency (<c>System.IO.Hashing</c> exposes vanilla
/// <c>Crc32</c> but not Crc32C). Throughput is bounded by the
/// 256-entry table lookup; on a modern x64 core this clears
/// gigabytes per second per thread, well above the WAL append rate
/// the simulator targets. Hardware-accelerated CRC32C intrinsics
/// (<c>System.Runtime.Intrinsics.X86.Sse42</c>,
/// <c>System.Runtime.Intrinsics.Arm.Crc32</c>) could be wired in
/// later if profiling shows hashing on the hot path; the
/// public surface is identical so a swap is transparent.</para>
///
/// <para>Castagnoli polynomial chosen over the IEEE/zlib variant
/// for its better collision properties on short payloads (it is the
/// CRC used by SCTP, iSCSI, and modern filesystems for the same
/// "detect storage bit-rot" job we have here).</para>
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

    /// <summary>
    /// Computes the Crc32C of <paramref name="bytes"/>.
    /// </summary>
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
