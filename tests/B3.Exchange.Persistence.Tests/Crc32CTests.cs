using B3.Exchange.Persistence;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #285: smoke tests for the software Crc32C implementation
/// (Castagnoli polynomial). Test vectors taken from RFC 3720
/// (iSCSI) Appendix B.4.
/// </summary>
public class Crc32CTests
{
    [Fact]
    public void EmptyInput_HasZeroCrc()
    {
        // Initial 0xFFFFFFFF cancels with the final XOR for an empty
        // buffer, yielding 0x00000000.
        Assert.Equal(0x00000000u, Crc32C.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RFC3720_AllZeros32_Yields_8A9136AA()
    {
        var input = new byte[32];
        Assert.Equal(0x8A9136AAu, Crc32C.Compute(input));
    }

    [Fact]
    public void RFC3720_AllOnes32_Yields_62A8AB43()
    {
        var input = new byte[32];
        Array.Fill(input, (byte)0xFF);
        Assert.Equal(0x62A8AB43u, Crc32C.Compute(input));
    }

    [Fact]
    public void RFC3720_IncrementingBytes32_Yields_46DD794E()
    {
        var input = new byte[32];
        for (int i = 0; i < 32; i++) input[i] = (byte)i;
        Assert.Equal(0x46DD794Eu, Crc32C.Compute(input));
    }

    [Fact]
    public void OneBitDifference_ChangesCrc()
    {
        var a = new byte[] { 0x00 };
        var b = new byte[] { 0x01 };
        Assert.NotEqual(Crc32C.Compute(a), Crc32C.Compute(b));
    }

    [Fact]
    public void Determinism_SameInputAlwaysSameOutput()
    {
        var input = System.Text.Encoding.UTF8.GetBytes("hello world");
        Assert.Equal(Crc32C.Compute(input), Crc32C.Compute(input));
    }
}
