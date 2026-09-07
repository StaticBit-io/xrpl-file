using System.Runtime.CompilerServices;

namespace XrplFile.Tests;

/// <summary>One recorded call to <see cref="ILedgerSource.AccountTransactionsAsync"/>, in call order.</summary>
public sealed record AccountTransactionsCall(string Account, long FromLedger, long ToLedger, bool Forward);

/// <summary>
/// An in-memory ledger: insertion order is canonical order within a ledger.
///
/// Everything it holds is guarded, because the sink writes into it from whichever task is
/// submitting and a sweep reads it at the same time. A sweep enumerates a snapshot taken when it
/// starts, which is also what a node's account_tx answer is.
/// </summary>
public sealed class FakeLedgerSource : ILedgerSource
{
    private readonly Lock _gate = new();
    private readonly List<LedgerTransaction> _transactions = [];

    /// <summary>
    /// Transactions held back from a sweep's view: revealed once this many more
    /// AccountTransactionsAsync calls ("sweeps") have happened. Backs FakeLedgerSink's
    /// QueuedUntilSweep, which needs a landing that a sweep in progress cannot see yet.
    /// </summary>
    private readonly List<(int RemainingSweeps, LedgerTransaction Tx)> _delayed = [];

    private long _currentLedger = 1;

    public long? EarliestLedger { get; set; } = 1;

    public List<AccountTransactionsCall> Calls { get; } = [];

    public int AccountTransactionsCalls
    {
        get { lock (_gate) return Calls.Count; }
    }

    public FakeLedgerSource Add(LedgerTransaction tx)
    {
        lock (_gate)
        {
            _transactions.Add(tx);
            if (tx.LedgerIndex > _currentLedger) _currentLedger = tx.LedgerIndex;
        }

        return this;
    }

    /// <summary>
    /// Holds a transaction back from AccountTransactionsAsync until the given number of further
    /// sweeps have passed, then reveals it — a queued submission that lands only later.
    /// </summary>
    public FakeLedgerSource AddDelayed(LedgerTransaction tx, int sweepsUntilVisible)
    {
        lock (_gate) _delayed.Add((Math.Max(sweepsUntilVisible, 0), tx));

        return this;
    }

    public FakeLedgerSource AddRange(IEnumerable<LedgerTransaction> transactions)
    {
        lock (_gate) _transactions.AddRange(transactions);

        return this;
    }

    public FakeLedgerSource Remove(Func<LedgerTransaction, bool> which)
    {
        lock (_gate) _transactions.RemoveAll(tx => which(tx));

        return this;
    }

    public FakeLedgerSource Replace(Func<LedgerTransaction, bool> which, Func<LedgerTransaction, LedgerTransaction> with)
    {
        lock (_gate)
        {
            for (int i = 0; i < _transactions.Count; i++)
            {
                if (which(_transactions[i])) _transactions[i] = with(_transactions[i]);
            }
        }

        return this;
    }

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_transactions.FirstOrDefault(tx => string.Equals(tx.Hash, hash, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
        string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<LedgerTransaction> ordered;

        lock (_gate)
        {
            Calls.Add(new AccountTransactionsCall(account, fromLedger, toLedger, forward));

            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                (int remaining, LedgerTransaction tx) = _delayed[i];

                if (remaining <= 0)
                {
                    _transactions.Add(tx);
                    if (tx.LedgerIndex > _currentLedger) _currentLedger = tx.LedgerIndex;
                    _delayed.RemoveAt(i);
                }
                else
                {
                    _delayed[i] = (remaining - 1, tx);
                }
            }

            IEnumerable<(LedgerTransaction Tx, int Position)> matching = _transactions
                .Select((tx, position) => (Tx: tx, Position: position))
                .Where(pair => string.Equals(pair.Tx.Account, account, StringComparison.Ordinal)
                    && pair.Tx.LedgerIndex >= fromLedger && pair.Tx.LedgerIndex <= toLedger);

            ordered = forward
                ? [.. matching.OrderBy(pair => pair.Tx.LedgerIndex).ThenBy(pair => pair.Position).Select(pair => pair.Tx)]
                : [.. matching.OrderByDescending(pair => pair.Tx.LedgerIndex).ThenByDescending(pair => pair.Position).Select(pair => pair.Tx)];
        }

        foreach (LedgerTransaction tx in ordered)
        {
            await Task.Yield();
            yield return tx;
        }
    }

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => Task.FromResult(EarliestLedger);

    /// <summary>What this fake node charges and reserves. Ten drops, one XRP, a fifth of an XRP.</summary>
    public NetworkFees Fees { get; set; } = new(10, 1_000_000, 200_000);

    public Dictionary<string, long> Balances { get; } = [];

    public long CurrentLedger
    {
        get { lock (_gate) return _currentLedger; }
        set { lock (_gate) _currentLedger = value; }
    }

    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => Task.FromResult(Fees);

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(Balances.TryGetValue(account, out long drops) ? drops : 0);

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentLedger);
}
