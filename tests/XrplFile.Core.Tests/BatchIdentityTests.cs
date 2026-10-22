using Xunit;

namespace XrplFile.Tests;

public class BatchIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Queues a foreign transaction at the first transmission, so it lands inside the first round.</summary>
    private static BatchTestNode NodeWithForeign(Func<BatchTestNode, string> account, string kind, uint count = 0)
    {
        BatchTestNode node = new();
        bool queued = false;
        node.Sink.Transmitting = _ =>
        {
            if (queued) return;
            queued = true;
            node.Sink.Foreign(account(node), kind, count);
        };
        return node;
    }

    [Fact]
    public async Task A_transaction_of_the_writer_that_the_run_did_not_send_fails_it_naming_the_writer()
    {
        BatchTestNode node = NodeWithForeign(n => n.Writer.Account, "AccountSet");
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Writer, error.Role);
        Assert.Contains(node.Writer.Account, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transaction_of_the_payer_that_the_run_did_not_send_fails_it_naming_the_payer()
    {
        BatchTestNode node = NodeWithForeign(n => n.Payer.Account, "AccountSet");
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Payer, error.Role);
        Assert.Contains(node.Payer.Account, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_create_of_the_payer_is_caught_by_identity_not_by_sequence_arithmetic()
    {
        BatchTestNode node = NodeWithForeign(n => n.Payer.Account, "TicketCreate", 5);
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Payer, error.Role);
        Assert.Contains(node.Payer.Account, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_create_and_a_ticketed_transaction_of_the_writer_complete_the_history_and_fail_identity()
    {
        BatchTestNode node = new();
        bool queued = false;
        node.Sink.Transmitting = _ =>
        {
            if (queued) return;
            queued = true;
            node.Sink.Foreign(node.Writer.Account, "TicketCreate", 3);
            node.Sink.Foreign(node.Writer.Account, "TicketedAccountSet");
        };
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token));

        // Not NodeLacksHistory: the TicketCreate covers the sequences its tickets took (spec B30).
        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Writer, error.Role);
    }

    [Fact]
    public async Task A_history_that_lags_the_snapshot_is_read_again_until_it_covers_the_round()
    {
        BatchTestNode node = new();
        node.Ledger.HistoryHides = tx => tx.ParentBatchID is not null;
        node.Ledger.HistoryHideReads = 2;
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { ProgressSweep = TimeSpan.FromHours(1) });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        Assert.Equal(16, landed.Count);
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_final_sweep_reads_the_writer_only_from_where_the_progress_sweeps_left_off()
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(20);
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.ProgressOptions with { InitialWindow = 2, MaxWindow = 2 });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(64), null, Token);

        Assert.Equal(64, landed.Count);
        List<AccountTransactionsCall> calls = [.. node.Ledger.Calls];
        int payerRead = calls.FindIndex(call => call.Account == node.Payer.Account);
        AccountTransactionsCall progressFirst = calls.First(call => call.Account == node.Writer.Account);
        AccountTransactionsCall finalWriter = calls.Take(payerRead).Last(call => call.Account == node.Writer.Account);
        Assert.True(finalWriter.FromLedger > progressFirst.FromLedger, $"the final sweep read the writer from {finalWriter.FromLedger} again");
    }

    [Fact]
    public async Task A_record_the_progress_sweeps_missed_is_found_by_reading_the_round_in_full()
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(20);
        string hidden = MemoLayout.ChunkMarkerHex(3);

        // Lags only until the final sweep reads the payer: what the progress sweeps kept lacks the
        // record, and so does the first final read, which starts past its ledger.
        node.Ledger.HistoryHides = tx => string.Equals(tx.MemoTypeHex, hidden, StringComparison.OrdinalIgnoreCase)
            && !node.Ledger.Calls.Any(call => call.Account == node.Payer.Account);
        node.Ledger.HistoryHideReads = int.MaxValue;
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.ProgressOptions with { InitialWindow = 2, MaxWindow = 2 });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(64), null, Token);

        Assert.Equal(64, landed.Count);
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
        List<AccountTransactionsCall> calls = [.. node.Ledger.Calls];
        int payerRead = calls.FindIndex(call => call.Account == node.Payer.Account);
        long start = calls.First(call => call.Account == node.Writer.Account).FromLedger;
        Assert.Contains(calls.Skip(payerRead), call => call.Account == node.Writer.Account && call.FromLedger == start);
    }

    [Fact]
    public async Task A_history_that_never_covers_the_round_fails_with_NodeLacksHistory()
    {
        BatchTestNode node = new();
        node.Ledger.HistoryHides = tx => tx.ParentBatchID is not null;
        node.Ledger.HistoryHideReads = int.MaxValue;
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with
        {
            ProgressSweep = TimeSpan.FromHours(1),
            HistoryLagLimit = TimeSpan.FromMilliseconds(200),
        });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Equal(XrplFileError.NodeLacksHistory, error.Error);
    }

    [Fact]
    public async Task The_payer_fee_is_the_fee_of_every_outer_that_applied_a_tec_outer_included()
    {
        BatchTestNode node = new(sink => sink.OuterResult[BatchTestNode.PayerStart] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        long applied = 0;
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Payer.Account, 0, node.Ledger.CurrentLedger, true, Token))
        {
            applied += tx.Fee!.Value;
        }

        Assert.Equal(applied, strategy.PayerFeeDrops);
        Assert.True(applied > 2 * BatchFees.BatchFee(8, 15));
    }

    [Fact]
    public async Task A_batch_signed_while_a_foreign_transaction_halted_the_round_ends_in_AccountInUse()
    {
        BatchTestNode node = new();
        GatedSigner payer = new(node.Payer, gateAt: 3);
        await using BatchStrategy strategy = node.Strategy(payer: payer);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);
        await payer.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        node.Sink.Foreign(node.Writer.Account, "TicketCreate", 16);
        await BatchTestNode.EventuallyAsync(() => strategy.Trace.Contains("halt:diverged"));
        payer.Release();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => run);
        Assert.Equal(XrplFileError.AccountInUse, error.Error);
        Assert.Equal(AccountRole.Writer, error.Role);
    }

    [Fact]
    public void A_ticket_create_covers_its_own_sequence_and_its_tickets()
    {
        LedgerTransaction create = new("C", 5, "rW", true, true, "tesSUCCESS", null, null, null)
        {
            TransactionType = "TicketCreate", Sequence = 10, TicketCount = 3,
        };
        LedgerTransaction next = new("N", 6, "rW", false, true, "tecFAKE", null, null, null) { Sequence = 14 };

        Assert.True(BatchSweep.Covers([create, next], 10, 15));
        Assert.False(BatchSweep.Covers([create], 10, 15));
        Assert.True(BatchSweep.Covers([], 10, 10));
    }

    [Fact]
    public async Task The_tidy_up_spends_this_librarys_leftover_tickets_on_the_writer()
    {
        BatchTestNode node = new();
        node.Ledger.Add(new LedgerTransaction("CREATOR", 1, node.Writer.Account, true, true, "tesSUCCESS", MemoLayout.TicketMarkerHex, null, null));
        node.Sink.AddLiveTicket(node.Writer.Account, 77, "CREATOR");
        await using BatchStrategy strategy = node.Strategy();

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(Token);

        Assert.Equal(1, release.Released);
        FakeBatchLedgerSink.Transmission spend = Assert.Single(node.Sink.Transmissions);
        Assert.Equal(node.Writer.Account, spend.Account);
    }

    [Fact]
    public async Task With_no_leftover_tickets_the_tidy_up_sends_nothing()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        Assert.Equal(TicketRelease.None, await strategy.ReleaseLeftoverTicketsAsync(Token));
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public void A_payer_record_that_is_not_validated_is_not_counted_in_the_payer_fee()
    {
        BatchTestNode node = new();
        BatchContext context = new(
            node.Sink, node.Ledger, node.Writer, node.Payer, BatchTestNode.Budget, BatchTestNode.FastOptions,
            new BatchFence(node.Fence), new BatchWindow(1), new BatchTrace(), new BatchSpending(BatchTestNode.Budget));
        BatchCandidate outer = new(0, isOuter: true, node.Payer.Account, 500, 900, 120, [0], new SignedTransaction("00", "AA"));
        LedgerTransaction validated = new("AA", 5, node.Payer.Account, true, true, "tesSUCCESS", null, null, null) { Fee = 120 };
        LedgerTransaction pendingOne = validated with { Validated = false, Fee = 999 };

        RoundResult counted = BatchSweep.Outcome(context, BatchTestNode.Memos(1), [0], [outer], [], [validated]);
        RoundResult skipped = BatchSweep.Outcome(context, BatchTestNode.Memos(1), [0], [outer], [], [pendingOne]);

        Assert.Equal(120, counted.PayerFeeDrops);
        Assert.Equal(0, skipped.PayerFeeDrops);
        Assert.True(skipped.PayerFeeKnown);
    }

    [Fact]
    public async Task A_blob_held_at_the_gate_outlives_the_cancellation_of_the_token_it_was_sent_with()
    {
        BatchTestNode node = new();
        GatedBlobSink gate = new(node.Sink, gateAt: 1);
        using CancellationTokenSource cancellation = new();

        Task<string> held = gate.SubmitBlobAsync("00", cancellation.Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await cancellation.CancelAsync();
        await Task.Delay(50, Token);

        Assert.False(held.IsCompleted);
        gate.Release();
        await Assert.ThrowsAnyAsync<Exception>(() => held);
    }

    [Fact]
    public async Task Transmissions_cancelled_at_the_end_of_their_rounds_do_not_narrow_the_window()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(sink: new HangingBlobSink(node.Sink));

        await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Equal(BatchTestNode.FastOptions.InitialWindow, strategy.WindowSize);
        Assert.Equal(0, strategy.WindowInFlight);
    }

    /// <summary>A node that takes every blob and never answers, until the round gives up on it.</summary>
    private sealed class HangingBlobSink(ILedgerSink inner) : ILedgerSink
    {
        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "unreachable";
        }
    }
}
