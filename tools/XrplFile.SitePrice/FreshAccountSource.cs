namespace XrplFile.SitePrice;

/// <summary>
/// The network as a publication on brand-new accounts sees it: fees, reserves and amendments come
/// from a real node, while every account is one nobody has funded yet. That is what a throwaway
/// writer and a payer-less estimate look like before the first transaction, and it keeps the price
/// table from depending on whoever happens to be using a real account today.
/// </summary>
/// <remarks>
/// Only what <see cref="PublicationSession.EstimateAsync(ILedgerSource, string, string, long, SessionOptions, CancellationToken)"/>
/// and <see cref="PublicationEstimator.EstimateAsync"/> ask is answered; everything else throws, so a
/// change in the estimator that starts asking for more fails loudly instead of pricing from a guess.
/// </remarks>
internal sealed class FreshAccountSource(ILedgerSource network) : ILedgerSource
{
    public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => network.FeesAsync(cancellationToken);

    public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => network.OpenLedgerFeeAsync(cancellationToken);

    public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) =>
        network.IsAmendmentEnabledAsync(name, cancellationToken);

    public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult(AccountState.Absent);

    // The estimator looks for this library's leftover tickets even on the ticket path; a new account has none.
    public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LiveTicket>>([]);

    public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => throw Unsupported();

    public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => throw Unsupported();

    public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => throw Unsupported();

    public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => throw Unsupported();

    public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => throw Unsupported();

    private static NotSupportedException Unsupported() => new($"{nameof(FreshAccountSource)} answers only fee, amendment and new-account questions");
}
