using Xunit;

namespace XrplFile.Tests;

public sealed class DestinationStateTests
{
    [Fact]
    public async Task An_account_the_fake_has_never_heard_of_is_absent()
    {
        FakeLedgerSource ledger = new();

        DestinationState state = await ledger.DestinationAsync("rStranger", TestContext.Current.CancellationToken);

        Assert.False(state.Exists);
        Assert.Equal(DestinationState.Absent, state);
    }

    [Fact]
    public async Task A_destination_the_fake_knows_answers_with_its_flags()
    {
        FakeLedgerSource ledger = new();
        ledger.Destinations["rPicky"] = new DestinationState(Exists: true, RequiresDestinationTag: true, AuthorisesDepositors: false);

        DestinationState state = await ledger.DestinationAsync("rPicky", TestContext.Current.CancellationToken);

        Assert.True(state.Exists);
        Assert.True(state.RequiresDestinationTag);
        Assert.False(state.AuthorisesDepositors);
    }

    [Fact]
    public void An_absent_destination_carries_no_flags()
    {
        Assert.False(DestinationState.Absent.Exists);
        Assert.False(DestinationState.Absent.RequiresDestinationTag);
        Assert.False(DestinationState.Absent.AuthorisesDepositors);
    }

    [Fact]
    public async Task A_balance_makes_a_destination_exist_without_a_test_stamping_it_separately()
    {
        // An account cannot hold XRP without existing: keeping Balances and Destinations as
        // independent dictionaries let a test describe a ledger no real node could ever answer
        // with, and every close test used to keep the two in step by hand. This is the fake's job
        // now.
        FakeLedgerSource ledger = new();
        ledger.Balances["rFunded"] = 5_000_000;

        DestinationState state = await ledger.DestinationAsync("rFunded", TestContext.Current.CancellationToken);

        Assert.True(state.Exists);
        Assert.False(state.RequiresDestinationTag);
        Assert.False(state.AuthorisesDepositors);
    }

    [Fact]
    public async Task A_balance_of_zero_is_still_absent()
    {
        // Zero drops is this fake's own convention for "does not exist" everywhere else
        // (AccountState.Absent); an explicit zero-balance entry must read the same way here.
        FakeLedgerSource ledger = new();
        ledger.Balances["rEmpty"] = 0;

        DestinationState state = await ledger.DestinationAsync("rEmpty", TestContext.Current.CancellationToken);

        Assert.Equal(DestinationState.Absent, state);
    }

    [Fact]
    public async Task An_explicit_entry_wins_over_a_balance()
    {
        // The escape hatch a test needs to give a destination its flags, or to describe one
        // explicitly absent regardless of what it holds.
        FakeLedgerSource ledger = new();
        ledger.Balances["rPicky"] = 5_000_000;
        ledger.Destinations["rPicky"] = new DestinationState(Exists: true, RequiresDestinationTag: true, AuthorisesDepositors: false);

        DestinationState state = await ledger.DestinationAsync("rPicky", TestContext.Current.CancellationToken);

        Assert.True(state.RequiresDestinationTag);
    }
}
