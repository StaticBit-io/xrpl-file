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

        // Four memos, so four tickets are ever live at once, well under the protocol's 250 cap.
        Assert.Equal(4 * 200_000, estimate.PeakReserveDrops);
        Assert.Equal(1_000_000, estimate.BaseReserveDrops);
        // A computed literal, not the sum of the instance's own fields: 5 transactions * 3
        // default max attempts * 10 drops fee = 150 worst-case fee, plus the 800,000-drop peak
        // reserve and the 1,000,000-drop base reserve.
        Assert.Equal(1_800_150, estimate.RequiredDrops);
    }

    [Fact]
    public async Task A_batch_smaller_than_the_file_does_not_cap_the_peak_reserve()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // A thousand-plus memos and a batch of a hundred: the live-ticket ceiling is the
        // protocol's own cap (TicketStrategy.MaxLiveTickets), not the caller's batch size — a
        // ticket dispatched whose submission never reaches a ledger stays live for the run's
        // lifetime, so the batch size cannot be trusted to bound the peak.
        Assert.Equal(TicketStrategy.MaxLiveTickets * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task A_batch_above_the_protocol_ceiling_is_still_clamped_when_counting_mints()
    {
        // ticketsPerBatch of 1000 exceeds TicketStrategy.MaxTicketsPerMint (250): the mint count
        // must use the clamped batch size, not the caller's number, or it undercounts mints.
        // 1027 memos over batches of 250 is five mints (250*4 = 1000, 27 left over).
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 1000, TestContext.Current.CancellationToken);

        Assert.Equal(5, estimate.Mints);
        Assert.Equal(1032, estimate.Transactions);
    }

    [Fact]
    public async Task The_worst_case_fee_assumes_every_memo_needs_every_attempt()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000, fee: 15), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.MaxAttempts);
        // 5 transactions * 3 max attempts * 15 drops fee.
        Assert.Equal(225, estimate.FeeDropsWorstCase);
        Assert.Equal(estimate.Transactions * estimate.MaxAttempts * estimate.FeeDropsPerTransaction, estimate.FeeDropsWorstCase);
        // FeeDropsTotal keeps its present meaning: the run as it goes, nothing retried.
        Assert.Equal(75, estimate.FeeDropsTotal);
    }

    [Fact]
    public async Task A_caller_supplied_max_attempts_changes_the_worst_case_but_not_the_shown_total()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken, maxAttempts: 5);

        Assert.Equal(5, estimate.MaxAttempts);
        // 5 transactions * 5 max attempts * 10 drops fee.
        Assert.Equal(250, estimate.FeeDropsWorstCase);
        // What is shown to a person does not move just because the worst case grew.
        Assert.Equal(50, estimate.FeeDropsTotal);
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
        // The balance here is also short of RequiredDrops (1,800,150 at these defaults), so a
        // Require that checked the balance first would throw InsufficientBalance instead. This
        // only passes when the ceiling really is checked before the balance.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(1_500_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.False(estimate.BalanceCovers);

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
