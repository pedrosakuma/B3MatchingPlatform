namespace B3.Exchange.Core;

internal static class UmdfSequenceVersion
{
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
}
