using System.Text.Json.Serialization;
using B3.Exchange.Core;

namespace B3.Exchange.Persistence;

/// <summary>
/// System.Text.Json source-generated <see cref="JsonSerializerContext"/>
/// for <see cref="WalRecord"/>. The WAL append path used to call
/// <c>JsonSerializer.SerializeToUtf8Bytes(record, options)</c>, which
/// walks the record graph reflectively and allocates pooled internal
/// buffers per call — measurable Gen0 pressure on the dispatch thread
/// at 50k+ records/sec. Source generation produces a precompiled
/// converter that emits straight to a pooled <c>Utf8JsonWriter</c>
/// without runtime reflection.
///
/// <para><b>Wire compatibility:</b>
/// <see cref="JsonSourceGenerationOptionsAttribute.UseStringEnumConverter"/>
/// + <see cref="JsonSourceGenerationOptionsAttribute.DefaultIgnoreCondition"/>
/// match the previous runtime <c>JsonSerializerOptions</c> exactly, so the
/// JSON bytes produced by this context are byte-identical to what
/// pre-change writers emitted. A WAL written by this build replays
/// cleanly under the previous decoder, and vice-versa.</para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WalRecord))]
internal sealed partial class WalJsonContext : JsonSerializerContext
{
}
