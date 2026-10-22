using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>The fake the Batch strategy is tested against, held to what the spike measured on the stand.</summary>
public class FakeBatchLedgerSinkTests
{
    private static readonly ITransactionSigner Writer = new WalletSigner(XrplWallet.Generate());
    private static readonly ITransactionSigner Payer = new WalletSigner(XrplWallet.Generate());

    private static MemoSubmission Memo(int index) => new(MemoLayout.ChunkMarkerHex(index), MemoLayout.Hex([(byte)index]), null);

    private static (FakeLedgerSource Ledger, FakeBatchLedgerSink Sink) Node()
    {
        FakeLedgerSource ledger = new();
        FakeBatchLedgerSink sink = new(ledger) { AutoClose = false };
        sink.SetSequence(Writer.Account, 100);
        sink.SetSequence(Payer.Account, 500);
        return (ledger, sink);
    }

    private static async Task<SignedTransaction> BatchAsync(uint payerSequence, uint writerSequence, int[] memos, uint lastLedger = 10_000) =>
        await BatchTransactions.SignAsync(
            BatchTransactions.Outer(Payer.Account, Payer.PublicKey, payerSequence, (memos.Length + 3) * 15L, lastLedger,
                [.. memos.Select((memo, i) => BatchTransactions.Inner(Writer.Account, writerSequence + (uint)i, Memo(memo)))]),
            Writer, Payer, TestContext.Current.CancellationToken);

    private static async Task<List<LedgerTransaction>> HistoryAsync(FakeLedgerSource ledger, string account)
    {
        List<LedgerTransaction> records = [];
        await foreach (LedgerTransaction tx in ledger.AccountTransactionsAsync(account, 0, ledger.CurrentLedger, true, TestContext.Current.CancellationToken))
        {
            records.Add(tx);
        }

        return records;
    }

