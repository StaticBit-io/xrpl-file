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

        Assert.Equal(new TicketRelease(3, 0, 0), release);
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

    [Fact]
    public async Task A_ticket_the_node_calls_gone_but_the_ledger_still_lists_is_not_released()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        sink.AnswersGone.Add(ours[0]);
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        // The node's tefNO_TICKET alone frees nothing: the validated ledger still lists the ticket,
        // so its reserve is still locked (spec 4.6, I5).
        Assert.Equal(new TicketRelease(1, 0, 1), release);
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
        Assert.Equal(new TicketRelease(0, 1, 0), release);
        Assert.Empty(sink.LiveTickets);
    }

    [Fact]
    public async Task A_spend_the_node_refuses_leaves_the_ticket_not_released()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        IReadOnlyList<uint> ours = sink.LeaveTickets(Wallet.ClassicAddress, 1, marked: true);
        sink.Refusals[0] = "telINSUF_FEE_P";
        await using TicketStrategy strategy = Strategy(sink, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(0, 0, 1), release);
        Assert.Contains(ours[0], sink.LiveTickets);
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

        Assert.Equal(new TicketRelease(0, 0, 2), release);
    }

    [Fact]
    public async Task A_sequence_strategy_tidies_up_too_when_it_can_read_the_ledger()
    {
        FakeLedgerSource ledger = new();
        FakeLedgerSink sink = new(ledger);
        sink.LeaveTickets(Wallet.ClassicAddress, 2, marked: true);
        await using SequenceStrategy strategy = new(sink, Signer, new SequenceStrategyOptions { ValidationPoll = TimeSpan.FromMilliseconds(1) }, ledger);

        TicketRelease release = await strategy.ReleaseLeftoverTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TicketRelease(2, 0, 0), release);
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
}
