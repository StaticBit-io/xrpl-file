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
/// <param name="Mints">TicketCreate transactions the batches will need, with nothing retried. The writer batches three times over — the chunks, the block list, the manifest — and each pass mints from an empty queue, because minting is exact and a pass never inherits a ticket from the one before it.</param>
/// <param name="Transactions">Everything above plus the manifest. The run as it goes when nothing is retried — what a person is shown, because inflating the transaction count on screen would mislead the other way.</param>
/// <param name="FeeDropsPerTransaction">The node's price at the moment of the estimate.</param>
/// <param name="FeeDropsTotal">That price across <see cref="Transactions"/>: the run as it goes, nothing retried.</param>
/// <param name="MaxAttempts">How many times one memo may be attempted before the run is called off. Defaults to <see cref="TicketStrategyOptions.MaxAttemptsPerMemo"/>'s own default; the two are independent knobs, kept in step by convention rather than by a shared constant.</param>
/// <param name="FeeDropsWorstCase">The fee if every memo needed every one of <see cref="MaxAttempts"/> tries, including a remint each retry cycle: <see cref="Transactions"/> times <see cref="MaxAttempts"/> times <see cref="FeeDropsPerTransaction"/>. What <see cref="RequiredDrops"/> is actually built from.</param>
/// <param name="PeakReserveDrops">What live tickets lock up at the worst moment: memos, capped at the protocol's own live-ticket ceiling (<see cref="TicketStrategy.MaxLiveTickets"/>), not the caller's batch size. A batch is what is normally live, but a ticket dispatched whose submission never reaches a ledger stays live for the run's lifetime, so the honest worst case is the protocol's cap.</param>
/// <param name="BaseReserveDrops">What the account must hold to exist at all.</param>
/// <param name="OwnerReserveDrops">What one owned object locks, as the node reports it now.</param>
/// <param name="RequiredDrops">The worst case: <see cref="FeeDropsWorstCase"/>, <see cref="PeakReserveDrops"/> and <see cref="BaseReserveDrops"/>, added. An optimistic number here would let a run start that cannot finish.</param>
/// <param name="BalanceDrops">What the account holds now.</param>
/// <param name="OwnerCount">Ledger objects the account already owns, each locking one <see cref="OwnerReserveDrops"/> out of that balance. Trust lines, offers — and the live tickets an interrupted run left behind, which is why a resume needs this most.</param>
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
    long OwnerReserveDrops,
    long RequiredDrops,
    long BalanceDrops,
    int OwnerCount)
{
    /// <summary>What the objects the account already owns lock out of its balance.</summary>
    public long LockedReserveDrops => OwnerCount * OwnerReserveDrops;

    /// <summary>
    /// What the account has to meet <see cref="RequiredDrops"/> with: the balance less what its
    /// existing objects lock. The run's own base reserve is not subtracted here because
    /// <see cref="RequiredDrops"/> already carries it, and subtracting it twice would print a
    /// figure no caller could compare against the one beside it. Shown rather than only used,
    /// because "you hold 21.5 XRP and 1 of it is locked" is what explains the refusal below.
    /// </summary>
    public long SpendableDrops => BalanceDrops - LockedReserveDrops;

    /// <summary>
    /// Whether the account can carry the run, judged against what it may actually spend rather
    /// than against the raw balance. <see cref="RequiredDrops"/> already carries this run's own
    /// base and peak-ticket reserves; what it cannot know is the reserve the account was already
    /// under before the run started, and a balance gate that ignores that passes an account whose
    /// first TicketCreate is answered <c>tecINSUFFICIENT_RESERVE</c> with chunks already paid for.
    /// </summary>
    public bool BalanceCovers => SpendableDrops >= RequiredDrops;
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

        // Three batching passes, not one: XrplFileWriter calls SubmitAllAsync for the chunks, then
        // for the block list when there is one, then for the manifest. Minting is exact, so every
        // pass starts with an empty queue and mints for itself — counting one pass over all the
        // memos reports fewer mints and fewer transactions than the run actually makes.
        int mints = Batches(chunks, batch) + Batches(lists, batch) + Batches(1, batch);
        long transactions = memos + mints;

        NetworkFees fees = await source.FeesAsync(cancellationToken).ConfigureAwait(false);

        // Balance and owner count from one answer, so the coverage check below describes one
        // ledger. What the account already owns is not decoration: every one of those objects
        // locks an owner reserve the run cannot spend, and a run approved against the raw balance
        // is a run whose first mint is answered tecINSUFFICIENT_RESERVE after the chunks are paid.
        AccountState state = await source.AccountStateAsync(account, cancellationToken).ConfigureAwait(false);

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
            fees.OpenLedgerFeeDrops, feeTotal, maxAttempts, feeWorstCase, peakReserve,
            fees.BaseReserveDrops, fees.OwnerReserveDrops, required, state.BalanceDrops, state.OwnerCount);
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
                $"the run needs {estimate.RequiredDrops} drops and the account holds {estimate.BalanceDrops}, "
                + $"of which {estimate.LockedReserveDrops} is locked by the {estimate.OwnerCount} objects it already owns");
        }
    }

    /// <summary>
    /// How many mints one batching pass over this many memos needs. A pass with no memos at all —
    /// a single-block file's absent block list — mints nothing.
    /// </summary>
    private static int Batches(long memos, int batch) => (int)((memos + batch - 1) / batch);

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
