namespace B3.Exchange.Gateway;

/// <summary>
/// Walks and validates the length-prefixed variable-length data segments
/// that follow the SBE fixed root block in EntryPoint application
/// messages (spec §3.5 / §4.10).
///
/// Each segment is encoded as <c>length(uint8)</c> + <c>varData(N bytes)</c>.
/// The set, order and per-segment maximum length depend on the template;
/// see <see cref="ExpectedFor(ushort, ushort)"/>.
/// </summary>
public static class EntryPointVarData
{
    /// <summary>One varData field declaration: spec name and the maximum
    /// payload length in bytes (the <c>length</c> primitive's
    /// <c>maxValue</c> attribute in the SBE schema).</summary>
    public readonly record struct Field(string Name, byte MaxLength);

    private static readonly Field Memo = new("memo", 40);
    private static readonly Field DeskID = new("deskID", 20);

    private static readonly Field[] SimpleNewOrderFields = [Memo];
    private static readonly Field[] SimpleModifyOrderFields = [Memo];
    private static readonly Field[] OrderCancelFields = [DeskID, Memo];
    private static readonly Field[] None = [];

    /// <summary>
    /// Returns the ordered varData specification for a template (the
    /// fields the gateway expects after the fixed block, top-to-bottom in
    /// the SBE schema). Unknown templates and session-level frames return
    /// an empty array.
    /// </summary>
    public static ReadOnlySpan<Field> ExpectedFor(ushort templateId, ushort version) => (templateId, version) switch
    {
        (EntryPointFrameReader.TidSimpleNewOrder, 2) => SimpleNewOrderFields,
        (EntryPointFrameReader.TidSimpleModifyOrder, 2) => SimpleModifyOrderFields,
        (EntryPointFrameReader.TidOrderCancelRequest, 0) => OrderCancelFields,
        _ => None,
    };

    /// <summary>
    /// Validates that <paramref name="varData"/> is a well-formed
    /// concatenation of length-prefixed segments matching
    /// <paramref name="spec"/>. Each segment is allowed to be empty
    /// (length=0) and must not exceed its declared
    /// <see cref="Field.MaxLength"/>. The cumulative length of all
    /// segments (including their 1-byte length prefixes) must equal
    /// <paramref name="varData"/>.Length exactly.
    /// </summary>
    public static bool TryValidate(ReadOnlySpan<byte> varData, ReadOnlySpan<Field> spec, out string? error)
    {
        error = null;
        int offset = 0;
        for (int i = 0; i < spec.Length; i++)
        {
            if (offset >= varData.Length)
            {
                // Per spec, varData fields are optional: a field may be omitted
                // by truncating the trailer, but if present they must appear in
                // order. If we're past the buffer, remaining fields are simply
                // not present — that is OK as long as the buffer ended
                // exactly.
                if (offset == varData.Length) return true;
                error = $"varData truncated at field '{spec[i].Name}' (offset {offset} > {varData.Length})";
                return false;
            }
            byte len = varData[offset];
            if (len > spec[i].MaxLength)
            {
                error = $"varData field '{spec[i].Name}' length {len} exceeds max {spec[i].MaxLength}";
                return false;
            }
            int next = offset + 1 + len;
            if (next > varData.Length)
            {
                error = $"varData field '{spec[i].Name}' length {len} overruns buffer (offset {offset + 1} + {len} > {varData.Length})";
                return false;
            }
            offset = next;
        }
        if (offset != varData.Length)
        {
            error = $"varData has {varData.Length - offset} trailing byte(s) after declared fields";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Iterates the next segment in <paramref name="remaining"/>: reads
    /// the 1-byte length, slices the payload, and advances
    /// <paramref name="remaining"/>. Returns <c>false</c> if no more
    /// data, or if the length prefix overruns the buffer.
    /// </summary>
    public static bool TryReadNext(ref ReadOnlySpan<byte> remaining, out ReadOnlySpan<byte> segment)
    {
        segment = default;
        if (remaining.IsEmpty) return false;
        byte len = remaining[0];
        if (1 + len > remaining.Length) return false;
        segment = remaining.Slice(1, len);
        remaining = remaining.Slice(1 + len);
        return true;
    }
}
