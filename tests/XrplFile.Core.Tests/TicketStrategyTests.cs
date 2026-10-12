using System.Text.Json;
using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Minting is exact: a batch asks for one ticket per memo it is about to send, because nothing on
/// the XRPL cancels a ticket and an unspent one locks an owner reserve for as long as the account
/// lives. So the queue is normally empty between calls and a second call mints again — what a
/// call carries is where its own sweep starts, not tickets it inherited from the last one.
/// </summary>
public class TicketStrategyTests
{
    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    private static IReadOnlyList<MemoSubmission> Memos(int count, int from = 0) =>
        [.. Enumerable.Range(from, count).Select(i => new MemoSubmission(MemoLayout.ChunkMarkerHex(i), MemoLayout.Hex([(byte)i]), null))];

    private static TicketStrategy Strategy(FakeLedgerSink sink, FakeLedgerSource ledger, int ticketsPerBatch = 100) =>
        new(sink, ledger, Signer, new TicketStrategyOptions
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
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
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
    public async Task A_mint_that_validated_and_failed_is_reported_rather_than_waited_out()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 0 is the mint. A TicketCreate can validate and still fail —
        // tecINSUFFICIENT_RESERVE is what 250 tickets ask of an account that cannot cover fifty
        // XRP of owner reserve — and then it moves the sequence by one instead of by the ticket
        // count and one. Watching for the larger step spins out the whole validation timeout and
        // calls a validated failure a timeout.
        sink.FailsAs.Add(0);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Empty(sink.LiveTickets);
        // 250 tickets is fifty XRP of owner reserve at the current increment — an operator needs
        // to see which code the node actually gave, not just that something was refused.
        Assert.Contains("tecINSUFFICIENT_RESERVE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unvalidated_mint_answer_is_not_reported_as_a_refusal()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 0 is the mint. The node holds it but has not validated it yet when the mint is
        // looked up by hash — meta, and so Succeeded, is absent in that shape. Reading an absent
        // Succeeded as a failure used to abandon a perfectly good mint, along with every ticket it
        // made, and report "validated in ledger 0 and failed" for a transaction that was never
        // actually validated at all.
        sink.UnvalidatedAt.Add(0);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
    }

    [Fact]
    public async Task A_second_call_whose_mint_is_unvalidated_at_lookup_reports_its_own_hash_not_the_firsts()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 1);

        // A first publication that lands cleanly: position 0 is its mint, position 1 its one memo.
        IReadOnlyList<SubmittedMemo> first = await strategy.SubmitAllAsync(Memos(1), null, TestContext.Current.CancellationToken);

        // Position 2 is the second call's mint. The node holds it but has not validated it yet when
        // it is looked up by hash, so EnsureTicketsAsync's firstMintLedger falls back to "the ledger
        // already open" exactly as SubmitAllAsync's own no-mint case does, and needs the same +1:
        // without it, this fallback can name the very ledger the first call's identically-marked
        // memo already landed at.
        sink.UnvalidatedAt.Add(2);

        // The same marker as the first call's memo — a retried publication reusing the same chunk
        // marker, which is the collision this floor exists to prevent. No ledger is closed between
        // the two calls on purpose: that is what leaves the first memo sitting at the exact ledger
        // this fallback would anchor at without the +1.
        IReadOnlyList<SubmittedMemo> second = await strategy.SubmitAllAsync(Memos(1), null, TestContext.Current.CancellationToken);

