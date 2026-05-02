using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Body-only decoder tests for OrderMassActionRequest (template 701,
/// spec §4.8 / #GAP-19). Wire framing (SOFH+SBE) is exercised by the
/// E2E suite — this file pins field offsets and the
/// Success/UnsupportedFeature/DecodeError mapping in isolation.
/// </summary>
public class OrderMassActionRequestDecoderTests
{
    private const int BlockLength = 52;

    private static byte[] BuildRequest(byte massActionType = 3, byte side = 0,
        ulong securityId = 0, byte ordTagId = 0, byte[]? asset = null, byte[]? investorId = null,
        ulong clOrdId = 4242UL)
    {
        var body = new byte[BlockLength];
        body[18] = massActionType;
        MemoryMarshal.Write(body.AsSpan(20, 8), in clOrdId);
        body[29] = ordTagId;
        body[30] = side;
        if (asset != null) asset.CopyTo(body.AsSpan(32, 6));
        MemoryMarshal.Write(body.AsSpan(38, 8), in securityId);
        if (investorId != null) investorId.CopyTo(body.AsSpan(46, 6));
        return body;
    }

    [Fact]
    public void Success_NoFilters()
    {
        var body = BuildRequest();
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, enteredAtNanos: 100UL, out var cmd, out var clOrd, out var msg);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(msg);
        Assert.Equal(4242UL, clOrd);
        Assert.Equal(0L, cmd.SecurityId);
        Assert.Null(cmd.SideFilter);
        Assert.Equal(100UL, cmd.EnteredAtNanos);
    }

    [Fact]
    public void Success_SideFilter_Buy()
    {
        var body = BuildRequest(side: (byte)'1');
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out var cmd, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(Side.Buy, cmd.SideFilter);
    }

    [Fact]
    public void Success_SideFilter_Sell()
    {
        var body = BuildRequest(side: (byte)'2');
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out var cmd, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(Side.Sell, cmd.SideFilter);
    }

    [Fact]
    public void Success_SecurityIdFilter()
    {
        var body = BuildRequest(securityId: 900_000_000_001UL);
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out var cmd, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(900_000_000_001L, cmd.SecurityId);
    }

    [Fact]
    public void UnsupportedFeature_ReleaseFromSuspension()
    {
        var body = BuildRequest(massActionType: 2);
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("MassActionType", msg);
    }

    [Fact]
    public void UnsupportedFeature_CancelAndSuspend()
    {
        var body = BuildRequest(massActionType: 4);
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
    }

    [Fact]
    public void UnsupportedFeature_OrdTagIdSet()
    {
        var body = BuildRequest(ordTagId: 7);
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("OrdTagID", msg);
    }

    [Fact]
    public void UnsupportedFeature_AssetSet()
    {
        var body = BuildRequest(asset: new byte[] { (byte)'D', (byte)'O', (byte)'L', 0, 0, 0 });
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("Asset", msg);
    }

    [Fact]
    public void UnsupportedFeature_InvestorIdSet()
    {
        // Non-"BVMF" bytes at offset 46 indicate V2 InvestorID is populated.
        var body = BuildRequest(investorId: new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC });
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Contains("InvestorID", msg);
    }

    [Fact]
    public void Success_SecurityExchangeBVMFTreatedAsNoInvestorId()
    {
        // V1 base layout writes the constant SecurityExchange "BVMF" at
        // offset 46; the decoder must NOT treat that as a populated
        // InvestorID.
        var body = BuildRequest(investorId: new byte[] { (byte)'B', (byte)'V', (byte)'M', (byte)'F', 0, 0 });
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
    }

    [Fact]
    public void DecodeError_InvalidMassActionType()
    {
        var body = BuildRequest(massActionType: 99);
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("invalid MassActionType", msg);
    }

    [Fact]
    public void DecodeError_InvalidSide()
    {
        var body = BuildRequest(side: (byte)'9');
        var outcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
            body, 0UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.DecodeError, outcome);
        Assert.Contains("Side", msg);
    }
}
