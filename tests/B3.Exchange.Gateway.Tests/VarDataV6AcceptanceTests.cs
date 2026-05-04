using B3.EntryPoint.Wire;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// #239 (sub-issue 1): the inbound varData spec table only registered
/// `(template, 2)` entries, so when the V6 header acceptance from
/// #237 let `(102, 6)` past the header parser, the varData walker
/// got an empty `Field[]` and reported "varData has 2 trailing
/// byte(s) after declared fields" for the empty `[DeskID, Memo]`
/// pair (`[0x00, 0x00]`) that EPC 0.8.0 always appends.
///
/// This test pins that the V6 entries map to the same field spec as
/// V2 — the schema delta between V2 and V6 is purely additive
/// optional fields in the fixed root block; the varData spec is
/// unchanged.
/// </summary>
public class VarDataV6AcceptanceTests
{
    [Theory]
    [InlineData(EntryPointFrameReader.TidNewOrderSingle, 2, 2)]              // [DeskID, Memo]
    [InlineData(EntryPointFrameReader.TidNewOrderSingle, 6, 2)]              // #239: same as V2
    [InlineData(EntryPointFrameReader.TidOrderCancelReplaceRequest, 2, 2)]
    [InlineData(EntryPointFrameReader.TidOrderCancelReplaceRequest, 6, 2)]   // #239
    [InlineData(EntryPointFrameReader.TidSimpleNewOrder, 2, 1)]              // [Memo]
    [InlineData(EntryPointFrameReader.TidSimpleNewOrder, 6, 1)]              // #239
    [InlineData(EntryPointFrameReader.TidSimpleModifyOrder, 2, 1)]
    [InlineData(EntryPointFrameReader.TidSimpleModifyOrder, 6, 1)]           // #239
    public void ExpectedFor_V2AndV6_ReturnSameFieldCount(ushort templateId, ushort version, int expectedFieldCount)
    {
        var spec = EntryPointVarData.ExpectedFor(templateId, version);
        Assert.Equal(expectedFieldCount, spec.Length);
    }

    /// <summary>
    /// The exact byte pattern reported in #239: 2 bytes of varData
    /// = empty DeskID (length=0) + empty Memo (length=0). Must be
    /// accepted (no trailing-bytes error) for both V2 and V6.
    /// </summary>
    [Theory]
    [InlineData((ushort)2)]
    [InlineData((ushort)6)]
    public void NewOrderSingle_EmptyDeskIdAndMemo_AcceptsForBothVersions(ushort version)
    {
        var spec = EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidNewOrderSingle, version);
        // Empty DeskID prefix + empty Memo prefix.
        ReadOnlySpan<byte> varData = stackalloc byte[] { 0x00, 0x00 };
        var result = EntryPointVarData.ValidateDetailed(varData, spec);
        Assert.True(result.IsOk, $"expected Ok, got {result.Kind}: {result.DebugMessage}");
    }

    /// <summary>
    /// Prove the bug the way it manifested before the fix: when V6
    /// is mapped to the empty spec (the bug), a 2-byte varData is
    /// classified as a protocol error. Pin the validator semantics
    /// so a future regression that drops V6 from the table is caught.
    /// </summary>
    [Fact]
    public void EmptySpec_TwoByteVarData_IsProtocolError()
    {
        ReadOnlySpan<byte> varData = stackalloc byte[] { 0x00, 0x00 };
        var result = EntryPointVarData.ValidateDetailed(varData, ReadOnlySpan<EntryPointVarData.Field>.Empty);
        Assert.Equal(EntryPointVarData.FailureKind.ProtocolError, result.Kind);
        Assert.Contains("trailing byte", result.DebugMessage);
    }
}
