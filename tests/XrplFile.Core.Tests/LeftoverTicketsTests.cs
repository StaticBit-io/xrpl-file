using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Tests;

/// <summary>
/// Which of an account's live tickets this library may touch (spec I2, I3). Everything here errs
/// toward leaving a ticket alone: the cost of a wrong "ours" is somebody else's prepared
/// transaction, the cost of a wrong "foreign" is one owner reserve until the account is closed.
/// </summary>
public class LeftoverTicketsTests
{
    private const string Account = "rWriter";

    private static string Hash(char fill) => new(fill, 64);

    private static LedgerTransaction Mint(string hash, string account = Account, string? memoType = null, bool succeeded = true, bool validated = true) =>
        new(hash, 100, account, succeeded, validated, succeeded ? "tesSUCCESS" : "tecINSUFFICIENT_RESERVE", memoType, null, null);

    [Fact]
    public async Task Only_tickets_from_a_marked_mint_by_this_account_are_ours()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));
        ledger.Add(Mint(Hash('B')));
        ledger.Add(Mint(Hash('C'), account: "rSomebodyElse", memoType: MemoLayout.TicketMarkerHex));
        ledger.Add(Mint(Hash('D'), memoType: MemoLayout.TicketMarkerHex, succeeded: false));
        ledger.Add(Mint(Hash('E'), memoType: MemoLayout.TicketMarkerHex, validated: false));
        ledger.SetLiveTickets(Account,
        [
            new LiveTicket(12, Hash('A')), new LiveTicket(11, Hash('A')),
            new LiveTicket(20, Hash('B')),
            new LiveTicket(30, Hash('C')),
            new LiveTicket(40, Hash('D')),
            new LiveTicket(50, Hash('E')),
            new LiveTicket(60, FakeLedgerSink.UnknownCreator),
        ]);

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        Assert.Equal([11u, 12u], census.Ours);
        Assert.Equal(5, census.Others);
    }

    [Fact]
    public async Task One_mint_is_looked_up_once_however_many_tickets_it_made()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));
        ledger.SetLiveTickets(Account, [.. Enumerable.Range(1, 5).Select(i => new LiveTicket((uint)i, Hash('A')))]);

        await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, ledger.TransactionLookups);
    }

    [Fact]
    public async Task A_verdict_already_in_the_cache_is_not_looked_up_again()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));
        ledger.SetLiveTickets(Account, [new LiveTicket(1, Hash('A'))]);
        Dictionary<string, bool> verdicts = new(StringComparer.OrdinalIgnoreCase);

        await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);
        TicketCensus second = await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);

        Assert.Equal(1, ledger.TransactionLookups);
        Assert.Equal([1u], second.Ours);
    }

    [Fact]
    public async Task A_foreign_verdict_is_cached_too_and_costs_no_second_lookup()
    {
        FakeLedgerSource ledger = new();
        ledger.Add(Mint(Hash('B')));
        ledger.SetLiveTickets(Account, [new LiveTicket(1, Hash('B'))]);
        Dictionary<string, bool> verdicts = new(StringComparer.OrdinalIgnoreCase);

        await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);
        TicketCensus second = await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);

        Assert.Equal(1, ledger.TransactionLookups);
        Assert.Empty(second.Ours);
        Assert.Equal(1, second.Others);
    }

    [Fact]
    public async Task A_creator_the_node_could_not_show_stays_foreign_for_as_long_as_the_cache_lives()
    {
        FakeLedgerSource ledger = new();
        ledger.SetLiveTickets(Account, [new LiveTicket(1, Hash('A'))]);
        Dictionary<string, bool> verdicts = new(StringComparer.OrdinalIgnoreCase);

        TicketCensus first = await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);

        // The node recovers its history: the mint is there now, and marked.
        ledger.Add(Mint(Hash('A'), memoType: MemoLayout.TicketMarkerHex));

        TicketCensus cached = await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);
        TicketCensus fresh = await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        // Conservative on purpose (spec I3): the cached "could not be shown" is not revisited, and a
        // caller that wants the recovered answer starts a new cache.
        Assert.Empty(first.Ours);
        Assert.Empty(cached.Ours);
        Assert.Equal([1u], fresh.Ours);
        Assert.Equal(2, ledger.TransactionLookups);
    }

    [Fact]
    public async Task A_cache_built_with_the_ignore_case_comparer_answers_for_a_creator_in_either_case()
    {
        FakeLedgerSource ledger = new();
        ledger.SetLiveTickets(Account, [new LiveTicket(1, Hash('A'))]);
        Dictionary<string, bool> verdicts = new(StringComparer.OrdinalIgnoreCase) { [Hash('a')] = true };

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Account, verdicts, TestContext.Current.CancellationToken);

        Assert.Equal([1u], census.Ours);
        Assert.Equal(0, ledger.TransactionLookups);
    }

    [Fact]
    public async Task An_account_with_no_tickets_has_an_empty_census_and_costs_no_lookup()
    {
        FakeLedgerSource ledger = new();

        TicketCensus census = await LeftoverTickets.FindAsync(ledger, Account, null, TestContext.Current.CancellationToken);

        Assert.Empty(census.Ours);
        Assert.Equal(0, census.Others);
        Assert.Equal(0, ledger.TransactionLookups);
    }

    private static readonly XrplWallet Wallet = XrplWallet.Generate();
    private static readonly ITransactionSigner Signer = new WalletSigner(Wallet);

    private static TicketStrategy Strategy(FakeLedgerSink sink, FakeLedgerSource ledger) =>
        new(sink, ledger, Signer, new TicketStrategyOptions
        {
            SweepPoll = TimeSpan.FromMilliseconds(1),
            BatchTimeout = TimeSpan.FromSeconds(5),
            ValidationPoll = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public async Task The_tidy_up_spends_this_librarys_tickets_on_transactions_that_do_nothing()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        IReadOnlyList<uint> unmarked = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: false);
        sink.LeakTickets(Wallet.ClassicAddress, 2);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(3, 0, 0) { Charged = 3 }, release);
        Assert.Equal([unmarked[0], 1_000_000u, 1_000_001u], sink.LiveTickets.Order().ToArray());

        Assert.Equal(3, sink.Submitted.Count);
        Assert.All(sink.Submitted, tx =>
        {
            Assert.Equal("AccountSet", tx["TransactionType"]);
            Assert.Contains(Convert.ToUInt32(tx["TicketSequence"]), ours);
            Assert.False(tx.ContainsKey("Memos"));
            Assert.False(tx.ContainsKey("SetFlag"));
            Assert.False(tx.ContainsKey("ClearFlag"));
        });
    }

    [Fact]
    public async Task An_account_with_no_leftovers_sees_no_tidy_up_transaction()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeakTickets(Wallet.ClassicAddress, 2);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TicketRelease.None, release);
        Assert.Empty(sink.Submitted);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_ticket_the_node_calls_gone_but_the_ledger_still_lists_is_not_released()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        sink.AnswersGone.Add(ours[0]);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The node's tefNO_TICKET alone frees nothing: the validated ledger still lists the ticket,
        // so its reserve is still locked (spec 4.6, I5). The ticket is never sent again, so nothing
        // is left to wait for and the tidy-up ends on its own rather than at its deadline.
        Assert.Equal(new TicketRelease(1, 0, 1) { Charged = 1 }, release);
        Assert.Contains(ours[0], sink.LiveTickets);
    }

    [Fact]
    public async Task A_ticket_the_node_calls_gone_and_the_ledger_no_longer_lists_is_already_gone()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.AnswersGone.Add(ours[0]);
        sink.ConsumedOnGoneAnswer.Add(ours[0]);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 1, 0), release);
        Assert.Empty(sink.LiveTickets);
    }

    [Theory(Timeout = 10_000)]
    [InlineData("telCAN_NOT_QUEUE")]
    [InlineData("temMALFORMED")]
    public async Task A_refused_spends_ticket_that_somebody_else_consumes_is_already_gone_and_not_sent_again(string refusal)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger);
        IReadOnlyList<uint> ours = fake.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        ConsumedWhileRefusedSink sink = new(fake, ledger, ours[0], refusal);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The refusal claimed no fee and left the ticket to be sent again, but the next listing no
        // longer shows it: something else consumed it, its reserve is free, and sending a spend for
        // it would only be refused tefNO_TICKET.
        Assert.Equal(new TicketRelease(0, 1, 0), release);
        Assert.Equal(1, sink.Sent);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(XrplFileException))]
    public async Task A_failure_that_is_not_an_unreachable_node_ends_the_tidy_up(Type failure)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);

        // A signer that refuses, a locked vault: only an unreachable node is survivable (spec 4.6).
        sink.Throws[0] = failure == typeof(XrplFileException)
            ? new XrplFileException(XrplFileError.SubmitRefused, "fake: the signer refused")
            : new InvalidOperationException("fake: the signer refused");
        await using TicketStrategy strategy = Strategy(sink, ledger);

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken));

        Assert.IsType(failure, thrown);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_census_cut_short_by_the_deadline_reads_the_same_as_nothing_to_tidy_up()
    {
        FakeLedgerSource ledger = new() { LiveTicketsFailures = int.MaxValue };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        // Documented on TicketRelease and PublicationReport: zeros do not prove the account is clean.
        // The two cases are the same value, and this pins that they are, so nobody reads one into the other.
        Assert.Equal(TicketRelease.None, release);
        Assert.Empty(sink.Submitted);
        Assert.Equal(2, sink.LiveTickets.Count);
    }

    [Fact]
    public async Task A_spend_that_validates_as_failed_is_not_a_release_but_the_ticket_is_gone()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.FailsAs.Add(0);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // A tec consumes the ticket without succeeding: nothing short of tesSUCCESS is "released",
        // and a ticket the ledger no longer lists holds no reserve however it went (spec 4.6).
        Assert.Equal(new TicketRelease(0, 1, 0) { Charged = 1 }, release);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_spend_the_node_refuses_once_is_sent_again_and_released()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "telCAN_NOT_QUEUE";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // A tel left the ticket intact and claimed no fee, so the next round sends it again (spec 4.6).
        Assert.Equal(new TicketRelease(1, 0, 0) { Charged = 1 }, release);
        Assert.DoesNotContain(ours[0], sink.LiveTickets);
        Assert.Equal(2, sink.Submitted.Count(tx => (string)tx["TransactionType"] == "AccountSet"));
    }

    [Fact]
    public async Task A_spend_the_node_refuses_leaves_the_ticket_not_released()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        for (int attempt = 0; attempt < 5; attempt++) sink.Refusals[attempt] = "telINSUF_FEE_P";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 0, 1), release);
        Assert.Contains(ours[0], sink.LiveTickets);
        Assert.Equal(5, sink.Submitted.Count);
    }

    [Fact]
    public async Task A_malformed_spend_is_not_sent_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "temMALFORMED";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // A tem is refused the same way every time: sending it again only repeats the refusal.
        Assert.Equal(new TicketRelease(0, 0, 1), release);
        Assert.Contains(ours[0], sink.LiveTickets);
        Assert.Single(sink.Submitted);
    }

    [Fact]
    public async Task A_round_the_node_could_not_answer_is_skipped_not_fatal()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);

        // The census is the first listing; the one after the spends is the one that fails.
        ledger.LiveTicketsFailFromCall = 2;
        ledger.LiveTicketsFailures = 1;
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(2, 0, 0) { Charged = 2 }, release);
        Assert.Equal(0, ledger.LiveTicketsFailures);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_spend_the_node_could_not_take_counts_as_an_attempt()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);

        // A throw takes no position, so position 0 throws on every attempt.
        sink.Throws[0] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: submit did not answer");
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 0, 1) { InDoubt = 1 }, release);
        Assert.Contains(ours[0], sink.LiveTickets);
        Assert.Equal(5, sink.Signatures);
    }

    [Fact(Timeout = 10_000)]
    public async Task The_tidy_up_stops_at_its_deadline_and_counts_what_did_not_settle()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { QueueEverything = true };
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(200));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(new TicketRelease(0, 0, 2) { InDoubt = 2 }, release);
    }

    [Fact]
    public async Task A_sequence_strategy_tidies_up_too_when_it_can_read_the_ledger()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) }, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(2, 0, 0) { Charged = 2 }, release);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_sequence_strategy_without_a_ledger_source_releases_nothing()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TicketRelease.None, release);
        Assert.Empty(sink.Submitted);
    }

    [Theory]
    [InlineData("error:tooBusy")]
    [InlineData("error:noCurrent")]
    [InlineData("error:noNetwork")]
    [InlineData("error:notSynced")]
    [InlineData("error:noClosed")]
    [InlineData("tefMAX_LEDGER")]
    public async Task A_spend_refused_for_a_passing_reason_is_sent_again_and_released(string refusal)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = refusal;
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, release.Released);
        Assert.DoesNotContain(ours[0], sink.LiveTickets);
        Assert.Equal(2, sink.Submitted.Count);
    }

    [Fact]
    public async Task Another_error_answer_is_not_sent_again()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "error:invalidTransaction";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, release.NotReleased);
        Assert.Single(sink.Submitted);
    }

    [Fact]
    public async Task Every_spend_carries_a_last_ledger_ten_past_the_validated_index()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 3, marked: true);
        long validated = ledger.CurrentLedger;
        await using TicketStrategy strategy = Strategy(sink, ledger);

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.All(sink.Submitted, tx => Assert.Equal((uint)validated + 10, Convert.ToUInt32(tx["LastLedgerSequence"])));
    }

    [Fact(Timeout = 10_000)]
    public async Task A_queued_spend_that_never_lands_is_sent_again_once_its_last_ledger_is_reached()
    {
        FakeLedgerSource ledger = new() { LedgerAdvancePerRead = 1 };
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);

        // Taken, and then dropped from the queue: it never lands.
        sink.Refusals[0] = "terQUEUED";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, release.Released);
        Assert.DoesNotContain(ours[0], sink.LiveTickets);

        uint first = Convert.ToUInt32(sink.Submitted[0]["LastLedgerSequence"]);
        uint second = Convert.ToUInt32(sink.Submitted[1]["LastLedgerSequence"]);

        // Not judged expired while the index was one short; judged expired in the round whose index
        // reached it, and resent in the round after, with a fresh deadline (Review Focus 5).
        Assert.Equal(first + 11, second);
        Assert.Equal(2, sink.Submitted.Count);
    }

    /// <summary>
    /// Answers every spend of the tidy-up's first four rounds <c>error:tooBusy</c>, so its fifth round
    /// — each ticket's last submission — sends one spend at a time (spec 4.6, I15).
    /// </summary>
    private static void BusyForFourRounds(FakeLedgerSink sink, int tickets)
    {
        for (int position = 0; position < 4 * tickets; position++) sink.Refusals[position] = "error:tooBusy";
    }

    private static uint LastLedger(Dictionary<string, object> tx) => Convert.ToUInt32(tx["LastLedgerSequence"]);

    [Fact(Timeout = 10_000)]
    public async Task A_throttled_round_longer_than_a_deadline_sends_no_spend_past_it()
    {
        const int Tickets = 20;
        FakeLedgerSource ledger = new();
        ManualTimeProvider clock = new();
        SlowNodeSink sink = new(new FakeLedgerSink(ledger), ledger, clock);
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, Tickets, marked: true);
        BusyForFourRounds(sink.Inner, Tickets);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1), Time = clock });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // Twenty spends one at a time, a ledger apiece, outlast a deadline ten ledgers past the
        // round's first index; on each ticket's last submission, one sent past it is lost for good.
        Assert.Equal(new TicketRelease(Tickets, 0, 0) { Charged = Tickets }, release);
        Assert.Equal(5 * Tickets, sink.Inner.Submitted.Count);
        Assert.Empty(sink.Inner.LiveTickets);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_spend_that_took_a_re_read_deadline_expires_only_once_the_index_reaches_it()
    {
        const int Tickets = 4;
        FakeLedgerSource ledger = new() { LedgerAdvancePerRead = 1 };
        ManualTimeProvider clock = new();
        SlowNodeSink sink = new(new FakeLedgerSink(ledger), ledger, clock);
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, Tickets, marked: true);
        BusyForFourRounds(sink.Inner, Tickets);

        // The fifth round's last spend goes out twelve seconds in, after a re-read; it is taken and
        // never lands.
        sink.Inner.Refusals[5 * Tickets - 1] = "terQUEUED";
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1), Time = clock });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        uint roundStart = LastLedger(sink.Inner.Submitted[4 * Tickets]);
        uint reRead = LastLedger(sink.Inner.Submitted[^1]);

        Assert.True(reRead > roundStart, $"the queued spend carries {reRead}, the round's first {roundStart}");
        Assert.Equal(new TicketRelease(Tickets - 1, 0, 1) { Charged = Tickets - 1 }, release);

        // Each later round reads an index one higher; the tidy-up stopped in the round whose index
        // reached the spend's own deadline, not the round's first one.
        Assert.Equal(reRead, ledger.CurrentLedger);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_re_read_the_node_does_not_answer_leaves_the_rest_of_the_round_for_the_next()
    {
        const int Tickets = 5;
        FakeLedgerSource ledger = new();
        ManualTimeProvider clock = new();

        // The re-read due before the fifth round's fourth spend goes unanswered.
        SlowNodeSink sink = new(new FakeLedgerSink(ledger), ledger, clock) { DropLedgerReadAt = 4 * Tickets };
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, Tickets, marked: true);
        BusyForFourRounds(sink.Inner, Tickets);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1), Time = clock });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        uint[] lastSubmissions = [.. sink.Inner.Submitted.Skip(4 * Tickets).Select(LastLedger)];

        Assert.Equal(new TicketRelease(Tickets, 0, 0) { Charged = Tickets }, release);
        Assert.Equal(5 * Tickets, sink.Inner.Submitted.Count);
        Assert.Equal([lastSubmissions[0], lastSubmissions[0], lastSubmissions[0]], lastSubmissions[..3]);
        Assert.All(lastSubmissions[3..], lastLedger => Assert.True(lastLedger > lastSubmissions[0]));
    }

    [Fact]
    public async Task A_census_the_node_did_not_answer_once_is_tried_again()
    {
        FakeLedgerSource ledger = new() { LiveTicketsFailFromCall = 1, LiveTicketsFailures = 1 };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, release.Released);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_stalled_ledger_never_expires_a_queued_spend()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "terQUEUED";
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(1, release.NotReleased);
        Assert.Single(sink.Submitted);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_round_whose_index_cannot_be_read_sends_nothing_and_does_not_throw()
    {
        FakeLedgerSource ledger = new()
        {
            CurrentLedgerFailure = new XrplFileException(XrplFileError.NodeUnreachable, "fake: ledger did not answer"),
            CurrentLedgerFailsFromCall = 1,
        };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using TicketStrategy strategy = Strategy(sink, ledger);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(2, release.NotReleased);
        Assert.Empty(sink.Submitted);
    }

    [Fact(Timeout = 10_000)]
    public async Task An_in_doubt_spend_that_expires_is_no_longer_in_doubt()
    {
        FakeLedgerSource ledger = new() { LedgerAdvancePerRead = 1 };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);

        // Taken, never lands; every later submission refused for good.
        sink.Refusals[0] = "terQUEUED";
        sink.Refusals[1] = "temMALFORMED";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 0, 1), release);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_refusal_after_an_unanswered_transmission_keeps_the_spend_in_doubt()
    {
        FakeLedgerSource ledger = new();
        UnansweredFirstSink sink = new(new FakeLedgerSink(ledger));
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Inner.Refusals[0] = "temMALFORMED";
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // The ledger index never moves here, so the waiting spend neither lands nor expires.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(deadline.Token);

        Assert.Equal(new TicketRelease(0, 0, 1) { InDoubt = 1 }, release);
        Assert.Single(sink.Inner.Submitted);
    }

    [Fact]
    public async Task A_refusal_after_an_unanswered_transmission_whose_earlier_copy_landed_is_released()
    {
        FakeLedgerSource ledger = new();
        EarlierCopyLandedSink sink = new(new FakeLedgerSink(ledger));
        sink.Inner.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The spend waited under its hash; when the ticket left the listing, that hash was looked up
        // and found validated with tesSUCCESS (spec 4.6).
        Assert.Equal(new TicketRelease(1, 0, 0) { Charged = 1 }, release);
    }

    [Fact]
    public async Task An_unanswered_spend_whose_ticket_somebody_else_consumed_is_gone_and_in_doubt()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Throws[0] = new XrplFileException(XrplFileError.NodeUnreachable, "fake: submit did not answer");
        ConsumingSink consuming = new(sink, ledger, ours[0]);
        await using TicketStrategy strategy = new(consuming, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 1, 0) { InDoubt = 1 }, release);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_taken_spend_whose_ticket_somebody_else_consumed_is_gone_and_not_in_doubt()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger);
        IReadOnlyList<uint> ours = fake.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        TakenButConsumedSink sink = new(fake, ledger, ours[0], recordLookup: true);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The spend's own lookup found it not validated: the ticket went to something else, so the
        // spend can never apply, and nothing was charged (spec 4.6).
        Assert.Equal(new TicketRelease(0, 1, 0), release);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_taken_spend_the_node_cannot_show_stays_in_doubt_when_its_ticket_is_gone()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger);
        IReadOnlyList<uint> ours = fake.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        TakenButConsumedSink sink = new(fake, ledger, ours[0], recordLookup: false);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // A lookup that finds nothing has not shown the spend unable to apply (spec 4.6).
        Assert.Equal(new TicketRelease(0, 1, 0) { InDoubt = 1 }, release);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_spend_that_lands_closes_every_earlier_unanswered_submission_of_its_ticket()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger);
        fake.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        FirstCallUnreachableSink sink = new(fake);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The first submission never answered and stayed open; the second validated with tesSUCCESS,
        // claimed the fee and settled the ticket, so nothing is in doubt.
        Assert.Equal(new TicketRelease(1, 0, 0) { Charged = 1 }, release);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_ticket_gets_five_submissions_however_often_they_expire()
    {
        FakeLedgerSource ledger = new() { LedgerAdvancePerRead = 1 };
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);

        // Every spend is taken and never lands.
        for (int position = 0; position < 5; position++) sink.Refusals[position] = "terQUEUED";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // A sixth spend would have landed; the fifth one's expiry closed the last open submission.
        Assert.Equal(5, sink.Submitted.Count);
        Assert.Equal(new TicketRelease(0, 0, 1), release);
    }

    [Fact(Timeout = 10_000)]
    public async Task Answers_a_cancelled_round_already_heard_stay_counted()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger);
        fake.LeaveTickets(Wallet.ClassicAddress, 30, marked: true);
        for (int position = 0; position < 25; position++) fake.Refusals[position] = "telCAN_NOT_QUEUE";
        HoldingSink sink = new(fake, answered: 25, held: 5);
        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TicketRelease> running = strategy.ReleaseLeftoverTicketsAsync(deadline.Token);
        await sink.Heard.Task.WaitAsync(TestContext.Current.CancellationToken);
        await sink.Holding.Task.WaitAsync(TestContext.Current.CancellationToken);
        await deadline.CancelAsync();

        TicketRelease release = await running;

        // Twenty-five refusals were heard and closed their submissions; five were cut off in flight
        // and stay open. A tidy-up that recorded answers only at the end of the round would have
        // lost the twenty-five and reported thirty in doubt.
        Assert.Equal(5, release.InDoubt);
        Assert.Equal(0, release.Charged);
        Assert.Equal(30, release.NotReleased);
    }

    private static TicketStrategy PacedStrategy(FakeLedgerSink sink, FakeLedgerSource ledger, TimeSpan poll) =>
        new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = poll });

    /// <summary>The most submissions in flight at once among positions [from, to).</summary>
    private static int PeakBetween(FakeLedgerSink sink, int from, int to) => sink.InFlightAtSubmit.Skip(from).Take(to - from).Max();

    [Fact(Timeout = 20_000)]
    public async Task A_load_warning_on_a_submit_halves_the_next_rounds_spends_in_flight_and_doubles_its_pause()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        sink.WarnsAt.Add(0);
        TimeSpan poll = TimeSpan.FromMilliseconds(100);
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, poll);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(60, release.Released);
        Assert.True(PeakBetween(sink, 60, 120) <= 12, $"round two had {PeakBetween(sink, 60, 120)} in flight");

        // The pause before round two is two polls. A timer may fire up to a tick early on Windows, so
        // the check is one and a half polls: still more than an undoubled pause could give.
        TimeSpan gap = sink.SubmittedAt[60] - sink.SubmittedAt[59];
        Assert.True(gap >= 1.5 * poll, $"round two started {gap} after round one");
    }

    [Fact(Timeout = 20_000)]
    public async Task A_load_warning_on_the_listing_backs_off_too()
    {
        // Listings 1 and 2 are the census and round one's settle listing, both inside round one: each
        // carries a warning, and either alone strains round one.
        FakeLedgerSource ledger = new() { ListingWarnings = 2 };
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, TimeSpan.FromMilliseconds(1));

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.True(PeakBetween(sink, 60, 120) <= 12);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("error:tooBusy")]
    [InlineData("error:notSynced")]
    [InlineData("listing")]
    [InlineData("index")]
    [InlineData("submission")]
    public async Task A_busy_answer_or_an_unreachable_node_backs_off_too(string strain)
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink fake = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        fake.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) fake.Refusals[position] = "telCAN_NOT_QUEUE";
        ILedgerSink sink = fake;

        // The fake's position at which the round after the strained one begins.
        int afterStrain = 60;

        switch (strain)
        {
            case "listing":
                // The census is listing 1; round one's is listing 2.
                ledger.LiveTicketsFailFromCall = 2;
                ledger.LiveTicketsFailures = 1;
                break;
            case "index":
                // Round one cannot read the index and sends nothing; round two sends all sixty.
                ledger.CurrentLedgerFailures = 1;
                afterStrain = 0;
                break;
            case "submission":
                // The first submission never reaches the fake, so round one takes positions 0..58.
                sink = new FirstCallUnreachableSink(fake);
                afterStrain = 59;
                break;
            default:
                fake.Refusals[0] = strain;
                break;
        }

        await using TicketStrategy strategy = new(sink, ledger, Signer, new TicketStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) });

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.True(PeakBetween(fake, afterStrain, afterStrain + 60) <= 12);
    }

    /// <summary>Fails its first submission as an unreachable node, before the fake sees it; forwards every other.</summary>
    private sealed class FirstCallUnreachableSink(ILedgerSink inner) : ILedgerSink
    {
        private int _calls;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? Task.FromException<SubmitOutcome>(new XrplFileException(XrplFileError.NodeUnreachable, "fake: the first submit did not answer"))
                : inner.SubmitAsync(transaction, signer, cancellationToken);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_round_without_strain_changes_nothing()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 60, marked: true);
        for (int position = 0; position < 60; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, TimeSpan.FromMilliseconds(1));

        await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.True(PeakBetween(sink, 60, 120) > 12, "a round without strain slowed down");
    }

    [Fact(Timeout = 30_000)]
    public async Task Warnings_on_every_answer_slow_the_tidy_up_to_its_floor_and_it_still_finishes()
    {
        // Every round's listing warns, so every round is strained.
        FakeLedgerSource ledger = new() { ListingWarnings = int.MaxValue };
        FakeLedgerSink sink = new(ledger) { SubmitDelay = TimeSpan.FromMilliseconds(5) };
        sink.LeaveTickets(Wallet.ClassicAddress, 30, marked: true);
        // Four rounds of thirty refusals: round five (positions 120..149) is every ticket's fifth and
        // last submission and runs after four strained rounds, at 25, 12, 6, 3 and 1 in flight.
        for (int position = 0; position < 120; position++) sink.Refusals[position] = "telCAN_NOT_QUEUE";
        await using TicketStrategy strategy = PacedStrategy(sink, ledger, TimeSpan.FromMilliseconds(2));

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(30, release.Released);
        Assert.True(PeakBetween(sink, 90, 120) <= 3, $"round four had {PeakBetween(sink, 90, 120)} in flight");
        Assert.Equal(1, PeakBetween(sink, 120, 150));
    }

    /// <summary>
    /// Forwards the first <paramref name="answered"/> submissions and signals once all have answered;
    /// holds the next <paramref name="held"/> until the call is cancelled, and signals once all are held.
    /// </summary>
    private sealed class HoldingSink(FakeLedgerSink inner, int answered, int held) : ILedgerSink
    {
        private int _calls;
        private int _answers;
        private int _holding;

        public TaskCompletionSource Heard { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > answered)
            {
                if (Interlocked.Increment(ref _holding) == held) Holding.TrySetResult();

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            SubmitOutcome outcome = await inner.SubmitAsync(transaction, signer, cancellationToken);

            if (Interlocked.Increment(ref _answers) == answered) Heard.TrySetResult();

            return outcome;
        }
    }

    /// <summary>
    /// Sends through to the fake, where the spend lands, and then reports what a node that had lost
    /// the first transmission's answer would: a refusal of the second copy, flagged.
    /// </summary>
    private sealed class EarlierCopyLandedSink(FakeLedgerSink inner) : ILedgerSink
    {
        public FakeLedgerSink Inner => inner;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            SubmitOutcome landed = await inner.SubmitAsync(transaction, signer, cancellationToken);
            return new SubmitOutcome("telCAN_NOT_QUEUE", landed.TxHash) { FollowsUnansweredAttempt = true };
        }
    }

    /// <summary>
    /// A loaded node answering the tidy-up's spends slowly: each submission takes four seconds of
    /// <paramref name="clock"/> and one validated ledger before it is answered, so a round of them
    /// sent one at a time outlasts a deadline ten ledgers past the round's first index.
    /// </summary>
    private sealed class SlowNodeSink(FakeLedgerSink inner, FakeLedgerSource ledger, ManualTimeProvider clock) : ILedgerSink
    {
        private int _submissions;

        public FakeLedgerSink Inner => inner;

        /// <summary>The submission, by position, after which the next index read goes unanswered.</summary>
        public int? DropLedgerReadAt { get; init; }

        public long LoadWarnings => inner.LoadWarnings;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            int position = Interlocked.Increment(ref _submissions) - 1;

            if (position == DropLedgerReadAt) ledger.CurrentLedgerFailures = 1;

            clock.Advance(TimeSpan.FromSeconds(4));
            ledger.AdvanceLedger(1);

            return inner.SubmitAsync(transaction, signer, cancellationToken);
        }
    }

    /// <summary>The first transmission of every submission goes unanswered; the sink then reports the node's answer to the second, flagged.</summary>
    private sealed class UnansweredFirstSink(FakeLedgerSink inner) : ILedgerSink
    {
        public FakeLedgerSink Inner => inner;

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            SubmitOutcome outcome = await inner.SubmitAsync(transaction, signer, cancellationToken);
            return outcome with { FollowsUnansweredAttempt = !Reached(outcome.EngineResult) };
        }

        private static bool Reached(string result) =>
            result.StartsWith("tes", StringComparison.Ordinal) || result.StartsWith("tec", StringComparison.Ordinal) || result == "terQUEUED";
    }

    /// <summary>
    /// Takes the tidy-up's spend as <c>terQUEUED</c> without applying it, while another transaction
    /// consumes the ticket: it leaves the listing, and the spend's hash is recorded as not validated
    /// — or, when <paramref name="recordLookup"/> is false, not recorded at all, as a node that cannot
    /// show the transaction answers.
    /// </summary>
    private sealed class TakenButConsumedSink(FakeLedgerSink inner, FakeLedgerSource ledger, uint ticket, bool recordLookup) : ILedgerSink
    {
        private static readonly string SpendHash = new('5', 64);

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            inner.LiveTickets.Remove(ticket);
            ledger.SetLiveTickets((string)transaction["Account"], [.. inner.LiveTickets.Order().Select(live => new LiveTicket(live, FakeLedgerSink.UnknownCreator))]);

            if (recordLookup)
            {
                ledger.Add(new LedgerTransaction(SpendHash, 100, (string)transaction["Account"], Succeeded: false, Validated: false, Result: null, null, null, null));
            }

            return Task.FromResult(new SubmitOutcome("terQUEUED", SpendHash));
        }
    }

    /// <summary>Lets another transaction consume one ticket the moment the tidy-up's own submission for it fails to answer.</summary>
    private sealed class ConsumingSink(FakeLedgerSink inner, FakeLedgerSource ledger, uint ticket) : ILedgerSink
    {
        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public async Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.SubmitAsync(transaction, signer, cancellationToken);
            }
            catch (XrplFileException)
            {
                inner.LiveTickets.Remove(ticket);
                ledger.SetLiveTickets((string)transaction["Account"], [.. inner.LiveTickets.Order().Select(live => new LiveTicket(live, FakeLedgerSink.UnknownCreator))]);
                throw;
            }
        }
    }

    /// <summary>
    /// Refuses the tidy-up's spend as <paramref name="refusal"/> while another transaction consumes
    /// the ticket, so the next listing no longer shows it. Counts the spends it was asked to send.
    /// </summary>
    private sealed class ConsumedWhileRefusedSink(FakeLedgerSink inner, FakeLedgerSource ledger, uint ticket, string refusal) : ILedgerSink
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public Task<uint> SequenceAsync(string account, bool validated, CancellationToken cancellationToken) =>
            inner.SequenceAsync(account, validated, cancellationToken);

        public Task<SubmitOutcome> SubmitAsync(Dictionary<string, object> transaction, ITransactionSigner signer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);

            inner.LiveTickets.Remove(ticket);
            ledger.SetLiveTickets((string)transaction["Account"], [.. inner.LiveTickets.Order().Select(live => new LiveTicket(live, FakeLedgerSink.UnknownCreator))]);

            return Task.FromResult(new SubmitOutcome(refusal, new string('7', 64)));
        }
    }
}
