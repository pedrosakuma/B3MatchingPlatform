namespace B3.Exchange.Instruments;

public static class InstrumentSecurityTypes
{
    public static bool IsOption(string securityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityType);

        return securityType.ToUpperInvariant() switch
        {
            "OPT" or "INDEXOPT" or "FOPT" or "OPTION" or "INDEXOPTION" => true,
            _ => false,
        };
    }
}
