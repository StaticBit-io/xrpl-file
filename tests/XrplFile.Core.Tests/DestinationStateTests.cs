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
}
