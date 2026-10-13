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
        // Three chunks and one manifest, each batching pass minting for itself: one mint for the
        // chunks and one for the manifest.
        Assert.Equal(2, estimate.Mints);
        Assert.Equal(6, estimate.Transactions);
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
        // Eleven mints for 1025 chunks over batches of a hundred, one for the list, one for the
        // manifest.
        Assert.Equal(13, estimate.Mints);
        Assert.Equal(1040, estimate.Transactions);
    }

    [Fact]
    public async Task The_fee_total_is_the_nodes_price_times_the_transactions()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000, fee: 15), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(15, estimate.FeeDropsPerTransaction);
        // Six transactions: three chunks, the manifest, and a mint for each of the two passes.
        Assert.Equal(90, estimate.FeeDropsTotal);
    }

    [Fact]
    public async Task The_peak_reserve_is_a_batch_of_tickets_and_the_account_itself()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // Four memos, so four tickets are ever live at once, well under the protocol's 250 cap.
        Assert.Equal(4 * 200_000, estimate.PeakReserveDrops);
        Assert.Equal(1_000_000, estimate.BaseReserveDrops);
        // A computed literal, not the sum of the instance's own fields: 6 transactions * 3
        // default max attempts * 10 drops, plus a tidy-up of up to 250 transactions * 10 drops,
        // is 2,680 worst-case fee; plus the 800,000-drop peak reserve and the 1,000,000-drop base
        // reserve.
        Assert.Equal(1_802_680, estimate.RequiredDrops);
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
        // 1025 chunks over batches of 250 is five mints (250*4 = 1000, 25 left over), plus one
        // for the list pass and one for the manifest pass.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 1000, TestContext.Current.CancellationToken);

        Assert.Equal(7, estimate.Mints);
        Assert.Equal(1034, estimate.Transactions);
    }

    [Fact]
    public async Task The_worst_case_fee_assumes_every_memo_needs_every_attempt()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000, fee: 15), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(3, estimate.MaxAttempts);
        // 6 transactions * 3 max attempts * 15 drops, plus the tidy-up's 250 transactions * 15 drops.
        Assert.Equal(4_020, estimate.FeeDropsWorstCase);
        Assert.Equal(
            (estimate.Transactions * estimate.MaxAttempts + TicketStrategy.MaxLiveTickets) * estimate.FeeDropsPerTransaction,
            estimate.FeeDropsWorstCase);
        // FeeDropsTotal keeps its present meaning: the run as it goes, nothing retried.
        Assert.Equal(90, estimate.FeeDropsTotal);
    }

    [Fact]
    public async Task A_caller_supplied_max_attempts_changes_the_worst_case_but_not_the_shown_total()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken, maxAttempts: 5);

        Assert.Equal(5, estimate.MaxAttempts);
        // 6 transactions * 5 max attempts * 10 drops, plus the tidy-up's 250 transactions * 10 drops.
        Assert.Equal(2_800, estimate.FeeDropsWorstCase);
        // What is shown to a person does not move just because the worst case grew.
        Assert.Equal(60, estimate.FeeDropsTotal);
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
        // The balance here is also short of RequiredDrops (1,802,680 at these defaults), so a
        // Require that checked the balance first would throw InsufficientBalance instead. This
        // only passes when the ceiling really is checked before the balance.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(1_500_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.False(estimate.BalanceCovers);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.Require(estimate, budgetDrops: 100));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
    }

    [Fact]
    public async Task A_balance_the_accounts_own_objects_already_lock_does_not_cover_the_run()
    {
        // A mainnet-shaped account: five trust lines, one XRP of owner reserve locked out of a
        // balance that clears RequiredDrops (1,802,680 here) on its face. Comparing the raw
        // balance passes the gate and the first TicketCreate is answered tecINSUFFICIENT_RESERVE,
        // with chunks already paid for.
        FakeLedgerSource node = Node(2_000_000);
        node.OwnerCounts["rWriter"] = 5;

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(5, estimate.OwnerCount);
        Assert.Equal(1_000_000, estimate.LockedReserveDrops);
        Assert.Equal(2_000_000, estimate.BalanceDrops);
        Assert.True(estimate.BalanceDrops >= estimate.RequiredDrops, "the raw balance is what used to be compared");
        Assert.False(estimate.BalanceCovers);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.Require(estimate, budgetDrops: null));

        Assert.Equal(XrplFileError.InsufficientBalance, error.Error);
        Assert.Contains("1000000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_is_spendable_is_the_figure_that_meets_what_is_required()
    {
        // The two numbers a caller prints side by side have to be comparable. RequiredDrops
        // already carries this run's base reserve, so SpendableDrops must not subtract it a
        // second time: an account that covers the run would otherwise be reported as holding
        // less than the run needs. FundingRequest.SpendableDrops defines the same name the
        // same way, and the two must not drift apart.
        FakeLedgerSource node = Node(10_700_000);
        node.OwnerCounts["rWriter"] = 0;

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(estimate.BalanceDrops - estimate.LockedReserveDrops, estimate.SpendableDrops);
        Assert.Equal(estimate.SpendableDrops >= estimate.RequiredDrops, estimate.BalanceCovers);
        Assert.True(estimate.BalanceCovers);
        Assert.True(estimate.SpendableDrops >= estimate.RequiredDrops, "a covered run cannot report less than it needs");

        FundingRequest funding = new("rWriter", estimate.RequiredDrops, estimate.BalanceDrops, estimate.LockedReserveDrops);

        Assert.Equal(estimate.SpendableDrops, funding.SpendableDrops);
        Assert.Equal(estimate.BalanceCovers, funding.Covered);
    }

    [Fact]
    public async Task An_account_owning_nothing_is_judged_on_its_whole_balance()
    {
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(1_900_000), "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.OwnerCount);
        Assert.Equal(0, estimate.LockedReserveDrops);
        Assert.True(estimate.BalanceCovers);
        PublicationEstimator.Require(estimate, budgetDrops: null);
    }

    [Fact]
    public async Task Every_batching_pass_mints_for_itself()
    {
        // 50 chunks, one block, no list. The writer calls SubmitAllAsync once for the chunks and
        // once for the manifest, and each call mints from an empty queue — minting is exact, so a
        // call never inherits a ticket from the one before it. Counting one batching pass over all
        // the memos describes a run nobody makes.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 50_000, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(50, estimate.Chunks);
        Assert.Equal(1, estimate.Blocks);
        Assert.Equal(0, estimate.Lists);
        Assert.Equal(2, estimate.Mints);
        Assert.Equal(53, estimate.Transactions);
    }

    [Fact]
    public async Task A_block_list_is_a_batching_pass_of_its_own()
    {
        // 1025 chunks over batches of a hundred is eleven mints for the chunks; the single list
        // memo is a second SubmitAllAsync call and mints once for itself, and the manifest a third.
        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            Node(500_000_000), "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(1, estimate.Lists);
        Assert.Equal(13, estimate.Mints);
        Assert.Equal(1040, estimate.Transactions);
    }

    [Fact]
    public async Task An_empty_file_has_nothing_to_estimate()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublicationEstimator.EstimateAsync(
            Node(100_000_000), "rWriter", fileBytes: 0, ticketsPerBatch: 100, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task This_librarys_leftovers_stand_in_for_tickets_the_run_would_mint()
    {
        FakeLedgerSource node = Node(100_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeaveTickets("rWriter", 3, marked: true);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // Four memos, three of them covered by tickets already on the account: one to mint.
        Assert.Equal(3, estimate.ReusableTickets);
        Assert.Equal(TicketStrategy.MaxLiveTickets, estimate.TicketSlots);
        Assert.Equal(1 * 200_000, estimate.PeakReserveDrops);
        // The count of mints stays the worst case: every pass is assumed to mint (spec 5).
        Assert.Equal(2, estimate.Mints);
    }

    [Fact]
    public async Task More_leftovers_than_memos_mint_nothing_and_never_go_negative()
    {
        FakeLedgerSource node = Node(100_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeaveTickets("rWriter", 10, marked: true);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // Four memos and ten tickets of ours: the run mints nothing, and the six it does not use are
        // the tidy-up's, so they are still reported as reusable rather than clamped to the memo count.
        Assert.Equal(10, estimate.ReusableTickets);
        Assert.Equal(0, estimate.PeakReserveDrops);
        Assert.Equal(TicketStrategy.MaxLiveTickets, estimate.TicketSlots);
    }

    [Fact]
    public async Task Leftovers_that_fill_every_slot_the_others_leave_mint_nothing()
    {
        FakeLedgerSource node = Node(100_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets - 2);
        sink.LeaveTickets("rWriter", 2, marked: true);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        // The account is full, but not refused: the two free slots are this library's own tickets,
        // which stand in for two of the four memos' tickets, and the peak the slots allow is two.
        Assert.Equal(2, estimate.ReusableTickets);
        Assert.Equal(2, estimate.TicketSlots);
        Assert.Equal(0, estimate.PeakReserveDrops);
        PublicationEstimator.RequireTicketSlot(estimate);
    }

    [Fact]
    public async Task Somebody_elses_tickets_take_slots_and_replace_nothing()
    {
        FakeLedgerSource node = Node(100_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", 3);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 2500, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.ReusableTickets);
        Assert.Equal(TicketStrategy.MaxLiveTickets - 3, estimate.TicketSlots);
        Assert.Equal(4 * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task The_peak_is_capped_by_the_slots_somebody_elses_tickets_leave()
    {
        FakeLedgerSource node = Node(500_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets - 2);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, estimate.TicketSlots);
        Assert.Equal(2 * 200_000, estimate.PeakReserveDrops);
    }

    [Fact]
    public async Task No_free_slot_refuses_a_ticketed_run_with_that_reason()
    {
        FakeLedgerSource node = Node(500_000_000);
        FakeLedgerSink sink = new(node);
        sink.LeakTickets("rWriter", TicketStrategy.MaxLiveTickets);

        PublicationEstimate estimate = await PublicationEstimator.EstimateAsync(
            node, "rWriter", fileBytes: 1_024_001, ticketsPerBatch: 100, TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.TicketSlots);
        Assert.Equal(0, estimate.PeakReserveDrops);

        XrplFileException error = Assert.Throws<XrplFileException>(() => PublicationEstimator.RequireTicketSlot(estimate));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("250", error.Message, StringComparison.Ordinal);
    }
}
