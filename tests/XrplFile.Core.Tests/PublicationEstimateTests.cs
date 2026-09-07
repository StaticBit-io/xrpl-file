using Xunit;

namespace XrplFile.Tests;

public class PublicationEstimateTests
{
    private static FakeLedgerSource Node(long balanceDrops, long fee = 10, long baseReserve = 1_000_000, long ownerReserve = 200_000)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(fee, baseReserve, ownerReserve) };
        source.Balances["rWriter"] = balanceDrops;
        return source;
    }

    [Fact]
    public async Task A_one_block_file_needs_no_list_and_one_manifest()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.Chunks);
        Assert.Equal(1, estimate.Blocks);
        Assert.Equal(0, estimate.Lists);
        // Three chunks, no list, one manifest, and one mint to carry the four.
        Assert.Equal(1, estimate.Mints);
        Assert.Equal(5, estimate.Transactions);
    }

    [Fact]
    public async Task A_multi_block_file_pays_for_its_block_list_too()
    {
        // 1024001 bytes is 1025 chunks, so two blocks and one list memo.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(1025, estimate.Chunks);
        Assert.Equal(2, estimate.Blocks);
        Assert.Equal(1, estimate.Lists);
        // 1027 memos over batches of a hundred is eleven mints.
        Assert.Equal(11, estimate.Mints);
        Assert.Equal(1038, estimate.Transactions);
    }

    [Fact]
    public async Task The_fee_total_is_the_nodes_price_times_the_transactions()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000, fee: 15), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(15, estimate.FeeDropsPerTransaction);
        Assert.Equal(75, estimate.FeeDropsTotal);
    }

    [Fact]
    public async Task The_peak_reserve_is_a_batch_of_tickets_and_the_account_itself()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // Four memos, so four tickets are ever live at once, not the batch's hundred.
        Assert.Equal(4 * 200_000, estimate.PeakReserveDrops);
        Assert.Equal(1_000_000, estimate.BaseReserveDrops);
        Assert.Equal(estimate.FeeDropsTotal + estimate.PeakReserveDrops + estimate.BaseReserveDrops, estimate.RequiredDrops);
    }

    [Fact]
    public async Task A_batch_smaller_than_the_file_caps_the_peak_reserve()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // A thousand memos, but only a hundred tickets are ever live together.
        Assert.Equal(100 * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task A_balance_that_covers_the_run_says_so()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.True(estimate.BalanceCovers);
        PublicationEstimator.Require(estimate, budgetDrops: null);
    }

    [Fact]
    public async Task A_balance_that_does_not_cover_the_run_is_refused_with_both_numbers()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(1_500_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.False(estimate.BalanceCovers);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.Require(estimate, budgetDrops: null));

        Assert.Equal(XrplFileError.InsufficientBalance, error.Error);
        Assert.Contains(estimate.RequiredDrops.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("1500000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_above_the_callers_ceiling_is_refused_before_the_balance_is_blamed()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.Require(estimate, budgetDrops: 100));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
    }

    [Fact]
    public async Task An_empty_file_has_nothing_to_estimate()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 0, ticketsPerBatch: 100, TestContext.Current.CancellationToken));
    }
}
