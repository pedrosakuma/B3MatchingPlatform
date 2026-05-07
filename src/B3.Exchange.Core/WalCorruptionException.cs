namespace B3.Exchange.Core;

/// <summary>
/// Thrown by <see cref="IChannelWriteAheadLog.ReadAll"/> when a WAL
/// record fails its integrity check (Crc32C mismatch, JSON parse
/// failure with valid CRC framing, or a sequence-number gap) at a
/// position that is not the physical tail of the file.
///
/// <para><b>Tail vs mid-stream (issue #285 follow-up):</b> the original
/// #285 implementation skipped every corrupt record uniformly. That is
/// only safe for a torn final write: if the host crashed mid-fsync the
/// last record may be partial and dropping it preserves the surviving
/// prefix. A mid-stream corruption is a different beast — silently
/// applying records 1, 2, 4, 5 (with 3 dropped) yields a book state
/// that never existed and violates the durability contract the WAL is
/// supposed to provide. Raising this exception forces the dispatcher
/// to halt the channel via the issue #286 mechanism instead of booting
/// into a fabricated state.</para>
///
/// <para>Operators see this as a CRITICAL log line plus the channel
/// flipping <c>IsWalHealthy=false</c>; recovery is to inspect the WAL
/// (or fall back to an older snapshot generation) and restart.</para>
/// </summary>
public sealed class WalCorruptionException : Exception
{
    public byte ChannelNumber { get; }

    /// <summary>Physical line / record number (1-based) where the
    /// corruption was detected. Useful for operator triage.</summary>
    public int RecordNumber { get; }

    public WalCorruptionException(byte channelNumber, int recordNumber, string message)
        : base(message)
    {
        ChannelNumber = channelNumber;
        RecordNumber = recordNumber;
    }

    public WalCorruptionException(byte channelNumber, int recordNumber, string message, Exception innerException)
        : base(message, innerException)
    {
        ChannelNumber = channelNumber;
        RecordNumber = recordNumber;
    }
}
