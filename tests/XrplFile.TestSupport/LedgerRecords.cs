namespace XrplFile.Tests;

public static class LedgerRecords
{
    /// <summary>
    /// The first successful copy of every chunk the writer carries from <paramref name="fromLedger"/>,
    /// in chunk order — the copy a reader takes. What tests read now that reports carry no chunk hashes.
    /// </summary>
    public static async Task<IReadOnlyList<LedgerTransaction>> ChunksAsync(
        ILedgerSource source, string writer, long fromLedger, CancellationToken cancellationToken)
    {
        SortedDictionary<long, LedgerTransaction> first = [];
        long to = await source.CurrentLedgerAsync(cancellationToken).ConfigureAwait(false);

        await foreach (LedgerTransaction tx in source.AccountTransactionsAsync(writer, fromLedger, to, forward: true, cancellationToken).ConfigureAwait(false))
        {
            if (tx.Succeeded && tx.Validated && string.Equals(tx.Account, writer, StringComparison.Ordinal)
                && MemoLayout.ChunkIndexOf(tx.MemoTypeHex) is { } index)
            {
                first.TryAdd(index, tx);
            }
        }

        return [.. first.Values];
    }
}
