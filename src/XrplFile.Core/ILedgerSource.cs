namespace XrplFile;

/// <summary>
/// What one <c>account_info</c> answer says about an account, read together so the two halves
/// describe the same ledger.
/// </summary>
/// <param name="BalanceDrops">What the account holds. An account the node does not know holds nothing.</param>
/// <param name="OwnerCount">
/// How many ledger objects it owns, each locking one owner reserve out of that balance. Tickets
/// are among them, which is what makes this number matter to anything paying money back off an
/// account a publication has used.
/// </param>
/// <param name="TicketCount">
/// How many of those objects are live tickets — <c>AccountRoot</c>'s own field, absent when there
/// are none. The protocol caps it at <see cref="TicketStrategy.MaxLiveTickets"/>, and a strategy
/// that minted against its own bookkeeping instead of this number asked for tickets an account
/// carrying an earlier run's leftovers had no room for.
/// </param>
public sealed record AccountState(long BalanceDrops, int OwnerCount, int TicketCount)
{
    /// <summary>An account the node has never heard of: it holds nothing and owns nothing.</summary>
    public static AccountState Absent { get; } = new(0, 0, 0);
}

/// <summary>The questions a reader asks a node. Everything else in the library is pure.</summary>
public interface ILedgerSource
{
    /// <summary>The transaction with this hash, or null when the node does not know it.</summary>
    Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken);

    /// <summary>
    /// Every transaction of the account whose ledger lies in [fromLedger, toLedger], in canonical
    /// order — ascending when forward, descending otherwise.
    ///
    /// An account the node does not know has no transactions and answers with none. That is the
    /// state an account nobody has funded yet is in, and it is an answer rather than a failure —
    /// the same rule <see cref="BalanceDropsAsync"/> follows for the same reason.
    /// </summary>
    IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken);

    /// <summary>The oldest ledger the node still holds, or null when it does not say.</summary>
    Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken);

    /// <summary>What the network charges and reserves right now.</summary>
    Task<NetworkFees> FeesAsync(CancellationToken cancellationToken);

    /// <summary>What the account holds, in drops. An account the node does not know holds nothing.</summary>
    Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken);

    /// <summary>
    /// What the account holds and what it owns, from one answer. Anything working out how much of
    /// a balance may actually be spent needs both, and needs them from the same read.
    /// </summary>
    Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken);

    /// <summary>The index of the latest validated ledger.</summary>
    Task<long> CurrentLedgerAsync(CancellationToken cancellationToken);
}
