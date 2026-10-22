using Xrpl.Wallet;
using XrplFile.Tests;
using Xunit;

namespace XrplFile.App.Core.Tests;

public sealed class PublishPolicyTests
{
    private static PublicationEstimate Estimate(long chunks, int lists, int mints = 0) =>
        new(
            Chunks: chunks,
            Blocks: 1,
            Lists: lists,
            Mints: mints,
            Transactions: chunks + lists + 1 + mints,
            FeeDropsPerTransaction: 12,
            FeeDropsTotal: (chunks + lists + 1 + mints) * 12,
            MaxAttempts: 3,
            FeeDropsWorstCase: (chunks + lists + 1 + mints) * 3 * 12,
            PeakReserveDrops: 0,
            BaseReserveDrops: 1_000_000,
            OwnerReserveDrops: 200_000,
            RequiredDrops: 2_000_000,
            BalanceDrops: 100_000_000,
            OwnerCount: 0);

    [Fact]
    public void The_memo_count_excludes_the_mints()
    {
        // 90 chunks, 1 list, 1 manifest = 92 memos, whatever the ticket machinery would add on top.
        Assert.Equal(92, PublishPolicy.MemoCount(Estimate(chunks: 90, lists: 1, mints: 40)));
    }

    [Fact]
    public void A_run_below_the_threshold_uses_sequence()
    {
        Assert.False(PublishPolicy.UsesTickets(Estimate(chunks: 97, lists: 1)));
    }

    [Fact]
    public void A_run_at_the_threshold_uses_tickets()
    {
        Assert.Equal(100, PublishPolicy.MemoCount(Estimate(chunks: 98, lists: 1)));
        Assert.True(PublishPolicy.UsesTickets(Estimate(chunks: 98, lists: 1)));
    }

    [Fact]
    public void A_run_above_the_threshold_uses_tickets()
    {
        Assert.True(PublishPolicy.UsesTickets(Estimate(chunks: 5000, lists: 5)));
    }

    [Fact]
    public void A_run_that_would_use_mints_to_cross_the_threshold_still_uses_sequence()
    {
        // 80 memos, and enough mints that PublicationEstimate.Transactions is past a hundred.
        PublicationEstimate estimate = Estimate(chunks: 78, lists: 1, mints: 30);

        Assert.True(estimate.Transactions > PublishPolicy.TicketThreshold);
        Assert.False(PublishPolicy.UsesTickets(estimate));
    }

    [Fact]
    public async Task A_sequence_strategy_built_by_the_policy_tidies_up_leftovers()
    {
        FakeConnectionFactory connections = new();
        XrplWallet wallet = XrplWallet.Generate();
        connections.Sink.LeaveTickets(wallet.ClassicAddress, 2, marked: true);
        ILedgerConnection connection = await connections.ConnectAsync("1", TestContext.Current.CancellationToken);

        await using ISubmissionStrategy strategy = PublishPolicy.Create(Estimate(chunks: 3, lists: 0), connection, new WalletSigner(wallet));

        Assert.IsType<SequenceStrategy>(strategy);
        Assert.Equal(2, (await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken)).Released);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(98_000L)]
    [InlineData(98_001L)]
    [InlineData(99_000L)]
    [InlineData(1_024_001L)]
    [InlineData(3_145_728L)]
    public async Task The_size_alone_decides_tickets_exactly_as_the_estimate_does(long fileBytes)
    {
        FakeLedgerSource node = new();
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes, PublishPolicy.TicketsPerBatch, TestContext.Current.CancellationToken);

        Assert.Equal(PublishPolicy.UsesTickets(estimate), PublishPolicy.UsesTickets(fileBytes));
    }

    [Fact]
    public void The_threshold_falls_between_98000_and_98001_bytes()
    {
        Assert.False(PublishPolicy.UsesTickets(98_000L));
        Assert.True(PublishPolicy.UsesTickets(98_001L));
    }

    [Fact]
    public async Task A_batch_strategy_is_built_from_the_sessions_budget_with_both_signers()
    {
        await using ILedgerConnection connection = await new FakeConnectionFactory().ConnectAsync("testnet", TestContext.Current.CancellationToken);
        ITransactionSigner writer = new WalletSigner(XrplWallet.Generate());
        ITransactionSigner payer = new WalletSigner(XrplWallet.Generate());

        BatchStrategy strategy = PublishPolicy.CreateBatch(new BatchBudget(15, 30, 1000, 1000), new NoFence(), connection, writer, payer);

        Assert.Equal(15, strategy.FeeDropsPerTransaction);
        Assert.Equal(writer.Account, strategy.Account);
        Assert.Equal(payer.Account, strategy.Payer);
    }

    [Theory]
    [InlineData("tickets", typeof(TicketStrategy))]
    [InlineData("sequence", typeof(SequenceStrategy))]
    public async Task A_run_resumes_on_the_strategy_it_recorded_whatever_its_size(string recorded, Type expected)
    {
        FakeConnectionFactory connections = new();
        await using ILedgerConnection connection = await connections.ConnectAsync("testnet", TestContext.Current.CancellationToken);
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            connections.Ledger, "rWriter", 2000, PublishPolicy.TicketsPerBatch, TestContext.Current.CancellationToken);

        ISubmissionStrategy strategy = PublishPolicy.CreateFor(recorded, estimate, connection, new WalletSigner(XrplWallet.Generate()));

        Assert.IsType(expected, strategy);
    }

    [Fact]
    public async Task A_run_that_recorded_no_strategy_resumes_on_the_existing_policy()
    {
        FakeConnectionFactory connections = new();
        await using ILedgerConnection connection = await connections.ConnectAsync("testnet", TestContext.Current.CancellationToken);
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            connections.Ledger, "rWriter", 2000, PublishPolicy.TicketsPerBatch, TestContext.Current.CancellationToken);

        Assert.IsType<SequenceStrategy>(PublishPolicy.CreateFor(null, estimate, connection, new WalletSigner(XrplWallet.Generate())));
    }

    private sealed class NoFence : IBatchFenceRecorder
    {
        public Task RecordFenceAsync(long deadline, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
