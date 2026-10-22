namespace XrplFile;

/// <summary>
/// What an approved estimate lets a Batch run spend (spec B15, B26): the unit it was priced at, the
/// ceiling no round's unit may pass, and each account's worst case, which the strategy refuses to
/// sign past.
/// </summary>
public sealed record BatchBudget(long FeeUnitDrops, long FeeCeilingDrops, long PayerBudgetDrops, long WriterBudgetDrops);
