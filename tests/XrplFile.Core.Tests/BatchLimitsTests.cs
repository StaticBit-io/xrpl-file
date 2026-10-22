using System.Runtime.CompilerServices;
using Xunit;

namespace XrplFile.Tests;

public class BatchLimitsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transport_retries_of_one_blob_are_one_candidate()
    {
        BatchTestNode node = new(sink =>
        {
            sink.AnswerAt[(BatchTestNode.PayerStart, 1)] = "telCAN_NOT_QUEUE";
            sink.AnswerAt[(BatchTestNode.PayerStart, 2)] = "error:tooBusy";
            sink.AnswerAt[(BatchTestNode.PayerStart, 3)] = "telCAN_NOT_QUEUE";
        });
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAttemptsPerMemo = 1, ProgressSweep = TimeSpan.FromSeconds(1) });

        Assert.Equal(8, (await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token)).Count);
    }

    [Fact]
    public async Task A_tec_outer_is_a_candidate_however_little_it_wrote()
    {
        BatchTestNode node = new(sink => sink.OuterResult[BatchTestNode.PayerStart] = "tecFAKE");
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.FastOptions with { MaxAttemptsPerMemo = 1, ProgressSweep = TimeSpan.FromSeconds(1) });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
    }

    [Fact]
    public async Task A_memo_that_would_need_a_fourth_candidate_fails_the_run_and_progress_never_counted_it()
    {
        BatchTestNode node = new(sink =>
        {
            sink.InnerResult[MemoLayout.ChunkMarkerHex(0)] = "tecFAKE";
            sink.InnerResult[MemoLayout.ChunkMarkerHex(1)] = "tecFAKE";
        });
        List<int> reports = [];
        await using BatchStrategy strategy = node.Strategy(BatchTestNode.ProgressOptions);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(
            BatchTestNode.Memos(16), new SynchronousProgress<int>(value => { lock (reports) reports.Add(value); }), Token));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Equal(2, node.Sink.Transmissions.Count(t => t.Answer == "tesSUCCESS" && t.Inners == 2));
        Assert.Equal(14, reports.Max());
        Assert.Equal(reports.OrderBy(value => value), reports);
    }

    [Fact]
    public async Task Progress_reports_memos_as_sweeps_find_them_before_the_round_settles()
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(5);
        List<(int Value, bool Settled)> reports = [];
        BatchStrategy strategy = node.Strategy(BatchTestNode.ProgressOptions);
        await using (strategy)
        {
            await strategy.SubmitAllAsync(BatchTestNode.Memos(200), new SynchronousProgress<int>(value =>
            {
                lock (reports) reports.Add((value, strategy.Trace.Any(entry => entry.StartsWith("settled:", StringComparison.Ordinal))));
            }), Token);
        }

        Assert.Contains(reports, report => !report.Settled && report.Value > 0);
        Assert.Equal(200, reports[^1].Value);
        Assert.Equal(reports.Select(r => r.Value).OrderBy(v => v), reports.Select(r => r.Value));
    }

    [Theory]
    [InlineData(typeof(XrplFileException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FormatException))]
    public async Task A_progress_read_that_fails_does_not_change_the_runs_outcome(Type failure)
    {
        BatchTestNode node = new();
        node.Sink.SubmitDelay = TimeSpan.FromMilliseconds(5);
        BatchStrategy? holder = null;
        ProgressFailingSource source = new(node.Ledger, () => holder is { } running
            && !running.Trace.Any(entry => entry.StartsWith("settled:", StringComparison.Ordinal)), failure);
        holder = new BatchStrategy(node.Sink, source, node.Writer, node.Payer, BatchTestNode.Budget, node.Fence, BatchTestNode.ProgressOptions);

        await using (holder)
        {
            Assert.Equal(40, (await holder.SubmitAllAsync(BatchTestNode.Memos(40), null, Token)).Count);
        }

        Assert.True(source.Failures > 0, "the progress reads were meant to fail at least once");
    }

    [Fact]
    public async Task No_batch_is_signed_past_the_payers_budget()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 100, WriterBudgetDrops: 10_000));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public async Task No_solo_is_signed_past_the_writers_budget()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 10_000, WriterBudgetDrops: 10));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Empty(node.Sink.Transmissions);
    }

    [Fact]
    public async Task The_payers_budget_accumulates_across_the_batches_of_a_round()
    {
        // Two batches of eight inners at unit 15 cost 11 x 15 = 165 each, 330 together.
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 329, WriterBudgetDrops: 10_000));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Single(node.Fence.Recorded);
        Assert.All(node.Sink.Transmissions, t => Assert.Equal(BatchTestNode.PayerStart, t.Sequence));
        Assert.True(node.Sink.Transmissions.Count <= 1, "only the first batch may have been transmitted");
    }

    [Fact]
    public async Task A_payers_budget_exactly_equal_to_two_batches_signs_both()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 330, WriterBudgetDrops: 10_000));

        Assert.Equal(16, (await strategy.SubmitAllAsync(BatchTestNode.Memos(16), null, Token)).Count);
        Assert.Equal(2, node.Sink.Transmissions.Count(t => t.TransactionType == "Batch"));
    }

    [Fact]
    public async Task The_payers_budget_accumulates_across_the_passes_of_a_session()
    {
        // One batch of two inners costs 5 x 15 = 75; the strategy lives for the session, so a second pass adds to the first.
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 149, WriterBudgetDrops: 10_000));

        Assert.Equal(2, (await strategy.SubmitAllAsync(BatchTestNode.Memos(2), null, Token)).Count);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(2), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Single(node.Sink.Transmissions);
    }

    [Fact]
    public async Task The_writers_budget_accumulates_across_solos_and_the_second_solo_is_refused()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 10_000, WriterBudgetDrops: 29));

        Assert.Single(await strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));

        Assert.Equal(XrplFileError.BudgetExceeded, error.Error);
        Assert.Single(node.Sink.Transmissions);
    }

    [Fact]
    public async Task A_writers_budget_exactly_equal_to_two_solos_signs_both()
    {
        BatchTestNode node = new();
        await using BatchStrategy strategy = node.Strategy(budget: new BatchBudget(15, 30, PayerBudgetDrops: 10_000, WriterBudgetDrops: 30));

        Assert.Single(await strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));
        Assert.Single(await strategy.SubmitAllAsync(BatchTestNode.Memos(1), null, Token));
        Assert.Equal(2, node.Sink.Transmissions.Count(t => t.TransactionType == "AccountSet"));
    }

    [Fact]
    public async Task A_unit_above_the_ceiling_waits_for_fees_to_fall_and_proceeds_when_they_do()
    {
        BatchTestNode node = new();
        node.Ledger.OpenLedgerFeeScript.Enqueue(new OpenLedgerFee(100, 10));
        await using BatchStrategy strategy = node.Strategy();

        Assert.Equal(8, (await strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token)).Count);
        Assert.Contains("fee-wait:150", strategy.Trace);
    }

    [Fact]
    public async Task A_unit_still_above_the_ceiling_after_the_wait_fails_with_FeeTooHigh_and_sends_nothing()
    {
        BatchTestNode node = new();
        node.Ledger.OpenLedgerFee = new OpenLedgerFee(100, 10);
        await using BatchStrategy strategy = node.Strategy();

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(() => strategy.SubmitAllAsync(BatchTestNode.Memos(8), null, Token));

        Assert.Equal(XrplFileError.FeeTooHigh, error.Error);
        Assert.Empty(node.Sink.Transmissions);
    }

    /// <summary>A source whose history reads fail with <paramref name="failure"/> while <c>failing</c> says so.</summary>
    private sealed class ProgressFailingSource(FakeLedgerSource inner, Func<bool> failing, Type failure) : ILedgerSource
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public async IAsyncEnumerable<LedgerTransaction> AccountTransactionsAsync(
            string account, long fromLedger, long toLedger, bool forward, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (failing())
            {
                Interlocked.Increment(ref _failures);
                throw failure == typeof(XrplFileException)
                    ? new XrplFileException(XrplFileError.NodeLacksHistory, "the progress read failed")
                    : (Exception)Activator.CreateInstance(failure, "the progress read failed")!;
            }

            await foreach (LedgerTransaction tx in inner.AccountTransactionsAsync(account, fromLedger, toLedger, forward, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return tx;
            }
        }

        public Task<LedgerTransaction?> TransactionAsync(string hash, CancellationToken cancellationToken) =>
            inner.TransactionAsync(hash, cancellationToken);

        public Task<long?> EarliestLedgerAsync(CancellationToken cancellationToken) => inner.EarliestLedgerAsync(cancellationToken);

        public Task<NetworkFees> FeesAsync(CancellationToken cancellationToken) => inner.FeesAsync(cancellationToken);

        public Task<long> BalanceDropsAsync(string account, CancellationToken cancellationToken) =>
            inner.BalanceDropsAsync(account, cancellationToken);

        public Task<AccountState> AccountStateAsync(string account, CancellationToken cancellationToken) =>
            inner.AccountStateAsync(account, cancellationToken);

        public Task<IReadOnlyList<LiveTicket>> LiveTicketsAsync(string account, CancellationToken cancellationToken) =>
            inner.LiveTicketsAsync(account, cancellationToken);

        public Task<long> CurrentLedgerAsync(CancellationToken cancellationToken) => inner.CurrentLedgerAsync(cancellationToken);

        public Task<LedgerSnapshot> SnapshotAsync(IReadOnlyList<string> accounts, CancellationToken cancellationToken) =>
            inner.SnapshotAsync(accounts, cancellationToken);

        public Task<OpenLedgerFee> OpenLedgerFeeAsync(CancellationToken cancellationToken) => inner.OpenLedgerFeeAsync(cancellationToken);

        public Task<bool> IsAmendmentEnabledAsync(string name, CancellationToken cancellationToken) =>
            inner.IsAmendmentEnabledAsync(name, cancellationToken);

        public Task<DestinationState> DestinationAsync(string account, CancellationToken cancellationToken) =>
            inner.DestinationAsync(account, cancellationToken);
    }
}
