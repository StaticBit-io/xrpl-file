using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Spec B24: a read the node left unanswered costs the connection, and the next request goes over a
/// new one. The read-back is the writer's own reading, between paid passes, so it must take that
/// next request rather than fail a run whose chunks have all landed.
/// </summary>
public class WriterReadBackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_read_back_read_the_node_did_not_answer_is_taken_again()
    {
        BatchTestNode node = new();
        UnansweredOnceSource source = new(node.Ledger);
        await using BatchStrategy batch = node.Strategy();
        ArmingStrategy strategy = new(batch, source);
        byte[] file = VectorStream.Bytes("read-back-retry", 20_000);

        PublishResult result = await new XrplFileWriter(strategy, source).PublishAsync(
            file, "application/octet-stream", null, node.Writer.Account, "testnet", null, null, Token);

        Assert.True(source.Refused);
        Assert.Equal(file, (await new XrplFileReader(node.Ledger).ReadAsync(result.Link, Token)).Bytes);
    }

    [Fact]
    public async Task A_read_back_whose_node_never_answers_still_fails_as_unreachable()
    {
        BatchTestNode node = new();
        UnansweredOnceSource source = new(node.Ledger) { Always = true };
        await using BatchStrategy batch = node.Strategy();
        ArmingStrategy strategy = new(batch, source);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => new XrplFileWriter(strategy, source).PublishAsync(
            VectorStream.Bytes("read-back-down", 20_000), "application/octet-stream", null, node.Writer.Account, "testnet", null, null, Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    /// <summary>Arms the source once the chunks have landed: every account_tx after that is the read-back's.</summary>
    private sealed class ArmingStrategy(ISubmissionStrategy inner, UnansweredOnceSource source) : ISubmissionStrategy
    {
        public long FeeDropsPerTransaction => inner.FeeDropsPerTransaction;

        public string Account => inner.Account;

        public int TicketMints => inner.TicketMints;

        public async Task<IReadOnlyList<SubmittedMemo>> SubmitAllAsync(
            IReadOnlyList<MemoSubmission> memos, IProgress<int>? submitted, CancellationToken cancellationToken)
        {
            IReadOnlyList<SubmittedMemo> landed = await inner.SubmitAllAsync(memos, submitted, cancellationToken);
            source.Armed = !source.Armed && memos.Count > 1;
            return landed;
        }

        public Task<TicketRelease> ReleaseLeftoverTicketsAsync(CancellationToken cancellationToken) => inner.ReleaseLeftoverTicketsAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Refuses the first account_tx once armed, the way a silent socket's request times out.</summary>
    private sealed class UnansweredOnceSource(ILedgerSource inner) : ILedgerSource
    {
        public volatile bool Armed;

        public bool Always { get; init; }

        public bool Refused { get; private set; }

        public IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(string account, long fromLedger, long toLedger, bool forward, CancellationToken cancellationToken)
        {
            if (Armed && (Always || !Refused))
            {
                Refused = true;
                throw new XrplFileException(XrplFileError.NodeUnreachable, "fake: the node did not answer account_tx");
            }

            return inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken);
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) => inner.TransactionAsync(hash, cancellationToken);

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
