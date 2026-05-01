namespace B3.Exchange.Contracts;

/// <summary>
/// Identifier of the firm (broker / "corretora") that owns a session.
/// Determined at authentication time and carried through every command
/// and event so Core, audit and post-trade can attribute every action to
/// the responsible market participant.
/// </summary>
public readonly record struct FirmId(string Value)
{
    public override string ToString() => Value;
}
