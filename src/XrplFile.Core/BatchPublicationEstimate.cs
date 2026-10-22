namespace XrplFile;

/// <summary>
/// What a Batch publication will cost each of its two accounts (spec 4.5). The worst cases are true
/// bounds for one session under spec B13 and B15 (spec B26); a resume is a new session and is priced again.
/// </summary>
/// <param name="Chunks">Memos carrying file bytes.</param>
/// <param name="Blocks">Merkle blocks the chunks group into.</param>
/// <param name="Lists">Memos carrying the block list; zero when there is one block.</param>
/// <param name="Memos">Every memo of the run: the chunks, the lists and the manifest.</param>
/// <param name="Batches">Batch transactions over the three passes, each grouped by spec B11.</param>
/// <param name="SoloTransactions">Memos sent on their own: a pass of exactly one memo.</param>
/// <param name="OpenLedgerFeeDrops">The node's open-ledger fee at the moment of the estimate.</param>
/// <param name="FeeUnitDrops">The fee unit the estimate approved (<see cref="BatchFees.Unit"/>).</param>
/// <param name="FeeCeilingDrops">The most a round may pay per unit (<see cref="BatchFees.Ceiling"/>).</param>
/// <param name="PayerFeeDrops">Σ (inners + 3) × unit over the batches, nothing retried.</param>
/// <param name="WriterFeeDrops">Solo transactions × unit, nothing retried.</param>
/// <param name="PayerFeeDropsWorstCase">ceil(Memos × 3 × 2.5) × ceiling: every memo in three candidates, in batches of two.</param>
/// <param name="WriterFeeDropsWorstCase">(Memos × 3 + 250) × ceiling: every memo three times as a solo, and a full tidy-up.</param>
/// <param name="BaseReserveDrops">What an account must hold to exist at all.</param>
/// <param name="OwnerReserveDrops">What one owned object locks, as the node reports it now.</param>
/// <param name="WriterBalanceDrops">What the writer holds now.</param>
/// <param name="WriterOwnerCount">Ledger objects the writer already owns.</param>
/// <param name="PayerBalanceDrops">What the payer holds now.</param>
/// <param name="PayerOwnerCount">Ledger objects the payer already owns.</param>
public sealed record BatchPublicationEstimate(
    long Chunks, int Blocks, int Lists, long Memos,
    int Batches, int SoloTransactions,
    long OpenLedgerFeeDrops, long FeeUnitDrops, long FeeCeilingDrops,
    long PayerFeeDrops,
    long WriterFeeDrops,
    long PayerFeeDropsWorstCase,
    long WriterFeeDropsWorstCase,
    long BaseReserveDrops, long OwnerReserveDrops,
    long WriterBalanceDrops, int WriterOwnerCount,
    long PayerBalanceDrops, int PayerOwnerCount)
{
    /// <summary>What the writer must hold to carry the run: its base reserve and its worst-case fees.</summary>
    public long WriterRequiredDrops => BaseReserveDrops + WriterFeeDropsWorstCase;

    /// <summary>What the payer must hold to carry the run: its base reserve and its worst-case fees.</summary>
    public long PayerRequiredDrops => BaseReserveDrops + PayerFeeDropsWorstCase;

    /// <summary>The writer's balance less what its existing objects lock.</summary>
    public long WriterSpendableDrops => WriterBalanceDrops - WriterOwnerCount * OwnerReserveDrops;

    /// <summary>The payer's balance less what its existing objects lock.</summary>
    public long PayerSpendableDrops => PayerBalanceDrops - PayerOwnerCount * OwnerReserveDrops;

    /// <summary>Whether the writer can spend <see cref="WriterRequiredDrops"/>.</summary>
    public bool WriterCovers => WriterSpendableDrops >= WriterRequiredDrops;

    /// <summary>Whether the payer can spend <see cref="PayerRequiredDrops"/>.</summary>
    public bool PayerCovers => PayerSpendableDrops >= PayerRequiredDrops;
}
