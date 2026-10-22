using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class BatchStrategyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_memo_lands_and_reports_the_inner_that_carried_it()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(20), null, Token);

        Assert.Equal(Enumerable.Range(0, 20).ToArray(), landed.Select(memo => memo.Index).ToArray());
        foreach (SubmittedMemo memo in landed)
        {
            LedgerTransaction tx = (await node.Ledger.TransactionAsync(memo.TxHash, Token))!;
            Assert.Equal(MemoLayout.ChunkMarkerHex(memo.Index), tx.MemoTypeHex);
            Assert.Equal(node.Writer.Account, tx.Account);
            Assert.NotNull(tx.ParentBatchID);
        }

        Assert.All(node.Sink.Transmissions, transmission => Assert.Equal(node.Payer.Account, transmission.Account));
    }

    [Fact]
    public async Task Nine_memos_go_as_seven_and_two()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(9), null, Token);

        Assert.Equal([7, 2], node.Sink.Transmissions.Where(t => t.TransactionType == "Batch").OrderBy(t => t.Sequence).Select(t => t.Inners).ToArray());
    }

    [Fact]
    public async Task Each_outer_pays_its_inners_plus_three_units_at_the_rounds_unit()
    {
        BatchTestNode node = new();
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(12, 10);
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(18, 36, 10_000_000, 10_000_000));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(10), null, Token);

        List<LedgerTransaction> outers = [];
        await foreach (LedgerTransaction tx in node.Ledger.AccountTransactionsAsync(node.Payer.Account, 0, node.Ledger.CurrentLedger, true, Token))
        {
            outers.Add(tx);
        }

        Assert.Equal([(8 + 3) * 18L, (2 + 3) * 18L], outers.Select(outer => outer.Fee!.Value).ToArray());
    }

    [Fact]
    public async Task Every_transmission_carries_a_deadline_twenty_ledgers_past_a_fresh_snapshot_persisted_before_it_is_sent()
    {
        BatchTestNode node = new();
        List<string> order = [];
        node.Fence.OnRecord = deadline => { lock (order) order.Add($"fence:{deadline}"); };
        node.Sink.Transmitting = transmission => { lock (order) order.Add($"send:{transmission.LastLedger}"); };
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { DeadlineLedgers = new BatchStrategyOptions().DeadlineLedgers });

        await strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);

        foreach (FakeBatchLedgerSink.Transmission transmission in node.Sink.Transmissions)
        {
            Assert.InRange(transmission.LastLedger - transmission.OpenLedgerIndex, 0L, (long)new BatchStrategyOptions().DeadlineLedgers);
            int sent = order.IndexOf($"send:{transmission.LastLedger}");
            int fenced = order.FindIndex(entry => entry.StartsWith("fence:", StringComparison.Ordinal)
                && long.Parse(entry["fence:".Length..]) >= transmission.LastLedger);
            Assert.InRange(fenced, 0, sent);
        }
    }

    [Fact]
    public async Task A_round_that_landed_everything_settles_long_before_its_deadlines()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        long settled = long.Parse(strategy.Trace.First(entry => entry.StartsWith("settled:", StringComparison.Ordinal))["settled:".Length..]);
        Assert.True(settled < node.Sink.Transmissions.Min(transmission => transmission.LastLedger));
    }

    [Fact]
    public async Task A_round_settles_at_the_snapshot_that_resolves_its_last_candidate()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        // Once B's validated sequence has passed every outer, nothing the round sent can apply, so
        // waiting further ledgers buys nothing (spec B9 as amended 2026-10-21). One ledger of slack:
        // every read of the fake closes one, and a progress sweep may read between the two.
        long lastLanded = (await node.LandedByMarkerAsync()).Values.SelectMany(copies => copies).Max(tx => tx.LedgerIndex);
        long settled = long.Parse(strategy.Trace.First(entry => entry.StartsWith("settled:", StringComparison.Ordinal))["settled:".Length..]);
        Assert.InRange(settled, lastLanded, lastLanded + 1);
    }

    [Fact]
    public async Task An_outer_that_never_lands_stalls_the_round_and_its_memos_go_in_the_next()
    {
        BatchTestNode node = new(sink => sink.LoseOuter.Add(BatchTestNode.PayerStart));
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAhead = 1 });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains("halt:stalled", strategy.Trace);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_tec_outer_is_seen_as_divergence_and_its_memos_are_sent_again()
    {
        BatchTestNode node = new(sink => sink.OuterResult[BatchTestNode.PayerStart] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAhead = 1 });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains("halt:diverged", strategy.Trace);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_tec_inner_costs_its_chunk_only_and_the_next_round_resends_it()
    {
        // InnerResult applies to inners only: the second round sends the one missing memo as a solo.
        BatchTestNode node = new(sink => sink.InnerResult[MemoLayout.ChunkMarkerHex(3)] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        Assert.Equal(16, landed.Count);
        Assert.DoesNotContain(strategy.Trace, entry => entry.StartsWith("halt:", StringComparison.Ordinal));
        Assert.Equal(2, strategy.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)));
        Assert.Equal("AccountSet", node.Sink.Transmissions[^1].TransactionType);
    }

    [Fact]
    public async Task An_inner_that_does_not_consume_its_sequence_halts_the_round_and_nothing_is_lost()
    {
        BatchTestNode node = new(sink => sink.InnerVanishes.Add(MemoLayout.ChunkMarkerHex(2)));
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAhead = 1 });

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains("halt:diverged", strategy.Trace);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_later_outer_that_overtakes_an_earlier_one_is_held_and_still_lands()
    {
        BatchTestNode node = new();
        OvertakingSink overtaking = new(node.Sink);
        await using BatchStrategy strategy = node.Strategy(sink: overtaking);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);

        Assert.Equal(24, landed.Count);
        Assert.Contains(node.Sink.Transmissions, transmission => transmission.Answer == "terPRE_SEQ");
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Busy_answers_resend_the_same_blob_and_need_no_new_round()
    {
        BatchTestNode node = new(sink =>
        {
            sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "telCAN_NOT_QUEUE";
            sink.AnswerAt[(BatchTestNode.PayerStart, 2)] = "error:tooBusy";
            sink.AnswerAt[(BatchTestNode.PayerStart, 3)] = "error:slowDown";
        });
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token);

        Assert.Equal(8, landed.Count);
        Assert.Single(node.Sink.Transmissions.Select(transmission => transmission.Hash).Distinct());
        Assert.Equal(4, node.Sink.Transmissions.Count);
        Assert.Single(strategy.Trace, entry => entry.StartsWith("round:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fee_refusal_is_not_resent_and_the_next_round_reads_the_unit_again()
    {
        BatchTestNode node = new(sink => sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "telINSUF_FEE_P");
        await using BatchStrategy strategy = node.Strategy();

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token);

        Assert.Equal(8, landed.Count);
        Assert.Single(node.Sink.Transmissions, transmission => transmission.Answer == "telINSUF_FEE_P");
        Assert.Equal(2, strategy.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_malformed_answer_fails_the_run_quoting_it()
    {
        BatchTestNode node = new(sink => sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "temBAD_SIGNATURE");
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Equal("temBAD_SIGNATURE", error.EngineResult);
    }

    [Fact]
    public async Task A_round_of_one_memo_sends_a_plain_AccountSet_of_the_writer_and_no_batch()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        SubmittedMemo landed = Assert.Single(await strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));

        FakeBatchLedgerSink.Transmission solo = Assert.Single(node.Sink.Transmissions);
        Assert.Equal("AccountSet", solo.TransactionType);
        Assert.Equal(node.Writer.Account, solo.Account);
        Assert.Null((await node.Ledger.TransactionAsync(landed.TxHash, Token))!.ParentBatchID);
    }

    [Fact]
    public async Task The_window_keeps_transmissions_in_flight_within_its_size_and_growth()
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(100);

        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { InitialWindow = 2 });

        await strategy.SubmitAllAsync(BatchTestNode.Memos(300), null, Token);

        // The window grows by one after ten clean answers (spec B10), and every transmission answers at most once.
        int largestWindow = 2 + node.Sink.Transmissions.Count / 10;

        Assert.InRange(node.Sink.PeakInFlight, 1, largestWindow);
    }

    [Fact]
    public async Task No_outer_is_sent_more_than_the_allowance_past_the_payers_checked_sequence()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAhead = 2 });

        await strategy.SubmitAllAsync(BatchTestNode.Memos(80), null, Token);

        Assert.All(
            node.Sink.Transmissions.Where(transmission => transmission.TransactionType == "Batch"),
            transmission => Assert.True(transmission.Sequence < transmission.PayerValidatedSequence + 2));
    }

    [Fact]
    public async Task A_batch_signed_while_the_round_halts_is_discarded_unsent()
    {
        BatchTestNode node = new();
        GatedSigner payer = new(node.Payer, gateAt: 3);
        await using BatchStrategy strategy = node.Strategy(payer: payer);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);
        await payer.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        node.Sink.Foreign(node.Writer.Account, "TicketCreate", 16);
        await BatchTestNode.EventuallyAsync(() => strategy.Trace.Contains("halt:diverged"));
        payer.Release();

        // Whether the run then completes or fails is Task 9's business (identity); here the
        // discarded blob must never have reached the node.
        Exception? outcome = await Record.ExceptionAsync(() => run);
        Assert.True(outcome is null or XrplFileException, outcome?.ToString());
        Assert.Contains("discard:2", strategy.Trace);
        Assert.DoesNotContain(node.Sink.Transmissions, transmission => transmission.Hash == payer.GatedHash);
    }

    [Fact]
    public async Task A_batch_registered_but_not_yet_seen_by_the_node_is_waited_for_before_the_sweep()
    {
        BatchTestNode node = new(sink => sink.LoseOuter.Add(BatchTestNode.PayerStart));
        GatedBlobSink gate = new(node.Sink, gateAt: 2);
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAhead = 2 }, sink: gate);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(24), null, Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await BatchTestNode.EventuallyAsync(() => strategy.Trace.Contains("halt:stalled"));
        gate.Release();

        IReadOnlyList<SubmittedMemo> landed = await run;

        // Registered, so not discarded: it reached the node after the halt, and the round settled
        // only past its deadline, when it could no longer apply (spec B9).
        string gatedHash = Xrpl.Utils.Hashes.HashLedger.HashSignedTx(gate.Gated!).ToUpperInvariant();
        FakeBatchLedgerSink.Transmission gated = Assert.Single(node.Sink.Transmissions, transmission => transmission.Hash == gatedHash);
        long settled = long.Parse(strategy.Trace.First(entry => entry.StartsWith("settled:", StringComparison.Ordinal))["settled:".Length..]);

        Assert.Equal(24, landed.Count);
        Assert.True(settled > gated.LastLedger);
        Assert.All((await node.LandedByMarkerAsync()).Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task A_fence_that_cannot_be_saved_sends_nothing()
    {
        BatchTestNode node = new();
        node.Fence.Fail = true;
        await using BatchStrategy strategy = node.Strategy();

        await Assert.ThrowsAsync<IOException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public void A_payer_who_is_also_the_writer_is_refused()
    {
        BatchTestNode node = new();

        Assert.Throws<ArgumentException>(() => node.Strategy(payer: node.Writer));
    }

    [Fact]
    public void The_strategy_reports_the_approved_unit_the_writer_and_no_mints()
    {
        BatchTestNode node = new();
        BatchStrategy strategy = node.Strategy();

        Assert.Equal(15, strategy.FeeDropsPerTransaction);
        Assert.Equal(node.Writer.Account, strategy.Account);
        Assert.Equal(node.Payer.Account, strategy.Payer);
        Assert.Equal(0, strategy.TicketMints);
    }

    [Fact]
    public async Task Every_landed_memo_says_which_ledger_it_is_in()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        foreach (SubmittedMemo memo in await strategy.SubmitAllAsync(BatchTestNode.Memos(10), null, Token))
        {
            Assert.Equal((await node.Ledger.TransactionAsync(memo.TxHash, Token))!.LedgerIndex, memo.LedgerIndex);
        }
    }

    [Fact]
    public async Task No_batch_is_signed_until_the_window_has_a_slot_for_it()
    {
        BatchTestNode node = new();
        GatedBlobSink gate = new(node.Sink, gateAt: 1);
        CountingSigner payer = new(node.Payer);
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { InitialWindow = 1 }, payer: payer, sink: gate);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), null, Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await Task.Delay(200, Token);

        // The one slot is held by the first batch's transmission: nothing else may be signed meanwhile.
        Assert.Equal(1, payer.Count);

        gate.Release();
        Assert.Equal(40, (await run).Count);
    }

    [Fact]
    public async Task A_pass_says_it_is_sending_once_its_first_batch_is_answered_before_any_memo_is_found()
    {
        // The first progress sweep finds landed memos 5 s or more into sending; until then the run
        // screen had nothing to go on and said "Starting" while batches were going out.
        BatchTestNode node = new();
        GatedBlobSink gate = new(node.Sink, gateAt: 1);
        List<int> reports = [];
        Lock reportsGate = new();
        SynchronousProgress<int> progress = new(done => { lock (reportsGate) reports.Add(done); });
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { InitialWindow = 1 }, sink: gate);

        Task<IReadOnlyList<SubmittedMemo>> run = strategy.SubmitAllAsync(BatchTestNode.Memos(40), progress, Token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await Task.Delay(200, Token);
        int beforeAnswer;
        lock (reportsGate) beforeAnswer = reports.Count;

        gate.Release();
        await run;

        Assert.Equal(0, beforeAnswer);
        Assert.Equal(0, reports[0]);
        Assert.Single(reports, done => done == 0);
        Assert.Equal(40, reports[^1]);
        Assert.Equal(reports.Order(), reports);
    }

    /// <summary>
    /// Holds the first blob submitted until the second has been answered, so the second outer reaches
    /// the node first whatever the speed of the machine (a timed delay of the fake raced with signing
    /// on a cold process, and with the deadlines the fake's ledger reads advance).
    /// </summary>
    private sealed class OvertakingSink(ILedgerSink inner) : ILedgerSink
    {
        private readonly TaskCompletionSource _secondAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            inner.SubmitAsync(transaction, signer, cancellationToken);

        public async Task<string> SubmitBlobAsync(string txBlob, CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref _count);

            if (ordinal == 1)
            {
                await _secondAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return await inner.SubmitBlobAsync(txBlob, cancellationToken);
            }

            try
            {
                return await inner.SubmitBlobAsync(txBlob, cancellationToken);
            }
            finally
            {
                if (ordinal == 2) _secondAnswered.TrySetResult();
            }
        }
    }

    /// <summary>Counts the signatures it makes.</summary>
    private sealed class CountingSigner(ITransactionSigner inner) : ITransactionSigner
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public string Account => inner.Account;

        public string PublicKey => inner.PublicKey;

        public Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return inner.SignAsync(transaction, cancellationToken);
        }
    }
}
