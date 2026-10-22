namespace XrplFile;

/// <summary>
/// A round's final sweep (spec 4.1 step 8). It reads the writer's and the payer's history over the
/// round's ledgers until both cover the account's sequence interval for the round (spec B30), checks
/// that nothing in it is foreign (spec B12), and only then decides which memos landed (spec B28) and
/// what the payer paid (spec B29).
/// </summary>
internal static class BatchSweep
{
    public static async Task<RoundResult> FinalAsync(
        BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending,
        IReadOnlyList<BatchCandidate> candidates, LedgerSnapshot start, LedgerSnapshot settled, CancellationToken cancellationToken)
    {
        if (settled.LedgerIndex <= start.LedgerIndex) return RoundResult.Empty;

        string writer = context.Writer.Account;
        string payer = context.Payer.Account;
        DateTimeOffset giveUp = context.Options.Time.GetUtcNow() + context.Options.HistoryLagLimit;

        while (true)
        {
            List<LedgerTransaction> writerRecords = Own(await BatchReads.RetryAsync(
                token => HistoryAsync(context.Source, writer, start.LedgerIndex + 1, settled.LedgerIndex, token),
                context.Options, cancellationToken).ConfigureAwait(false), writer);
            List<LedgerTransaction> payerRecords = Own(await BatchReads.RetryAsync(
                token => HistoryAsync(context.Source, payer, start.LedgerIndex + 1, settled.LedgerIndex, token),
                context.Options, cancellationToken).ConfigureAwait(false), payer);

            if (Covers(writerRecords, start.SequenceOf(writer), settled.SequenceOf(writer))
                && Covers(payerRecords, start.SequenceOf(payer), settled.SequenceOf(payer)))
            {
                RoundIdentity identity = new(candidates);
                RequireIdentity(context, identity, writerRecords, payerRecords);
                return Outcome(memos, pending, identity, writerRecords, payerRecords);
            }

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
    internal static bool Covers(IEnumerable<LedgerTransaction> records, uint from, uint to)
    {
        if (to <= from) return true;

        HashSet<uint> covered = [];

        foreach (LedgerTransaction tx in records)
        {
            if (tx.Sequence == 0) continue;

            covered.Add(tx.Sequence);

            if (string.Equals(tx.TransactionType, "TicketCreate", StringComparison.Ordinal) && tx.TicketCount is { } count)
            {
                for (uint ticket = 1; ticket <= count; ticket++) covered.Add(tx.Sequence + ticket);
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
        IReadOnlyList<LedgerTransaction> writerRecords, IReadOnlyList<LedgerTransaction> payerRecords)
    {
        foreach (LedgerTransaction tx in payerRecords)
        {
            if (identity.IsOuter(tx.Hash)) continue;

            throw new XrplFileException(XrplFileError.AccountInUse,
                $"the payer {context.Payer.Account} sent {tx.TransactionType ?? "a transaction"} {tx.Hash} in ledger {tx.LedgerIndex} "
                + "during the publication", role: AccountRole.Payer);
        }

        foreach (LedgerTransaction tx in writerRecords)
        {
            if (identity.IsOurs(tx)) continue;

            throw new XrplFileException(XrplFileError.AccountInUse,
                $"the writer {context.Writer.Account} sent {tx.TransactionType ?? "a transaction"} {tx.Hash} in ledger {tx.LedgerIndex} "
                + "during the publication", role: AccountRole.Writer);
        }
    }

    internal static RoundResult Outcome(
        BatchContext context, IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, IReadOnlyList<BatchCandidate> candidates,
        IReadOnlyList<LedgerTransaction> writerRecords, IReadOnlyList<LedgerTransaction> payerRecords) =>
        Outcome(memos, pending, new RoundIdentity(candidates), writerRecords, payerRecords);

    private static RoundResult Outcome(
        IReadOnlyList<MemoSubmission> memos, IReadOnlyList<int> pending, RoundIdentity identity,
        IReadOnlyList<LedgerTransaction> writerRecords, IReadOnlyList<LedgerTransaction> payerRecords)
    {
        Dictionary<string, int> byMarker = new(StringComparer.OrdinalIgnoreCase);
        foreach (int index in pending) byMarker[memos[index].MemoTypeHex] = index;

        Dictionary<int, LandedInner> landed = [];

        foreach (LedgerTransaction tx in writerRecords)
        {
            if (!tx.Succeeded || !tx.Validated) continue;

            if (!identity.IsOurs(tx) || tx.MemoTypeHex is null || !byMarker.TryGetValue(tx.MemoTypeHex, out int index)) continue;
            if (!string.Equals(tx.MemoDataHex, memos[index].MemoDataHex, StringComparison.OrdinalIgnoreCase)) continue;

            landed.TryAdd(index, new LandedInner(tx.Hash, tx.LedgerIndex));
        }

        long fee = 0;
        bool known = true;

        foreach (LedgerTransaction tx in payerRecords)
        {
            if (!tx.Validated || !identity.IsOuter(tx.Hash)) continue;

            if (tx.Fee is { } drops) fee += drops;
            else known = false;
        }

        return new RoundResult(landed, fee, known);
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
        public bool IsOurs(LedgerTransaction tx) =>
            tx.ParentBatchID is { } parent
                ? _outers.Contains(parent)
                : _solo is not null && string.Equals(tx.Hash, _solo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The records the account sent: account_tx also returns what others sent to it, which is not "of" it.</summary>
    private static List<LedgerTransaction> Own(IReadOnlyList<LedgerTransaction> records, string account) =>
        [.. records.Where(tx => string.Equals(tx.Account, account, StringComparison.Ordinal))];
}
