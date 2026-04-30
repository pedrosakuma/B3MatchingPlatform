using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;

namespace B3.Umdf.WireEncoder.Tests;

public class SecurityTypeMapTests
{
    [Theory]
    [InlineData("CS", (byte)SecurityType.CS)]
    [InlineData("cs", (byte)SecurityType.CS)]
    [InlineData("PS", (byte)SecurityType.PS)]
    [InlineData("OPT", (byte)SecurityType.OPT)]
    [InlineData("FUT", (byte)SecurityType.FUT)]
    [InlineData("ETF", (byte)SecurityType.ETF)]
    public void DirectEnumNames_MapToSbeByte(string input, byte expected)
        => Assert.Equal(expected, SecurityTypeMap.ToSbeByte(input));

    [Theory]
    [InlineData("EQUITY", (byte)SecurityType.CS)]
    [InlineData("equity", (byte)SecurityType.CS)]
    [InlineData("STOCK", (byte)SecurityType.CS)]
    [InlineData("FUTURE", (byte)SecurityType.FUT)]
    [InlineData("OPTION", (byte)SecurityType.OPT)]
    [InlineData("PREFERRED", (byte)SecurityType.PS)]
    public void Aliases_MapToSbeByte(string input, byte expected)
        => Assert.Equal(expected, SecurityTypeMap.ToSbeByte(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT_A_TYPE")]
    public void UnknownOrEmpty_MapToUnspecified(string? input)
        => Assert.Equal(SecurityTypeMap.Unspecified, SecurityTypeMap.ToSbeByte(input));
}
