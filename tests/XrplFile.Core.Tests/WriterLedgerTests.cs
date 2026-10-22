using Xunit;

namespace XrplFile.Tests;

/// <summary>Spec question 23: a strategy that says where each memo landed spares the writer a lookup per memo.</summary>
public class WriterLedgerTests
{
    [Fact]
    public async Task A_strategy_that_reports_ledgers_spares_the_writer_a_lookup_per_chunk()
    {
        BatchTestNode node = new();
        LookupRecordingSource source = new(node.Ledger);
        await using BatchStrategy strategy = node.Strategy();
        XrplFileWriter writer = new(strategy, source, new WriterOptions { ReadBack = false });

        PublishResult result = await writer.PublishAsync(
            VectorStream.Bytes("ledgers", 20_000), "text/plain", null, node.Writer.Account, "testnet", null, null,
            TestContext.Current.CancellationToken);

        Assert.Empty(source.LookedUp.Intersect(result.ChunkHashes, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Forwards every read and remembers which hashes were looked up one by one.</summary>
    private sealed class LookupRecordingSource(ILedgerSource inner) : ILedgerSource
    {
        private readonly List<string> _lookedUp = [];

        public IReadOnlyList<string> LookedUp
        {
            get { lock (_lookedUp) return [.. _lookedUp]; }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken)
        {
            lock (_lookedUp) _lookedUp.Add(hash);
            return inner.TransactionAsync(hash, cancellationToken);
        }

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken) =>
            inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) => inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) => inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) => inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) => inner.DestinationAsync(account, cancellationToken);

        public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken) => inner.SnapshotAsync(accounts, cancellationToken);

        public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => inner.OpenLedgerFeeAsync(cancellationToken);

        public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) => inner.IsAmendmentEnabledAsync(name, cancellationToken);
    }
}
