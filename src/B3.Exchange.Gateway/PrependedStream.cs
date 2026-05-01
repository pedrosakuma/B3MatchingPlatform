namespace B3.Exchange.Gateway;

/// <summary>
/// Read-side stream wrapper that returns a fixed in-memory <c>prefix</c>
/// before delegating to an inner <see cref="Stream"/>. Used by the
/// per-connection first-frame router in <see cref="EntryPointListener"/>:
/// the listener reads enough bytes to decide whether the connection is a
/// rebind (issue #69b-2) or a fresh session, then prepends those already-
/// consumed bytes to the underlying <see cref="System.Net.Sockets.NetworkStream"/>
/// so the downstream <see cref="FixpSession"/> can re-process them through
/// its normal receive loop without duplicated decoders.
///
/// <para>Write-side and disposal are forwarded as-is to the inner
/// stream. Only the read paths are buffered.</para>
/// </summary>
internal sealed class PrependedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private int _prefixPos;

    public PrependedStream(byte[] prefix, Stream inner)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(inner);
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var fromPrefix = ReadFromPrefix(buffer.AsSpan(offset, count));
        if (fromPrefix > 0) return fromPrefix;
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        var fromPrefix = ReadFromPrefix(buffer);
        if (fromPrefix > 0) return fromPrefix;
        return _inner.Read(buffer);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var fromPrefix = ReadFromPrefix(buffer.AsSpan(offset, count));
        if (fromPrefix > 0) return fromPrefix;
        return await _inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var fromPrefix = ReadFromPrefix(buffer.Span);
        if (fromPrefix > 0) return fromPrefix;
        return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    private int ReadFromPrefix(Span<byte> dest)
    {
        var remaining = _prefix.Length - _prefixPos;
        if (remaining <= 0) return 0;
        var take = Math.Min(remaining, dest.Length);
        _prefix.AsSpan(_prefixPos, take).CopyTo(dest);
        _prefixPos += take;
        return take;
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
