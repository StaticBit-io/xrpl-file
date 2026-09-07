using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class ThrowawayAccountTests
{
    private static readonly XrplWallet Throwaway = XrplWallet.Generate();

    private static PublicationEstimate Estimate(long required) =>
        new(Chunks: 3, Blocks: 1, Lists: 0, Mints: 2, Transactions: 6,
            FeeDropsPerTransaction: 10, FeeDropsTotal: 60, MaxAttempts: 3, FeeDropsWorstCase: 180,
            PeakReserveDrops: 800_000, BaseReserveDrops: 1_000_000, OwnerReserveDrops: 200_000,
            RequiredDrops: required, BalanceDrops: 0, OwnerCount: 0);

    private static FakeLedgerSource Node(long balance = 0, long current = 5000, int ownerCount = 0)
    {
        FakeLedgerSource source = new() { Fees = new NetworkFees(10, 1_000_000, 200_000), CurrentLedger = current };
        source.Balances[Throwaway.ClassicAddress] = balance;
        source.OwnerCounts[Throwaway.ClassicAddress] = ownerCount;
        return source;
    }

    [Fact]
    public async Task The_funding_request_names_the_address_and_what_it_needs()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        FundingRequest request = await account.FundingAsync(Estimate(2_500_000), TestContext.Current.CancellationToken);

        Assert.Equal(Throwaway.ClassicAddress, request.Address);
        Assert.Equal(2_500_000, request.RequiredDrops);
        Assert.Equal(0, request.BalanceDrops);
        Assert.False(request.Covered);
        Assert.Equal(2_500_000, request.MissingDrops);
    }

    [Fact]
    public void The_thing_a_wallet_scans_is_the_address_itself()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        Assert.Equal(Throwaway.ClassicAddress, account.Address);
        Assert.Equal(
            Throwaway.ClassicAddress,
            new FundingRequest(Throwaway.ClassicAddress, 1, 0, 0).QrPayload);
    }

    [Fact]
    public async Task Money_that_arrived_covers_the_request()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        FundingRequest request = await account.FundingAsync(Estimate(2_500_000), TestContext.Current.CancellationToken);

        Assert.True(request.Covered);
        Assert.Equal(0, request.MissingDrops);
    }

    [Fact]
    public async Task Tickets_an_aborted_run_left_live_are_not_money_the_next_one_can_spend()
    {
        // Ten tickets an interrupted run never spent, at 200,000 drops of owner reserve each: the
        // balance clears RequiredDrops on its face and the account cannot carry the run. Reporting
        // it as funded is how a person is told to pay, pays, and is then refused by the session's
        // own balance gate.
        FakeLedgerSource ledger = Node(balance: 3_000_000, ownerCount: 10);
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        FundingRequest request = await account.FundingAsync(Estimate(2_500_000), TestContext.Current.CancellationToken);

        Assert.Equal(2_000_000, request.LockedReserveDrops);
        Assert.Equal(1_000_000, request.SpendableDrops);
        Assert.False(request.Covered);
        Assert.Equal(1_500_000, request.MissingDrops);
    }

    [Fact]
    public async Task Waiting_returns_as_soon_as_the_money_is_there()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        Task<FundingRequest> waiting = account.AwaitFundingAsync(
            Estimate(2_500_000), TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        ledger.Balances[Throwaway.ClassicAddress] = 2_500_000;

        FundingRequest request = await waiting;

        Assert.True(request.Covered);
    }

    [Fact]
    public async Task Waiting_stops_when_the_caller_cancels()
    {
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);
        using CancellationTokenSource giveUp = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => account.AwaitFundingAsync(Estimate(2_500_000), TimeSpan.FromMilliseconds(5), giveUp.Token));
    }

    [Fact]
    public async Task The_funder_is_read_from_the_payment_that_arrived()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        // The payment was sent by somebody else and lands in this account's account_tx because it
        // is the destination — which is what the funder is read out of.
        ledger.AddAffecting(
            new LedgerTransaction(
                "AAAA", 5001, "rWhoeverPaid", Succeeded: true, Validated: true, Result: "tesSUCCESS",
                MemoTypeHex: null, MemoDataHex: null, MemoFormatHex: null),
            Throwaway.ClassicAddress);
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        string? funder = await account.FunderAsync(5000, TestContext.Current.CancellationToken);

        Assert.Equal("rWhoeverPaid", funder);
    }

    [Fact]
    public async Task Nobody_paid_means_nobody_is_proposed()
    {
        // An account nobody has funded does not exist on the ledger at all, and a real node
        // answers actNotFound rather than an empty list. That absence is an answer, and the
        // source is what turns it into one -- pinned against a node's own two error shapes by
        // XrplClientLedgerSourceTests, because a fake that cannot fail proves nothing here.
        FakeLedgerSource ledger = Node();
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        Assert.Null(await account.FunderAsync(5000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_node_that_cannot_be_read_is_not_reported_as_nobody_paid()
    {
        // The other half of the same question: a node that genuinely could not answer must not
        // come out of here as "nobody funded this account", which would send the refund screen a
        // blank proposal built on nothing.
        FakeLedgerSource ledger = Node();
        ledger.AccountTransactionsFailure = new XrplFileException(
            XrplFileError.NodeUnreachable, "fake: the node answered internal to account_tx");
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        XrplFileException failed = await Assert.ThrowsAsync<XrplFileException>(
            () => account.FunderAsync(5000, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.NodeUnreachable, failed.Error);
    }

    [Fact]
    public async Task The_account_itself_is_never_proposed_as_its_own_funder()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        ledger.Add(new LedgerTransaction(
            "AAAA", 5001, Throwaway.ClassicAddress, Succeeded: true, Validated: true, Result: "tesSUCCESS",
            MemoTypeHex: null, MemoDataHex: null, MemoFormatHex: null));
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        Assert.Null(await account.FunderAsync(5000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_refund_sends_everything_above_the_reserve_and_the_fee()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        // Three XRP held, one XRP of base reserve stays, ten drops pay for the payment.
        Assert.Equal(3_000_000 - 1_000_000 - 10, refund.SentDrops);
        Assert.Equal(1_000_000, refund.LeftBehindDrops);
        Assert.Equal("rBack", refund.Destination);
        Assert.Equal(RefundOutcome.Submitted, refund.Outcome);

        // The fields that move money are read off what was actually signed, not off the record
        // describing it: a version that reported the sendable and sent the balance would satisfy
        // every assertion above.
        Assert.Equal("rBack", sink.Submitted[0]["Destination"]);
        Assert.Equal("1999990", sink.Submitted[0]["Amount"]);
        Assert.Equal("10", sink.Submitted[0]["Fee"]);
    }

    [Fact]
    public async Task Tickets_an_aborted_run_left_live_keep_their_owner_reserve_out_of_the_refund()
    {
        // What a publication that threw mid-batch leaves behind: TicketStrategy.DisposeAsync drops
        // its queue, and the tickets stay on the ledger, one owner reserve each. Priced against
        // the base reserve alone the payment is over the account's spendable balance, the node
        // answers tecUNFUNDED_PAYMENT, the fee is claimed and every retry does the same -- which
        // is exactly when a person most wants their money back.
        FakeLedgerSource ledger = Node(balance: 3_000_000, ownerCount: 5);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        // One XRP of base reserve, five fifths of an XRP of owner reserve, ten drops of fee.
        Assert.Equal(3_000_000 - 1_000_000 - 1_000_000 - 10, refund.SentDrops);
        Assert.Equal(2_000_000, refund.LeftBehindDrops);
        Assert.Equal("999990", sink.Submitted[0]["Amount"]);
    }

    [Fact]
    public async Task A_balance_that_is_only_the_reserve_has_nothing_to_send_back()
    {
        FakeLedgerSource ledger = Node(balance: 1_000_000);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        Assert.Equal(0, refund.SentDrops);
        Assert.Equal(RefundOutcome.NothingToSend, refund.Outcome);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task Owner_reserves_that_swallow_the_balance_leave_nothing_to_send_back()
    {
        // One and a half XRP against a floor of two: the account is over its base reserve and
        // still has nothing to give back.
        FakeLedgerSource ledger = Node(balance: 1_500_000, ownerCount: 5);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        Assert.Equal(0, refund.SentDrops);
        Assert.Equal(1_500_000, refund.LeftBehindDrops);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_balance_a_few_drops_over_the_floor_reports_what_is_really_left()
    {
        // Above the reserve, but not by enough to pay for the payment. Nothing moves, so what
        // stays behind is the whole balance; naming the reserve would be five drops short of the
        // truth, and the drops it forgets are real ones on a real account.
        FakeLedgerSource ledger = Node(balance: 1_000_005);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        Assert.Equal(0, refund.SentDrops);
        Assert.Equal(1_000_005, refund.LeftBehindDrops);
        Assert.Empty(sink.Submitted);
    }

    [Fact]
    public async Task A_queued_refund_says_it_is_queued_rather_than_that_the_money_went_home()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        FakeLedgerSink sink = new(ledger);
        sink.Refusals[0] = "terQUEUED";
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        RefundResult refund = await account.RefundAsync("rBack", TestContext.Current.CancellationToken);

        // terQUEUED is not arrival. The queue can drop the submission, and until a ledger takes
        // it every drop is still on the throwaway account.
        Assert.Equal(RefundOutcome.Queued, refund.Outcome);
        Assert.NotEqual(string.Empty, refund.TxHash);
    }

    [Fact]
    public async Task A_refund_without_a_destination_is_refused()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        ThrowawayAccount account = new(new FakeLedgerSink(ledger), ledger, Throwaway);

        await Assert.ThrowsAsync<ArgumentException>(
            () => account.RefundAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refund_the_node_refuses_is_not_reported_as_money_that_went_home()
    {
        FakeLedgerSource ledger = Node(balance: 3_000_000);
        FakeLedgerSink sink = new(ledger);
        sink.Refusals[0] = "tecUNFUNDED_PAYMENT";
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        XrplFileException refused = await Assert.ThrowsAsync<XrplFileException>(
            () => account.RefundAsync("rBack", TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, refused.Error);
    }

    [Fact]
    public async Task Deletion_becomes_possible_two_hundred_and_fifty_four_ledgers_past_the_sequence()
    {
        FakeLedgerSource ledger = Node(balance: 1_000_000, current: 5000);
        FakeLedgerSink sink = new(ledger);
        ThrowawayAccount account = new(sink, ledger, Throwaway);

        // The fake sink hands out sequence 10 for this account.
        long from = await account.DeletableFromLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10 + 254, from);
        Assert.Equal(254, ThrowawayAccount.DeletionSequenceDelta);
    }
}
