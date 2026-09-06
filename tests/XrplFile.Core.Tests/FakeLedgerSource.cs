using System.Runtime.CompilerServices;

namespace XrplFile.Tests;

/// <summary>An in-memory ledger: insertion order is canonical order within a ledger.</summary>
public sealed class FakeLedgerSource : ILedgerSource
{
    private readonly List<LedgerTransaction> _transactions = [];

    public long? EarliestLedger { get; set; } = 1;

    public int AccountTransactionsCalls { get; private set; }

    public FakeLedgerSource Add(LedgerTransaction tx)
    {
        _transactions.Add(tx);
        return this;
    }

    public FakeLedgerSource AddRange(IEnumerable<LedgerTransaction> transactions)
    {
        _transactions.AddRange(transactions);
        return this;
    }

    public FakeLedgerSource Remove(Func<LedgerTransaction, bool> which)
    {
        _transactions.RemoveAll(tx => which(tx));
        return this;
    }

    public FakeLedgerSource Replace(Func<LedgerTransaction, bool> which, Func<LedgerTransaction, LedgerTransaction> with)
    {
        for (int i = 0; i < _transactions.Count; i++)
        {
            if (which(_transactions[i])) _transactions[i] = with(_transactions[i]);
        }

        return this;
    }

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) =>
        Task.FromResult(_transactions.FirstOrDefault(tx => string.Equals(tx.Hash, hash, StringComparison.OrdinalIgnoreCase)));

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AccountTransactionsCalls++;

        IEnumerable<(LedgerTransaction Tx, int Position)> matching = _transactions
            .Select((tx, position) => (Tx: tx, Position: position))
            .Where(pair => string.Equals(pair.Tx.Account, account, StringComparison.Ordinal)
                && pair.Tx.LedgerIndex >= fromLedger && pair.Tx.LedgerIndex <= toLedger);

        IEnumerable<LedgerTransaction> ordered = forward
            ? matching.OrderBy(pair => pair.Tx.LedgerIndex).ThenBy(pair => pair.Position).Select(pair => pair.Tx)
            : matching.OrderByDescending(pair => pair.Tx.LedgerIndex).ThenByDescending(pair => pair.Position).Select(pair => pair.Tx);

        foreach (LedgerTransaction tx in ordered)
        {
            await Task.Yield();
            yield return tx;
        }
    }

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => Task.FromResult(EarliestLedger);
}
