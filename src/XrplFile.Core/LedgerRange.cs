namespace XrplFile;

/// <summary>The first and last ledger something landed in, both inclusive.</summary>
public readonly record struct LedgerRange(long First, long Last)
{
    public static LedgerRange Of(long ledger) => new(ledger, ledger);

    public LedgerRange Including(long ledger) => new(Math.Min(First, ledger), Math.Max(Last, ledger));

    public LedgerRange Including(LedgerRange other) => new(Math.Min(First, other.First), Math.Max(Last, other.Last));
}
