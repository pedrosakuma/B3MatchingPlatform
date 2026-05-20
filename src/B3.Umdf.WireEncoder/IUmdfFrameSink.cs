namespace B3.Umdf.WireEncoder;

/// <summary>
/// Abstracts the per-command packet buffer's Reserve/Commit pair so
/// <see cref="UmdfFrameBuilder"/> can be tested independently of
/// <c>ChannelDispatcher</c>.
/// </summary>
public interface IUmdfFrameSink
{
    /// <summary>Reserves at least <paramref name="size"/> bytes in the packet
    /// buffer, flushing the current packet first if necessary. Returns a span
    /// over the reserved region.</summary>
    Span<byte> Reserve(int size);

    /// <summary>Advances the write cursor by <paramref name="written"/> bytes,
    /// committing the frame written since the last <see cref="Reserve"/>.</summary>
    void Commit(int written);
}
