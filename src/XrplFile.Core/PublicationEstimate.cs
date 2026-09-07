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
/// <param name="Mints">TicketCreate transactions the batches will need, with nothing retried.</param>
/// <param name="Transactions">Everything above plus the manifest. The run as it goes when nothing is retried — what a person is shown, because inflating the transaction count on screen would mislead the other way.</param>
/// <param name="FeeDropsPerTransaction">The node's price at the moment of the estimate.</param>
/// <param name="FeeDropsTotal">That price across <see cref="Transactions"/>: the run as it goes, nothing retried.</param>
/// <param name="MaxAttempts">How many times one memo may be attempted before the run is called off. Defaults to <see cref="TicketStrategyOptions.MaxAttemptsPerMemo"/>'s own default; the two are independent knobs, kept in step by convention rather than by a shared constant.</param>
/// <param name="FeeDropsWorstCase">The fee if every memo needed every one of <see cref="MaxAttempts"/> tries, including a remint each retry cycle: <see cref="Transactions"/> times <see cref="MaxAttempts"/> times <see cref="FeeDropsPerTransaction"/>. What <see cref="RequiredDrops"/> is actually built from.</param>
/// <param name="PeakReserveDrops">What live tickets lock up at the worst moment: memos, capped at the protocol's own live-ticket ceiling (<see cref="TicketStrategy.MaxLiveTickets"/>), not the caller's batch size. A batch is what is normally live, but a ticket dispatched whose submission never reaches a ledger stays live for the run's lifetime, so the honest worst case is the protocol's cap.</param>
/// <param name="BaseReserveDrops">What the account must hold to exist at all.</param>
/// <param name="RequiredDrops">The worst case: <see cref="FeeDropsWorstCase"/>, <see cref="PeakReserveDrops"/> and <see cref="BaseReserveDrops"/>, added. An optimistic number here would let a run start that cannot finish.</param>
/// <param name="BalanceDrops">What the account holds now.</param>
public sealed record PublicationEstimate(
    long Chunks,
    int Blocks,
    int Lists,
    int Mints,
    long Transactions,
    long FeeDropsPerTransaction,
    long FeeDropsTotal,
    int MaxAttempts,
    long FeeDropsWorstCase,
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
        ILedgerSource source, string account, long fileBytes, int ticketsPerBatch, CancellationToken cancellationToken,
        // Matches TicketStrategyOptions.MaxAttemptsPerMemo's own default. The two are independent
        // knobs — nothing forces a caller to keep them equal — but they start out equal so the
        // estimate and the run it describes cannot drift apart by accident.
        int maxAttempts = 3)
    {
        if (fileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileBytes), fileBytes, "an empty file has no chunks and no root");
        }

        if (ticketsPerBatch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticketsPerBatch), ticketsPerBatch, "a batch holds at least one ticket");
        }

        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "at least one attempt is made per memo");
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
        long feeWorstCase = transactions * maxAttempts * fees.OpenLedgerFeeDrops;

        // TicketStrategy caps live tickets at the protocol's own ceiling (MaxLiveTickets), not at
        // the batch size: a ticket dispatched whose submission never reaches a ledger stays
        // counted as live for the strategy instance's lifetime (see TicketStrategy.DisposeAsync).
        // A batch's worth is what is normally live at once; a ticket that never resolves is what
        // makes the honest worst case the protocol's cap instead.
        long peakReserve = Math.Min(memos, TicketStrategy.MaxLiveTickets) * fees.OwnerReserveDrops;
        long required = feeWorstCase + peakReserve + fees.BaseReserveDrops;

        return new PublicationEstimate(
            chunks, blocks, lists, mints, transactions,
            fees.OpenLedgerFeeDrops, feeTotal, maxAttempts, feeWorstCase, peakReserve, fees.BaseReserveDrops, required, balance);
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
