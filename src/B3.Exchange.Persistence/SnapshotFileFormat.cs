namespace B3.Exchange.Persistence;

/// <summary>
/// Issue #266: on-disk wire encoding picked by
/// <see cref="FileChannelStatePersister"/> when writing snapshots.
/// Both formats are always recognised on load (the persister sniffs the
/// file's leading bytes); this enum only controls what gets WRITTEN.
/// </summary>
public enum SnapshotFileFormat
{
    /// <summary>
    /// Pretty-debuggable JSON via <c>System.Text.Json</c>. The historical
    /// PR #261 default — kept as the default to avoid surprising any
    /// existing deployment. Every field in <see cref="B3.Exchange.Core.ChannelStateSnapshot"/>
    /// is rendered with its property name, so files remain human-readable
    /// and amenable to <c>jq</c>-driven inspection.
    /// </summary>
    Json = 0,

    /// <summary>
    /// Compact little-endian binary encoded by
    /// <see cref="BinaryChannelStateSnapshotCodec"/>. Targets a 5–10×
    /// size reduction over <see cref="Json"/> on representative books
    /// (per issue #266 acceptance criteria) by dropping property names,
    /// using fixed-width primitives instead of decimal text, and
    /// length-prefixing variable-length collections instead of
    /// punctuating them. The leading magic bytes <c>B3SS</c> let the
    /// persister auto-detect the format on load so a deployment can
    /// switch direction (or roll back) without a one-shot conversion.
    /// </summary>
    Binary = 1,
}
