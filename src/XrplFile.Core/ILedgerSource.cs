namespace XrplFile;

/// <summary>The three questions a reader asks a node. Everything else in the library is pure.</summary>
public interface ILedgerSource
{
    /// <summary>The transaction with this hash, or null when the node does not know it.</summary>
    Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken);

    /// <summary>
    /// Every transaction of the account whose ledger lies in [fromLedger, toLedger], in canonical
    /// order — ascending when forward, descending otherwise.
    /// </summary>
    IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken);

    /// <summary>The oldest ledger the node still holds, or null when it does not say.</summary>
    Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken);

    /// <summary>What the network charges and reserves right now.</summary>
    Task<NetworkFees> FeesAsync(CancellationToken cancellationToken);

    /// <summary>What the account holds, in drops. An account the node does not know holds nothing.</summary>
    Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken);

    /// <summary>The index of the latest validated ledger.</summary>
    Task<long> CurrentLedgerAsync(CancellationToken cancellationToken);
}