    [Fact]
    public async Task An_outer_takes_the_payers_sequence_at_submit_and_its_inners_land_at_the_close()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        SignedTransaction batch = await BatchAsync(500, 100, [0, 1, 2]);

        Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(batch.TxBlob, TestContext.Current.CancellationToken));
        Assert.Empty(await HistoryAsync(ledger, Writer.Account));

        sink.CloseLedger();

        List<LedgerTransaction> inners = await HistoryAsync(ledger, Writer.Account);
        Assert.Equal(3, inners.Count);
        Assert.All(inners, inner => Assert.Equal(batch.Hash, inner.ParentBatchID));
        Assert.Equal([100u, 101u, 102u], inners.Select(inner => inner.Sequence).ToArray());

        LedgerSnapshot snapshot = await ledger.SnapshotAsync([Writer.Account, Payer.Account], TestContext.Current.CancellationToken);
        Assert.Equal(103u, snapshot.SequenceOf(Writer.Account));
        Assert.Equal(501u, snapshot.SequenceOf(Payer.Account));
        Assert.Equal(6 * 15L, Assert.Single(await HistoryAsync(ledger, Payer.Account)).Fee);
    }

    [Fact]
    public async Task A_snapshot_answers_for_the_ledger_it_reads_not_for_the_open_one()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        await sink.SubmitBlobAsync((await BatchAsync(500, 100, [0, 1])).TxBlob, TestContext.Current.CancellationToken);

        LedgerSnapshot before = await ledger.SnapshotAsync([Writer.Account, Payer.Account], TestContext.Current.CancellationToken);

        Assert.Equal(100u, before.SequenceOf(Writer.Account));
        Assert.Equal(500u, before.SequenceOf(Payer.Account));
    }

    [Fact]
    public async Task A_later_outer_that_arrives_first_is_held_and_applies_after_the_earlier_one()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        SignedTransaction first = await BatchAsync(500, 100, [0, 1]);
        SignedTransaction second = await BatchAsync(501, 102, [2, 3]);

        Assert.Equal("terPRE_SEQ", await sink.SubmitBlobAsync(second.TxBlob, TestContext.Current.CancellationToken));
        Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(first.TxBlob, TestContext.Current.CancellationToken));
        sink.CloseLedger();

        Assert.Equal(4, (await HistoryAsync(ledger, Writer.Account)).Count);
        Assert.Equal("tefPAST_SEQ", await sink.SubmitBlobAsync(first.TxBlob, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tec_inner_consumes_its_sequence_and_a_vanished_inner_leaves_a_gap()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        sink.InnerResult[MemoLayout.ChunkMarkerHex(1)] = "tecFAKE";
        sink.InnerVanishes.Add(MemoLayout.ChunkMarkerHex(3));

        await sink.SubmitBlobAsync((await BatchAsync(500, 100, [0, 1, 2, 3, 4])).TxBlob, TestContext.Current.CancellationToken);
        sink.CloseLedger();

        List<LedgerTransaction> inners = await HistoryAsync(ledger, Writer.Account);
        Assert.Equal([100u, 101u, 102u], inners.Select(inner => inner.Sequence).ToArray());
        Assert.Equal("tecFAKE", inners[1].Result);
        Assert.Equal(103u, (await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken)).SequenceOf(Writer.Account));
    }

    [Fact]
    public async Task A_foreign_ticket_create_consumes_one_plus_its_count()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        sink.Foreign(Writer.Account, "TicketCreate", 3);
        sink.CloseLedger();

        LedgerTransaction create = Assert.Single(await HistoryAsync(ledger, Writer.Account));
        Assert.Equal("TicketCreate", create.TransactionType);
        Assert.Equal(3u, create.TicketCount);
        Assert.Equal(104u, (await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken)).SequenceOf(Writer.Account));
    }

    [Fact]
    public async Task Every_ledger_read_closes_a_ledger_when_auto_close_is_on()
    {
        FakeLedgerSource ledger = new();
        FakeBatchLedgerSink sink = new(ledger);
        long start = ledger.CurrentLedger;

        await ledger.SnapshotAsync([], TestContext.Current.CancellationToken);
        await ledger.CurrentLedgerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(start + 2, ledger.CurrentLedger);
        Assert.Equal(2, sink.Closes);
    }

    [Fact]
    public async Task An_outer_past_its_deadline_is_refused_at_submit()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        SignedTransaction late = await BatchAsync(500, 100, [0, 1], lastLedger: (uint)ledger.CurrentLedger);

        Assert.Equal("tefMAX_LEDGER", await sink.SubmitBlobAsync(late.TxBlob, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_ledger_is_published_with_its_history_and_its_sequences_in_one_step()
    {
        FakeLedgerSource ledger = new();
        LedgerTransaction record = new("AA", 5, Writer.Account, true, true, "tesSUCCESS", null, null, null) { Sequence = 100 };

        ledger.Publish(5, [record], new Dictionary<string, uint> { [Writer.Account] = 101 });

        Assert.Equal(5, ledger.CurrentLedger);
        Assert.Equal(101u, (await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken)).SequenceOf(Writer.Account));
        Assert.Single(await HistoryAsync(ledger, Writer.Account));
    }

    [Fact]
    public async Task A_reader_racing_closes_never_sees_a_ledger_with_the_previous_ledgers_sequences()
    {
        (FakeLedgerSource ledger, FakeBatchLedgerSink sink) = Node();
        long start = ledger.CurrentLedger;
        using CancellationTokenSource stop = new();
        const int closes = 20_000;

        Task closer = Task.Run(() =>
        {
            for (int i = 0; i < closes; i++)
            {
                sink.Foreign(Writer.Account, "AccountSet");
                sink.CloseLedger();
            }

            stop.Cancel();
        }, TestContext.Current.CancellationToken);

        // Every close adds one ledger and one sequence, so a snapshot at ledger N must read 100 + (N - start).
        Task<int> reader = Task.Run(async () =>
        {
            int reads = 0;

            while (!stop.IsCancellationRequested)
            {
                LedgerSnapshot snapshot = await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken);
                Assert.Equal(100u + (uint)(snapshot.LedgerIndex - start), snapshot.SequenceOf(Writer.Account));
                reads++;
            }

            return reads;
        }, TestContext.Current.CancellationToken);

        await closer;
        Assert.True(await reader > 0);
        Assert.Equal(100u + closes, (await ledger.SnapshotAsync([Writer.Account], TestContext.Current.CancellationToken)).SequenceOf(Writer.Account));
        Assert.Equal(closes, (await HistoryAsync(ledger, Writer.Account)).Count);
    }

    [Fact]
    public void An_inner_can_only_be_scripted_to_end_in_tec()
    {
        (_, FakeBatchLedgerSink sink) = Node();

        Assert.Throws<ArgumentException>(() => sink.InnerResult[MemoLayout.ChunkMarkerHex(0)] = "tesSUCCESS");
        Assert.Throws<ArgumentException>(() => sink.InnerResult[MemoLayout.ChunkMarkerHex(0)] = "tefPAST_SEQ");
    }

    [Fact]
    public async Task An_outer_without_tf_independent_is_refused_loudly()
    {
        (_, FakeBatchLedgerSink sink) = Node();
        Dictionary<string, object> outer = BatchTransactions.Outer(
            Payer.Account, Payer.PublicKey, 500, 3 * 15L, 10_000, [BatchTransactions.Inner(Writer.Account, 100, Memo(0))]);
        outer["Flags"] = 0u;
        SignedTransaction ordered = await BatchTransactions.SignAsync(outer, Writer, Payer, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.SubmitBlobAsync(ordered.TxBlob, TestContext.Current.CancellationToken));
        Assert.Empty(sink.Transmissions);
    }

    [Fact]
    public async Task A_scripted_answer_addresses_the_outer_and_not_a_writer_transaction_that_shares_its_sequence()
    {
        (_, FakeBatchLedgerSink sink) = Node();
        sink.SetSequence(Writer.Account, 500);
        sink.AnswerAt[(500u, 1)] = "telINSUF_FEE_P";
        SignedTransaction solo = await Writer.SignAsync(
            BatchTransactions.Solo(Writer.Account, Writer.PublicKey, 500, 12, 10_000, Memo(0)), TestContext.Current.CancellationToken);

        Assert.Equal("tesSUCCESS", await sink.SubmitBlobAsync(solo.TxBlob, TestContext.Current.CancellationToken));
        Assert.Equal("telINSUF_FEE_P", await sink.SubmitBlobAsync((await BatchAsync(500, 501, [1])).TxBlob, TestContext.Current.CancellationToken));
    }
}
