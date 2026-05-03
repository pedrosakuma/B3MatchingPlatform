namespace B3.EntryPoint.Wire;

/// <summary>
/// Validates the fixed-block ASCII identifier slots
/// (<c>senderLocation</c>, <c>enteringTrader</c>, <c>executingTrader</c>)
/// that appear in the root SBE blocks of EntryPoint application
/// templates per spec §4.10. The fields are space- or NUL-padded ASCII;
/// any embedded CR (0x0D) / LF (0x0A) byte must trigger
/// <c>BusinessMessageReject(33003 "Line breaks not supported in
/// &lt;fieldName&gt;")</c>.
///
/// Length rejection is not applicable here because the schema fixes the
/// slot width (5 or 10 bytes); over-length cases are instead detected by
/// the varData walker (see <see cref="EntryPointVarData"/>).
/// </summary>
public static class EntryPointFixedIdentifiers
{
    /// <summary>Field declaration: (spec name, byte offset within the
    /// fixed block, slot length).</summary>
    public readonly record struct Slot(string Name, int Offset, int Length);

    // NewOrderSingleV2 (templateId=102, BlockLength=125): senderLocation@32(10),
    // enteringTrader@42(5), executingTrader@100(5).
    private static readonly Slot[] NewOrderSingleSlots =
    [
        new("senderLocation", 32, 10),
        new("enteringTrader", 42, 5),
        new("executingTrader", 100, 5),
    ];

    // OrderCancelReplaceRequestV2 (templateId=104, BlockLength=142):
    // senderLocation@32(10), enteringTrader@42(5), executingTrader@116(5).
    private static readonly Slot[] OrderCancelReplaceSlots =
    [
        new("senderLocation", 32, 10),
        new("enteringTrader", 42, 5),
        new("executingTrader", 116, 5),
    ];

    // OrderCancelRequest V0 (templateId=105, BlockLength=76):
    // senderLocation@56(10), enteringTrader@66(5), executingTrader@71(5).
    private static readonly Slot[] OrderCancelSlots =
    [
        new("senderLocation", 56, 10),
        new("enteringTrader", 66, 5),
        new("executingTrader", 71, 5),
    ];

    // NewOrderCross V6 (templateId=106, root BlockLength=84):
    // senderLocation@28(10), enteringTrader@38(5), executingTrader@43(5).
    private static readonly Slot[] NewOrderCrossSlots =
    [
        new("senderLocation", 28, 10),
        new("enteringTrader", 38, 5),
        new("executingTrader", 43, 5),
    ];

    private static readonly Slot[] None = [];

    /// <summary>Returns the identifier slots a given template carries in
    /// its fixed block. Templates without identifier slots
    /// (SimpleNewOrder, SimpleModifyOrder, session-layer frames) return
    /// an empty span.</summary>
    public static ReadOnlySpan<Slot> ExpectedFor(ushort templateId, ushort version) => (templateId, version) switch
    {
        (EntryPointFrameReader.TidNewOrderSingle, 2) => NewOrderSingleSlots,
        (EntryPointFrameReader.TidOrderCancelReplaceRequest, 2) => OrderCancelReplaceSlots,
        (EntryPointFrameReader.TidOrderCancelRequest, 0) => OrderCancelSlots,
        (EntryPointFrameReader.TidNewOrderCross, 6) => NewOrderCrossSlots,
        _ => None,
    };

    /// <summary>Returns <c>true</c> iff <paramref name="field"/> contains
    /// at least one CR (0x0D) or LF (0x0A) byte. Allocation-free; safe to
    /// call on the per-frame hot path.</summary>
    public static bool ContainsCrLf(ReadOnlySpan<byte> field)
    {
        for (int i = 0; i < field.Length; i++)
        {
            byte b = field[i];
            if (b == 0x0A || b == 0x0D) return true;
        }
        return false;
    }

    /// <summary>Validation outcome for the identifier sweep.</summary>
    public readonly record struct Result(bool ContainsLineBreak, string? FieldName)
    {
        public bool IsOk => !ContainsLineBreak;

        /// <summary>Returns the BMR text mandated by spec §4.10
        /// (<c>"Line breaks not supported in &lt;fieldName&gt;"</c>) or
        /// <c>null</c> when the result is valid.</summary>
        public string? BmrText() => ContainsLineBreak ? $"Line breaks not supported in {FieldName}" : null;
    }

    /// <summary>Scans every identifier slot defined for <paramref name="templateId"/>
    /// for CR/LF bytes. Returns the first offending field name (schema
    /// declaration order) so the caller can answer with
    /// <c>BusinessMessageReject(33003)</c>.</summary>
    public static Result Validate(ushort templateId, ushort version, ReadOnlySpan<byte> fixedBlock)
    {
        var slots = ExpectedFor(templateId, version);
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s.Offset + s.Length > fixedBlock.Length)
            {
                // Defensive: caller has already validated BlockLength so
                // this should be unreachable, but skip silently rather
                // than throw — the framing layer owns wire integrity.
                continue;
            }
            if (ContainsCrLf(fixedBlock.Slice(s.Offset, s.Length)))
                return new Result(ContainsLineBreak: true, FieldName: s.Name);
        }
        return new Result(ContainsLineBreak: false, FieldName: null);
    }
}
