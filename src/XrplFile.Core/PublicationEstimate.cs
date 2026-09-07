namespace XrplFile;

/// <summary>
/// What a publication will cost, in the numbers a person is shown before they agree to it.
///
/// It is a set of figures rather than a verdict on purpose. "You cannot afford this" is not
/// useful; "this needs 2.4 XRP and you hold 1.9" is.
/// </summary>
/// <param name="Chunks">Memos carrying file bytes.</param>
/// <param name="Blocks">Merkle blocks the chunks group into.</param>
/// <param name="Lists">Memos carrying the block list; zero when there is one block.</param>
/// <param name="Mints">TicketCreate transactions the batches will need.</param>
/// <param name="Transactions">Everything above plus the manifest.</param>
/// <param name="FeeDropsPerTransaction">The node's price at the moment of the estimate.</param>
/// <param name="FeeDropsTotal">That price across every transaction.</param>
/// <param name="PeakReserveDrops">What live tickets lock up at the worst moment.</param>
/// <param name="BaseReserveDrops">What the account must hold to exist at all.</param>
/// <param name="RequiredDrops">The three above, added.</param>
/// <param name="BalanceDrops">What the account holds now.</param>
public sealed record PublicationEstimate(
    long Chunks,
    int Blocks,
    int Lists,
    int Mints,
    long Transactions,
    long FeeDropsPerTransaction,
    long FeeDropsTotal,
    long PeakReserveDrops,
    long BaseReserveDrops,
    long RequiredDrops,
    long BalanceDrops)
{
    public bool BalanceCovers => BalanceDrops >= RequiredDrops;
}

public static class PublicationEstimator
{
    public static async Task<PublicationEstimate> EstimateAsync(
        ILedgerSource source, string account, long fileBytes, int ticketsPerBatch, CancellationToken cancellationToken)
    {
        if (fileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileBytes), fileBytes, "an empty file has no chunks and no root");
        }

        if (ticketsPerBatch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticketsPerBatch), ticketsPerBatch, "a batch holds at least one ticket");
        }

        long chunks = FileTree.ChunkCount(fileBytes, MemoLayout.ChunkBytes);
        int blocks = FileTree.BlockCount(chunks);
        int lists = blocks > 1 ? BlockListMemos(blocks) : 0;

        long memos = chunks + lists + 1;
        int batch = Math.Min(ticketsPerBatch, TicketStrategy.MaxTicketsPerMint);
        int mints = (int)((memos + batch - 1) / batch);
        long transactions = memos + mints;

        NetworkFees fees = await source.FeesAsync(cancellationToken).ConfigureAwait(false);
        long balance = await source.BalanceDropsAsync(account, cancellationToken).ConfigureAwait(false);

        long feeTotal = transactions * fees.OpenLedgerFeeDrops;

        // Only a batch's worth of tickets is ever live at once, and a file smaller than a batch
        // never mints a full one. Charging the whole file's worth would refuse runs that fit.
        long peakReserve = Math.Min(memos, batch) * fees.OwnerReserveDrops;
        long required = feeTotal + peakReserve + fees.BaseReserveDrops;

        return new PublicationEstimate(
            chunks, blocks, lists, mints, transactions,
            fees.OpenLedgerFeeDrops, feeTotal, peakReserve, fees.BaseReserveDrops, required, balance);
    }

    /// <summary>
    /// Refuses a run the caller's ceiling forbids, then one the account cannot carry. The ceiling
    /// comes first because it is the caller's own decision and needs no excuse from the network.
    /// </summary>
    public static void Require(PublicationEstimate estimate, long? budgetDrops)
    {
        if (budgetDrops is { } ceiling && estimate.RequiredDrops > ceiling)
        {
            throw new XrplFileException(XrplFileError.BudgetExceeded,
                $"the run needs {estimate.RequiredDrops} drops and the budget allows {ceiling}");
        }

        if (!estimate.BalanceCovers)
        {
            throw new XrplFileException(XrplFileError.InsufficientBalance,
                $"the run needs {estimate.RequiredDrops} drops and the account holds {estimate.BalanceDrops}");
        }
    }

    /// <summary>
    /// How many memos the block list takes. Counted by packing real entries rather than by
    /// arithmetic, because the packer is greedy and its own code is the only honest answer.
    /// </summary>
    private static int BlockListMemos(int blocks)
    {
        string hash = new('0', 64);
        List<BlockEntry> entries = [.. Enumerable.Range(0, blocks).Select(i => new BlockEntry(i, hash, 999_999_999))];

        return FileTree.EncodeEntries(entries).Count;
    }
}