        Assert.Single(second);
        Assert.NotEqual(first[0].TxHash, second[0].TxHash);
    }

    [Fact]
    public async Task A_call_that_mints_nothing_does_not_match_the_ledger_that_was_already_open()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 0 is the mint and 1..3 the three memos: the last is a node call that fails
        // after its ticket left the queue and before any ledger could have seen the submission —
        // exactly A_submission_that_throws_hands_its_ticket_back_rather_than_stranding_it's setup,
        // which is what leaves one ticket sitting in the queue for the call below to find without
        // minting. Marker 0 (the first call's first memo) already landed cleanly before the throw.
        sink.Throws[3] = new InvalidOperationException("fake: the socket died mid-submission");
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken));
        sink.Throws.Clear();

        // A ledger closes between two publications, as one always does. This second call reuses
        // the same first marker — a retried publication — and has a ticket already in hand, so it
        // mints nothing.
        long ledgerAlreadyOpenWhenTheCallStarted = ledger.CurrentLedger;
        sink.CloseLedger();

        IReadOnlyList<SubmittedMemo> second = await strategy.SubmitAllAsync(Memos(1), null, TestContext.Current.CancellationToken);

        Assert.Single(second);

        LedgerTransaction? landedTx = await ledger.TransactionAsync(second[0].TxHash, TestContext.Current.CancellationToken);
        Assert.NotNull(landedTx);
        // The floor is one past the ledger that was already open when this no-mint call started:
        // nothing it submits can itself land at or before that ledger. Anchoring at that ledger
        // instead of one past it is exactly what let this call's sweep match marker 0's earlier,
        // unrelated landing from the first call instead of its own.
        Assert.True(landedTx!.LedgerIndex > ledgerAlreadyOpenWhenTheCallStarted);
    }

    [Theory]
    [InlineData("telINSUF_FEE_P")]
    [InlineData("telCAN_NOT_QUEUE_FULL")]
    [InlineData("terINSUF_FEE_B")]
    [InlineData("terRETRY")]
    public async Task A_refusal_that_never_reached_a_ledger_hands_its_ticket_back(string refusal)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 0 is the mint, so position 1 is the first memo. None of these codes reaches a
        // ledger, so the ticket the submission named is untouched and still the account's — and
        // nothing on the XRPL cancels a ticket, so one that is not handed back is lost for good.
        sink.Refusals[1] = refusal;
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, landed.Count);
        // Three tickets covered three memos and a retry: no second mint was needed.
        Assert.Equal(1, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_submission_that_throws_hands_its_ticket_back_rather_than_stranding_it()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Position 0 is the mint and 1..3 the three memos: the last is a node call that fails
        // after its ticket left the queue and before any ledger could have seen the submission.
        sink.Throws[3] = new InvalidOperationException("fake: the socket died mid-submission");
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken));

        sink.Throws.Clear();

        // A ledger closes between two publications, as one always does — and a no-mint call's
        // sweep anchors one past whatever ledger was already open when it started, so its own
        // submission must land after that, not inside it.
        sink.CloseLedger();

        // The publication that follows finds that ticket in the queue rather than minting for a
        // reserve the account is already paying.
        await strategy.SubmitAllAsync(Memos(1, from: 3), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, sink.Events.Count(kind => kind == "mint"));
    }

    [Fact]
    public async Task A_second_publication_reports_its_own_hashes_not_the_first_publications()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 4);

        IReadOnlyList<SubmittedMemo> first = await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        // A ledger closes between two publications, as one always does.
        sink.CloseLedger();

        IReadOnlyList<SubmittedMemo> second = await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        // Chunk markers carry no publication scope — xf/c/0 is xf/c/0 for every file this account
        // ever published — so a sweep anchored before the first publication matches the first
        // publication's transactions and reports their hashes for memos this call never landed.
        Assert.Equal(2, second.Count);
        Assert.Empty(second.Select(memo => memo.TxHash).Intersect(first.Select(memo => memo.TxHash), StringComparer.Ordinal));
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
        // Three memos, three transactions (the mint carries a mark of its own, so it is not counted).
        // A queued one re-sent would make four.
        Assert.Equal(3, sink.Submitted.Count(tx => (string)tx["TransactionType"] != "TicketCreate" && tx.ContainsKey("Memos")));
    }

    [Fact]
    public async Task A_memo_that_never_lands_is_reported_rather_than_retried_forever()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        // Every attempt at this memo lands with a tec, so no attempt ever writes it.
        FakeLedgerSink.FailEvery failing = new(Marker: MemoLayout.ChunkMarkerHex(1));
        sink.FailMarkers.Add(failing.Marker);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
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
    public async Task A_second_call_mints_only_what_it_needs()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 10);

        await strategy.SubmitAllAsync(Memos(3), null, TestContext.Current.CancellationToken);
        sink.CloseLedger();
        await strategy.SubmitAllAsync(Memos(2, from: 3), null, TestContext.Current.CancellationToken);

        uint[] minted =
        [
            .. sink.Submitted
                .Where(tx => (string)tx["TransactionType"] == "TicketCreate")
                .Select(tx => Convert.ToUInt32(tx["TicketCount"])),
        ];

        // Three memos, then two: two mints, each for exactly the memos it is about to carry. The
        // first call leaves nothing behind because it never asked for anything it could not spend.
        Assert.Equal(new uint[] { 3, 2 }, minted);
    }

    [Fact]
    public async Task No_more_submissions_are_in_flight_than_the_option_allows()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions
        {
            TicketsPerBatch = 40,
            MaxInFlight = 4,
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(30),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

        await strategy.SubmitAllAsync(Memos(40), null, TestContext.Current.CancellationToken);

        // The lower bound matters as much as the upper one: without genuine overlap the gate is
        // never contended and the assertion above it would hold for a strategy that has no gate.
        Assert.InRange(sink.PeakInFlight, 2, 4);
    }

    [Fact]
    public async Task A_mint_leaves_room_for_the_tickets_the_account_already_holds()
    {
        // Two cancelled runs at the default batch of a hundred leave 200 tickets live on the
        // account: nothing on the XRPL cancels a ticket, and DisposeAsync only drops the queue. A
        // fresh instance knows nothing of them, and a mint of a hundred asked against its own
        // bookkeeping takes the account past the protocol's 250 ceiling — tecDIR_FULL, on a run
        // that has 50 tickets of real headroom to work with.
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, 200);

        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 100);

        IReadOnlyList<SubmittedMemo> landed = await strategy.SubmitAllAsync(Memos(100), null, TestContext.Current.CancellationToken);

        Assert.Equal(100, landed.Count);
        Assert.Equal([.. Enumerable.Range(0, 100)], landed.Select(memo => memo.Index).Order().ToArray());

        // Every mint asked for what the account had room for and no more, so none of them failed.
        uint[] minted =
        [
            .. sink.Submitted
                .Where(tx => (string)tx["TransactionType"] == "TicketCreate")
                .Select(tx => Convert.ToUInt32(tx["TicketCount"])),
        ];

        Assert.NotEmpty(minted);
        Assert.All(minted, count => Assert.InRange(count, 1u, 50u));

        // The leaked 200 are still there — this run neither spent them nor added to them.
        Assert.Equal(200, ledger.TicketCounts[Wallet.ClassicAddress]);
    }

    [Fact]
    public async Task An_account_already_at_the_ceiling_is_refused_by_name()
    {
        // 250 live tickets and no room at all. There is nothing to do but say so: the mint cannot
        // succeed, and blaming an owner reserve — which is what a failed mint is read as — sends a
        // person looking for money that would not help.
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, TicketStrategy.MaxLiveTickets);

        await using TicketStrategy strategy = Strategy(sink, ledger, ticketsPerBatch: 100);

        XrplFileException error = await Assert.ThrowsAsync<XrplFileException>(
            () => strategy.SubmitAllAsync(Memos(10), null, TestContext.Current.CancellationToken));

        Assert.Equal(XrplFileError.SubmitRefused, error.Error);
        Assert.Contains("250", error.Message, StringComparison.Ordinal);
        Assert.Contains("live tickets", error.Message, StringComparison.Ordinal);

        // Not one mint was attempted: the ledger already said there was no room.
        Assert.DoesNotContain("mint", sink.Events);
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

    [Fact]
    public async Task Every_mint_carries_the_librarys_mark_and_nothing_else()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.SubmitAllAsync(Memos(2), null, TestContext.Current.CancellationToken);

        Dictionary<string, object> mint = sink.Submitted.Single(tx => (string)tx["TransactionType"] == "TicketCreate");
        JsonElement memo = Assert.Single(JsonSerializer.SerializeToElement(mint["Memos"]).EnumerateArray()).GetProperty("Memo");

        Assert.Equal(MemoLayout.TicketMarkerHex, memo.GetProperty("MemoType").GetString());
        Assert.False(memo.TryGetProperty("MemoData", out _));
        Assert.False(memo.TryGetProperty("MemoFormat", out _));
    }
}
