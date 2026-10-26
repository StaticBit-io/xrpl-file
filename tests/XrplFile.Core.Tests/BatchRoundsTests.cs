using Xunit;

namespace XrplFile.Tests;

public class BatchRoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static BatchStrategyOptions Capped(int memos) => BatchTestNode.FastOptions with { MaxRoundMemos = memos };

    private static int Rounds(BatchStrategy strategy) => strategy.Trace.Count(entry => entry.StartsWith("round:", StringComparison.Ordinal));

    [Fact]
    public async Task Three_hundred_memos_at_sixty_four_a_round_go_in_five_rounds_and_each_lands_once()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        SubmittedMemos landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(300), null, Token);

        Assert.Equal(300, landed.Landed.Count);
        Assert.Equal(5, Rounds(strategy));
        IReadOnlyDictionary<string, List<LedgerTransaction>> records = await node.LandedByMarkerAsync();
        Assert.Equal(300, records.Count);
        Assert.All(records.Values, copies => Assert.Single(copies));
    }

    [Fact]
    public async Task Every_rounds_registry_is_released_when_the_round_ends()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(300), null, Token);

        // Relies on finished async state machines dropping their locals, which holds without a
        // debugger attached. Under a debugger, or with TPL async-debugging tracing on, the boxes keep
        // their state and this test fails; run it without one. The call returns through the
        // continuations of the last round, still on this stack; yielding drops them before the
        // collection.
        await Task.Yield();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Assert.Equal(5, strategy.FinishedRegistries.Count);
        Assert.All(strategy.FinishedRegistries, registry => Assert.False(registry.IsAlive));
    }

    [Fact]
    public async Task A_round_holds_no_registry_of_the_round_before_it_while_it_signs()
    {
        BatchTestNode node = new();
        BatchStrategy? strategy = null;
        bool? previousAlive = null;

        // Checked from inside the second round's first signature, on the stack that round runs on: a
        // round that ran on the continuation stack of the one before it kept that round's registry,
        // and with it every signed blob of the round, reachable from the stack frames below it.
        CheckingSigner payer = new(node.Payer, () =>
        {
            if (previousAlive is not null || strategy!.FinishedRegistries.Count != 2) return;

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            previousAlive = strategy.FinishedRegistries[0].IsAlive;
        });

        await using (strategy = node.Strategy(Capped(64), payer: payer))
        {
            await strategy.SubmitAllAsync(BatchTestNode.Memos(128), null, Token);
        }

        Assert.False(previousAlive);
    }

    [Fact]
    public async Task One_memo_past_a_full_round_goes_as_a_solo_after_it()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(65), null, Token);

        Assert.Equal(8, node.Sink.Transmissions.Count(t => t.TransactionType == "Batch"));
        Assert.Equal("AccountSet", node.Sink.Transmissions[^1].TransactionType);
        Assert.Equal(2, Rounds(strategy));
    }

    [Fact]
    public async Task Candidate_counts_carry_across_rounds_and_a_fourth_candidate_is_refused()
    {
        // Two memos whose inners always end in tec: each round sends them again, never as a solo,
        // until each has been in three candidates; the next round would need a fourth.
        BatchTestNode node = new(sink =>
        {
            sink.InnerResult[MemoLayout.ChunkMarkerHex(100)] = "tecFAKE";
            sink.InnerResult[MemoLayout.ChunkMarkerHex(101)] = "tecFAKE";
        });
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(130), null, Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains(MemoLayout.ChunkMarkerHex(100), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, Rounds(strategy));
    }

    [Fact]
    public async Task The_payers_budget_spans_rounds()
    {
        // Two batches of eight a round at unit 15 cost 165 each: the third batch, in round two, is the last that fits 500.
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(
            Capped(16), new BatchBudget(15, 30, PayerBudgetDrops: 500, WriterBudgetDrops: 10_000));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(32), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Equal(3, node.Sink.Transmissions.Where(t => t.TransactionType == "Batch").Select(t => t.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task Every_round_covers_its_deadlines_with_the_one_fence_of_the_strategy()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(16));

        await strategy.SubmitAllAsync(BatchTestNode.Memos(48), null, Token);

        // Rising deadlines alone would pass with a fresh fence per round; the seam shows there is one.
        Assert.Equal(3, strategy.RoundFences.Count);
        Assert.Single(strategy.RoundFences.Distinct());
        IReadOnlyList<long> recorded = node.Fence.Recorded;
        Assert.Equal(recorded.Order(), recorded);
        Assert.Equal(recorded.Distinct().Count(), recorded.Count);
    }

    [Fact]
    public async Task The_ledger_floor_outlives_a_round()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(16) with { NodeOutageLimit = TimeSpan.FromMilliseconds(300) });
        await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token);

        // From now on every answer comes from a ledger the previous round already read past. A
        // round with a floor of its own would plan from it; the strategy's floor refuses it as a
        // node that is behind, until the outage limit ends the call.
        node.Sink.AutoClose = false;
        node.Ledger.CurrentLedger -= 10;

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token).WaitAsync(TimeSpan.FromSeconds(20), Token));

        Assert.Equal(XrplFileError.NodeUnreachable, error.Error);
    }

    [Fact]
    public async Task A_chunk_pass_keeps_no_hash_and_reports_each_blocks_range()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(Capped(64));

        SubmittedMemos landed = await strategy.SubmitAllAsync(BatchTestNode.Memos(100), null, Token);

        Assert.All(Enumerable.Range(0, 100), index => Assert.False(landed.TryHashOf(index, out _)));
        List<long> ledgers = [.. (await node.LandedByMarkerAsync()).Values.Select(copies => copies[0].LedgerIndex)];
        Assert.Equal(new LedgerRange(ledgers.Min(), ledgers.Max()), Assert.Single(landed.BlockLedgers).Value);
    }

    [Fact]
    public async Task A_manifest_pass_keeps_its_hash_and_ledger()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy();

        SubmittedMemos landed = await strategy.SubmitAllAsync([new MemoSubmission(MemoLayout.ManifestMarkerHex, "7B7D", MemoLayout.JsonFormatHex)], null, Token);

        Assert.True(landed.TryHashOf(0, out string? hash));
        Assert.True(landed.TryLedgerOf(0, out long ledger));
        Assert.Equal(ledger, (await node.Ledger.TransactionAsync(hash, Token))!.LedgerIndex);
    }

    /// <summary>Signs with the inner signer after running a check, on the caller's stack.</summary>
    private sealed class CheckingSigner(ITransactionSigner inner, Action check) : ITransactionSigner
    {
        public string Account => inner.Account;

        public string PublicKey => inner.PublicKey;

        public Task<SignedTransaction> SignAsync(IDictionary<string, object> transaction, CancellationToken cancellationToken)
        {
            check();
            return inner.SignAsync(transaction, cancellationToken);
        }
    }
}
