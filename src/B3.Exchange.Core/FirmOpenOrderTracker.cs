namespace B3.Exchange.Core;

/// <summary>
/// Process-wide admission counter for simultaneously open orders per entering
/// firm. Dispatchers share one instance so the configured limit applies across
/// every UMDF channel, while reservations make admission atomic when channel
/// loops process orders concurrently.
/// </summary>
public sealed class FirmOpenOrderTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> _counts = new();

    public FirmOpenOrderTracker(int maximumPerFirm)
    {
        if (maximumPerFirm < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPerFirm), "maximum open orders per firm must be at least 1");
        MaximumPerFirm = maximumPerFirm;
    }

    public int MaximumPerFirm { get; }

    public bool TryReserve(uint firm, int slots)
    {
        if (slots < 0) throw new ArgumentOutOfRangeException(nameof(slots));
        if (slots == 0) return true;

        while (true)
        {
            int current = _counts.GetOrAdd(firm, 0);
            if (current > MaximumPerFirm - slots) return false;
            if (_counts.TryUpdate(firm, current + slots, current)) return true;
        }
    }

    public void AddExisting(uint firm, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        _counts.AddOrUpdate(firm, count, (_, current) => checked(current + count));
    }

    public void Release(uint firm, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;

        while (true)
        {
            int current = _counts.GetOrAdd(firm, 0);
            int next = Math.Max(0, current - count);
            if (_counts.TryUpdate(firm, next, current)) return;
        }
    }

    public int Count(uint firm) => _counts.TryGetValue(firm, out int count) ? count : 0;
}
