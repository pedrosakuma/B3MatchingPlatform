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
    /// <c>maxValue</c> attribute in the SBE schema). When
    /// <see cref="RejectLineBreaks"/> is set, the validator additionally
    /// scans the segment for CR (0x0D) / LF (0x0A) bytes (spec §4.10:
    /// trader-identity / desk fields are single-line text).</summary>
    public readonly record struct Field(string Name, byte MaxLength, bool RejectLineBreaks = false);

    private static readonly Field Memo = new("memo", 40);
    private static readonly Field DeskID = new("deskID", 20, RejectLineBreaks: true);
    private static readonly Field NegotiateCredentials = new("credentials", 128);
    private static readonly Field NegotiateClientIp = new("clientIP", 30);
    private static readonly Field NegotiateClientAppName = new("clientAppName", 30);
    private static readonly Field NegotiateClientAppVersion = new("clientAppVersion", 30);
    private static readonly Field EstablishCredentials = new("credentials", 128);

    private static readonly Field[] SimpleNewOrderFields = [Memo];
    private static readonly Field[] SimpleModifyOrderFields = [Memo];
    private static readonly Field[] NewOrderSingleFields = [DeskID, Memo];
    private static readonly Field[] OrderCancelReplaceFields = [DeskID, Memo];
    private static readonly Field[] OrderCancelFields = [DeskID, Memo];
    private static readonly Field[] NegotiateFields =
        [NegotiateCredentials, NegotiateClientIp, NegotiateClientAppName, NegotiateClientAppVersion];
    private static readonly Field[] EstablishFields = [EstablishCredentials];
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
        (EntryPointFrameReader.TidNewOrderSingle, 2) => NewOrderSingleFields,
        (EntryPointFrameReader.TidOrderCancelReplaceRequest, 2) => OrderCancelReplaceFields,
        (EntryPointFrameReader.TidOrderCancelRequest, 0) => OrderCancelFields,
        (EntryPointFrameReader.TidNegotiate, 0) => NegotiateFields,
        (EntryPointFrameReader.TidEstablish, 0) => EstablishFields,
        _ => None,
    };

    /// <summary>
    /// Spec §4.10 classification for a varData validation failure.
    /// Distinguishes business-rule rejects (must answer with
    /// <c>BusinessMessageReject(33003)</c> per spec) from protocol errors
    /// that warrant session termination (<c>DECODING_ERROR</c>).
    /// </summary>
    public enum FailureKind
    {
        /// <summary>varData is valid.</summary>
        None = 0,
        /// <summary>A field exceeded its declared <see cref="Field.MaxLength"/>.
        /// Spec §4.10: BMR(33003 "&lt;field&gt; too long").</summary>
        FieldTooLong,
        /// <summary>A <see cref="Field.RejectLineBreaks"/> field contained
        /// CR or LF. Spec §4.10: BMR(33003 "Line breaks not supported in
        /// &lt;field&gt;").</summary>
        ContainsLineBreaks,
        /// <summary>The varData blob is structurally malformed
        /// (truncated, length-prefix overruns buffer, trailing bytes).
        /// Spec §3.5 / §4.10: DECODING_ERROR → terminate session.</summary>
        ProtocolError,
    }

    /// <summary>Structured outcome of <see cref="ValidateDetailed"/>.</summary>
    public readonly record struct Result(FailureKind Kind, string? FieldName, string? DebugMessage)
    {
        public bool IsOk => Kind == FailureKind.None;
        public bool IsBusinessReject => Kind is FailureKind.FieldTooLong or FailureKind.ContainsLineBreaks;

        /// <summary>
        /// Returns the BMR text corresponding to spec §4.10 for this
        /// failure, or <c>null</c> if not applicable. Caller passes the
        /// returned string as the <c>text</c> field of
        /// <c>BusinessMessageReject(33003)</c>.
        /// </summary>
        public string? BmrText() => Kind switch
        {
            FailureKind.FieldTooLong => $"{FieldName} too long",
            FailureKind.ContainsLineBreaks => $"Line breaks not supported in {FieldName}",
            _ => null,
        };
    }

    /// <summary>
    /// Validates <paramref name="varData"/> with full §4.10 classification
    /// (length, line-breaks, structural). Replaces <see cref="TryValidate"/>
    /// for callers that need to distinguish BMR-eligible failures from
    /// protocol errors. The legacy bool-returning API is kept as a
    /// passthrough for tests / non-strict paths.
    /// </summary>
    public static Result ValidateDetailed(ReadOnlySpan<byte> varData, ReadOnlySpan<Field> spec)
    {
        int offset = 0;
        for (int i = 0; i < spec.Length; i++)
        {
            if (offset >= varData.Length)
            {
                if (offset == varData.Length) return new Result(FailureKind.None, null, null);
                return new Result(FailureKind.ProtocolError, spec[i].Name,
                    $"varData truncated at field '{spec[i].Name}' (offset {offset} > {varData.Length})");
            }
            byte len = varData[offset];
            int payloadStart = offset + 1;
            int next = payloadStart + len;
            // Structural check first: if the declared length cannot fit
            // in the remaining buffer, that is a protocol error
            // (DECODING_ERROR / Terminate) — not a §4.10 BMR. Ordering
            // matters: a frame with len=255 but only the prefix byte
            // present must NOT be classified as FieldTooLong, since the
            // session is fundamentally desynchronised at the SBE layer.
            if (next > varData.Length)
            {
                return new Result(FailureKind.ProtocolError, spec[i].Name,
                    $"varData field '{spec[i].Name}' length {len} overruns buffer (offset {payloadStart} + {len} > {varData.Length})");
            }
            if (len > spec[i].MaxLength)
            {
                return new Result(FailureKind.FieldTooLong, spec[i].Name,
                    $"varData field '{spec[i].Name}' length {len} exceeds max {spec[i].MaxLength}");
            }
            if (spec[i].RejectLineBreaks && len > 0)
            {
                var payload = varData.Slice(payloadStart, len);
                for (int b = 0; b < payload.Length; b++)
                {
                    if (payload[b] == 0x0A || payload[b] == 0x0D)
                    {
                        return new Result(FailureKind.ContainsLineBreaks, spec[i].Name,
                            $"varData field '{spec[i].Name}' contains line break at offset {b}");
                    }
                }
            }
            offset = next;
        }
        if (offset != varData.Length)
        {
            return new Result(FailureKind.ProtocolError, null,
                $"varData has {varData.Length - offset} trailing byte(s) after declared fields");
        }
        return new Result(FailureKind.None, null, null);
    }

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
        var r = ValidateDetailed(varData, spec);
        error = r.DebugMessage;
        return r.IsOk;
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
