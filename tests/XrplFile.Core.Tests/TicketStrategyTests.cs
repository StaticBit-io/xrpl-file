using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

public class TicketStrategyTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();

    private static IReadOnlyList<MemoSubmission> Memos(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i]), null))];

    private static TicketStrategy Strategy(FakeLedgerSink sink, FakeLedgerSource ledger, int ticketsPerBatch = 100) =>
        new(sink, ledger, Wallet, new TicketStrategyOptions
        {
            TicketsPerBatch = ticketsPerBatch,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(5),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public async Task Every_memo_lands_and_reports_the_hash_that_carried_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken);

        Assert.Equal([0, 1, 2, 3, 4], landed.Select(memo => memo.Index).Order().ToArray());
        foreach (SubmittedMemo memo in landed)
        {
            LedgerTransaction? tx = await ledger.TransactionAsync(memo.TxHash, TestContext.Current.CancellationToken);
            Assert.NotNull(tx);
            Assert.Equal(MemoLayout.ChunkMarkerHex(memo.Index), tx!.MemoTypeHex);
        }
    }

    [Fact]
    public async Task A_mint_comes_first_and_the_memos_ride_the_tickets_it_made()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal("mint", sink.Events[0]);
        Assert.Equal("TicketCreate", sink.Submitted[0]["TransactionType"]);
        Assert.Equal(3u, Convert.ToUInt32(sink.Submitted[0]["TicketCount"]));

        // The mint sat at sequence 10, so the tickets are 11, 12 and 13 and nothing rides a sequence.
        uint[] ridden = [.. sink.Submitted.Skip(1).Select(tx => Convert.ToUInt32(tx["TicketSequence"])).Order()];
        Assert.Equal(new uint[] { 11, 12, 13 }, ridden);
        Assert.All(sink.Submitted.Skip(1), tx => Assert.Equal(0u, Convert.ToUInt32(tx["Sequence"])));
    }

    [Fact]
    public async Task A_batch_never_mints_more_than_it_needs()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 100);

        await strategy.SubmitAllAsync(Memos(4), null, TestContext.Current.CancellationToken);

        // Four memos, four tickets. A leftover ticket costs an owner reserve until it is spent.
        Assert.Equal(4u, Convert.ToUInt32(sink.Submitted[0]["TicketCount"]));
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task More_memos_than_a_batch_holds_are_minted_for_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 2);

        await strategy.SubmitAllAsync(Memos(5), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_batch_is_never_larger_than_the_protocol_allows()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = new(sink, ledger, Wallet, new TicketStrategyOptions
        {
            TicketsPerBatch = 5000,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await strategy.SubmitAllAsync(Memos(300), null, TestContext.Current.CancellationToken);

        Assert.All(
            sink.Submitted.Where(tx => (string)tx["TransactionType"] == "TicketCreate"),
            tx => Assert.InRange(Convert.ToUInt32(tx["TicketCount"]), 1u, (uint)TicketStrategy.MaxTicketsPerMint));
    }

    [Fact]
    public async Task A_chunk_that_landed_with_a_tec_is_retried_on_a_fresh_ticket()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 2 is the second memo: position 0 is the mint, position 1 the first memo.
        sink.FailsAs.Add(2);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // A second mint had to happen: the failed transaction ate its ticket without writing.
        Assert.Equal(2, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_malformed_submission_hands_its_ticket_back_rather_than_wasting_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // A tef never reaches a ledger, so the ticket it named is still live and still ours.
        sink.Refusals[1] = "tefBAD_AUTH";
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // Three tickets covered three memos and a retry: no second mint was needed.
        Assert.Equal(1, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_queued_transaction_is_waited_for_rather_than_sent_twice()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // The node answers terQUEUED and the transaction appears in the ledger a sweep later.
        sink.QueuedUntilSweep[1] = 1;
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // Three memos, three transactions. A queued one re-sent would make four.
        Assert.Equal(3, sink.Submitted.Count(tx => tx.ContainsKey("Memos")));
    }

    [Fact]
    public async Task A_memo_that_never_lands_is_reported_rather_than_retried_forever()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Every attempt at this memo lands with a tec, so no attempt ever writes it.
        FakeLedgerSink.FailEvery failing = new(Marker: MemoLayout.ChunkMarkerHex(1));
        sink.FailMarkers.Add(failing.Marker);
        await using TicketStrategy strategy = new(sink, ledger, Wallet, new TicketStrategyOptions
        {
            TicketsPerBatch = 3,
            MaxAttemptsPerMemo = 2,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(2),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains(MemoLayout.ChunkMarkerHex(1), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unspent_tickets_survive_into_the_next_call()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 10);

        await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);
        int mintsAfterFirst = sink.Events.Count(kind => kind == "mint");

        // A second phase — the block list, then the manifest — must not mint again while the
        // first phase's tickets are still live.
        await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        Assert.Equal(mintsAfterFirst, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task Progress_counts_memos_that_landed_not_memos_that_were_sent()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        List<int> seen = [];
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 4);

        await strategy.SubmitAllAsync(Memos(4), new SynchronousProgress<int>(seen.Add), TestContext.Current.CancellationToken);

        Assert.Equal(4, seen.Max());
    }
}
