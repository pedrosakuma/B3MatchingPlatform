namespace B3.Exchange.Core;

internal static class UmdfSequenceVersion
{
    public static void EnsureCanBeginLiveWork(ushort current, string context)
    {
        if (current == 0)
        {
            throw new InvalidOperationException(
                $"{context}: SequenceVersion 0 is reserved as the SBE null value");
        }
        if (current == ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"{context}: SequenceVersion space is exhausted at terminal wire epoch {ushort.MaxValue}; refusing to begin new live work");
        }
    }

    public static ushort NextOrThrow(ushort current, string context)
    {
        if (current == 0)
        {
            throw new InvalidOperationException(
                $"{context}: SequenceVersion 0 is reserved as the SBE null value");
        }
        if (current == ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"{context}: SequenceVersion space is exhausted at {ushort.MaxValue}; refusing to wrap to reserved null value 0");
        }
        return (ushort)(current + 1);
    }

    public static ushort NextLiveOrThrow(ushort current, string context)
    {
        ushort next = NextOrThrow(current, context);
        if (next == ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"{context}: SequenceVersion space is exhausted before terminal wire epoch {ushort.MaxValue}; refusing to prepare an epoch that cannot accept live work");
        }
        return next;
    }
}
