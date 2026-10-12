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
}
