namespace XrplFile;

/// <summary>
/// A round's final sweep (spec 4.1 step 8). It reads the writer's and the payer's history over the
/// round's ledgers until both cover the account's sequence interval for the round (spec B30), checks
/// that nothing in it is foreign (spec B12), and only then decides which memos landed (spec B28) and
/// what the payer paid (spec B29).
/// </summary>
internal static class BatchSweep
{
    /// <param name="known">
    /// The writer's records the round's progress sweeps already read, or null. Only the ledgers from
    /// <see cref="KnownHistory.ReadFrom"/> on are read again: on a 7 MB run on testnet, reading the
    /// writer's whole round again took 14 s of the run's 114.
    /// </param>
    public static async Task<RoundResult> FinalAsync(
        BatchContext context, RoundMarkers markers,
        IReadOnlyList<BatchCandidate> candidates, LedgerSnapshot start, LedgerSnapshot settled, KnownHistory? known,
        CancellationToken cancellationToken)
    {
        if (settled.LedgerIndex <= start.LedgerIndex) return RoundResult.Empty;

        string writer = context.Writer.Account;
        string payer = context.Payer.Account;
        DateTimeOffset giveUp = context.Options.Time.GetUtcNow() + context.Options.HistoryLagLimit;
        bool reuse = known is not null && known.ReadFrom > start.LedgerIndex + 1;

        while (true)
        {
            long writerFrom = reuse ? Math.Min(known!.ReadFrom, settled.LedgerIndex) : start.LedgerIndex + 1;
            IReadOnlyList<SweepRecord> read = await BatchReads.RetryAsync(
                token => CompactHistoryAsync(context.Source, writer, writerFrom, settled.LedgerIndex, markers, token),
                context.Options, cancellationToken).ConfigureAwait(false);
            List<SweepRecord> writerRecords = Own(
                reuse ? [.. known!.Records.Where(tx => tx.LedgerIndex > start.LedgerIndex && tx.LedgerIndex < writerFrom), .. read] : read,
                writer);
            List<SweepRecord> payerRecords = Own(await BatchReads.RetryAsync(
                token => CompactHistoryAsync(context.Source, payer, start.LedgerIndex + 1, settled.LedgerIndex, null, token),
                context.Options, cancellationToken).ConfigureAwait(false), payer);

            if (Covers(writerRecords, start.SequenceOf(writer), settled.SequenceOf(writer))
                && Covers(payerRecords, start.SequenceOf(payer), settled.SequenceOf(payer)))
            {
                RoundIdentity identity = new(candidates);
                RequireIdentity(context, identity, writerRecords, payerRecords);
                return Outcome(identity, writerRecords, payerRecords);
            }

            // A gap below where the progress sweeps stopped would never be read again: account_tx that
            // lagged behind one of them is read over the whole round from here on, as without them.
            reuse = false;

            if (context.Options.Time.GetUtcNow() >= giveUp)
            {
                throw new XrplFileException(XrplFileError.NodeLacksHistory,
                    $"account_tx did not cover ledgers {start.LedgerIndex + 1}–{settled.LedgerIndex} for the writer and the payer "
                    + $"within {context.Options.HistoryLagLimit}");
            }

            await Task.Delay(context.Options.ChainCheck, context.Options.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the records account for every sequence in [<paramref name="from"/>, <paramref name="to"/>):
    /// a record with a non-zero sequence covers it, and a TicketCreate also covers the sequences its
    /// tickets took. A ticketed record covers nothing. Records of any result count (spec B30).
    /// </summary>
    internal static bool Covers(IEnumerable<LedgerTransaction> records, uint from, uint to) =>
        CoversCore(records.Select(tx => (tx.Sequence, tx.TransactionType, tx.TicketCount)), from, to);

    /// <inheritdoc cref="Covers(IEnumerable{LedgerTransaction}, uint, uint)"/>
    internal static bool Covers(IEnumerable<SweepRecord> records, uint from, uint to) =>
        CoversCore(records.Select(tx => (tx.Sequence, tx.TransactionType, tx.TicketCount)), from, to);

    private static bool CoversCore(IEnumerable<(uint Sequence, string? TransactionType, uint? TicketCount)> records, uint from, uint to)
    {
        if (to <= from) return true;

        HashSet<uint> covered = [];

        foreach ((uint sequence, string? type, uint? ticketCount) in records)
        {
            if (sequence == 0) continue;

            covered.Add(sequence);

            if (string.Equals(type, "TicketCreate", StringComparison.Ordinal) && ticketCount is { } count)
            {
                for (uint ticket = 1; ticket <= count; ticket++) covered.Add(sequence + ticket);
            }
        }

        for (uint sequence = from; sequence < to; sequence++)
        {
            if (!covered.Contains(sequence)) return false;
        }

        return true;
    }

    /// <summary>
    /// A transaction of the payer that is not one of the round's outers, or of the writer that is
    /// neither an inner of those outers nor the round's solo, fails the run (spec B12).
    /// </summary>
    private static void RequireIdentity(
        BatchContext context, RoundIdentity identity,
        IReadOnlyList<SweepRecord> writerRecords, IReadOnlyList<SweepRecord> payerRecords)
    {
        foreach (SweepRecord tx in payerRecords)
        {
            if (identity.IsOuter(tx.Hash)) continue;

            throw new XrplFileException(XrplFileError.AccountInUse,
                $"the payer {context.Payer.Account} sent {tx.TransactionType ?? "a transaction"} {tx.Hash} in ledger {tx.LedgerIndex} "
                + "during the publication", role: AccountRole.Payer);
        }

        foreach (SweepRecord tx in writerRecords)
        {
            if (identity.IsOurs(tx)) continue;

            throw new XrplFileException(XrplFileError.AccountInUse,
                $"the writer {context.Writer.Account} sent {tx.TransactionType ?? "a transaction"} {tx.Hash} in ledger {tx.LedgerIndex} "
                + "during the publication", role: AccountRole.Writer);
        }
    }

    internal static RoundResult Outcome(IReadOnlyList<BatchCandidate> candidates, IReadOnlyList<SweepRecord> writerRecords, IReadOnlyList<SweepRecord> payerRecords) =>
        Outcome(new RoundIdentity(candidates), writerRecords, payerRecords);

    private static RoundResult Outcome(RoundIdentity identity, IReadOnlyList<SweepRecord> writerRecords, IReadOnlyList<SweepRecord> payerRecords)
    {
        Dictionary<int, LandedInner> landed = [];

        foreach (SweepRecord tx in writerRecords)
        {
            if (!tx.Succeeded || !tx.Validated || !identity.IsOurs(tx)) continue;

            // Matched on marker and data (spec B28): the verdict was taken when the record was read.
            if (tx.CallIndex < 0 || tx.Verdict != DataVerdict.Matches) continue;

            landed.TryAdd(tx.CallIndex, new LandedInner(tx.Hash, tx.LedgerIndex));
        }

        long fee = 0;
        bool known = true;

        foreach (SweepRecord tx in payerRecords)
        {
            if (!tx.Validated || !identity.IsOuter(tx.Hash)) continue;

            if (tx.Fee is { } drops) fee += drops;
            else known = false;
        }

        return new RoundResult(landed, fee, known);
    }

    /// <summary>
    /// An account's records over [<paramref name="from"/>, <paramref name="to"/>], classified as they
    /// arrive (spec S6): no page's memo data outlives the page.
    /// </summary>
    internal static async Task<IReadOnlyList<SweepRecord>> CompactHistoryAsync(
        ILedgerSource source, string account, long from, long to, RoundMarkers? markers, CancellationToken cancellationToken)
    {
        List<SweepRecord> records = [];

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(account, from, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            records.Add(SweepRecord.From(tx, markers));
        }

        return records;
    }

    internal static async Task<IReadOnlyList<LedgerTransaction>> HistoryAsync(
        ILedgerSource source, string account, long from, long to, CancellationToken cancellationToken)
    {
        List<LedgerTransaction> records = [];

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(account, from, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            records.Add(tx);
        }

        return records;
    }

    /// <summary>
    /// What "ours" means for one round, in one place for both the identity check (spec B12) and the
    /// outcome (spec B28): the hashes of its outers and of its solo.
    /// </summary>
    private sealed class RoundIdentity
    {
        private readonly HashSet<string> _outers;
        private readonly string? _solo;

        public RoundIdentity(IReadOnlyList<BatchCandidate> candidates)
        {
            _outers = new HashSet<string>(candidates.Where(c => c.IsOuter).Select(c => c.Hash), StringComparer.OrdinalIgnoreCase);
            _solo = candidates.FirstOrDefault(c => !c.IsOuter)?.Hash;
        }

        public bool IsOuter(string hash) => _outers.Contains(hash);

        /// <summary>A writer's record that is an inner of one of the round's outers, or the round's solo.</summary>
        public bool IsOurs(SweepRecord tx) =>
            tx.ParentBatchID is { } parent
                ? _outers.Contains(parent)
                : _solo is not null && string.Equals(tx.Hash, _solo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The writer's records a round's progress sweeps read, in ledger order: complete as account_tx
    /// gave them below <paramref name="ReadFrom"/>, the ledger the next read would have started at.
    /// That rests on account_tx indexing ledgers in order. A gap that breaks it is caught by B30's
    /// coverage when the missing record took a sequence, and the sweep then reads the round in full;
    /// a ticketed one (sequence 0) would go unseen by B12, as it would in a full read made too early.
    /// </summary>
    internal sealed record KnownHistory(IReadOnlyList<SweepRecord> Records, long ReadFrom);

    /// <summary>The records the account sent: account_tx also returns what others sent to it, which is not "of" it.</summary>
    private static List<SweepRecord> Own(IReadOnlyList<SweepRecord> records, string account) =>
        [.. records.Where(tx => string.Equals(tx.Account, account, StringComparison.Ordinal))];
}
