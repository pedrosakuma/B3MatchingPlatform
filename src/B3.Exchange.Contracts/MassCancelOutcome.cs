namespace B3.Exchange.Contracts;

/// <summary>
/// Terminal result of a solicited mass-cancel after every targeted channel
/// has executed its resolved batch.
/// </summary>
public readonly record struct MassCancelOutcome(bool Succeeded, int TotalAffectedOrders)
{
    public static MassCancelOutcome Completed(int totalAffectedOrders)
        => new(true, totalAffectedOrders);

    public static MassCancelOutcome SystemBusy => new(false, 0);
}
