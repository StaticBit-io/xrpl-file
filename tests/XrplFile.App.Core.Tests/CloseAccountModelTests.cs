using Xunit;

using Xrpl.Wallet;

using XrplFile.Keys;
using XrplFile.Tests;

namespace XrplFile.App.Core.Tests;

public sealed class CloseAccountModelTests : VaultHarness
{
    private readonly FakeConnectionFactory _connections = new();

    // A real, codec-valid classic address rather than a bare placeholder string: DeleteAsync signs
    // through VaultSigner, which drives the real binary codec, and it rejects a Destination that
    // does not decode as an AccountId. Mirrors ThrowawayAccountDeletionTests' own Home constant.
    private static readonly string Home = XrplWallet.Generate().ClassicAddress;

    private async Task<(CloseAccountModel Model, KeyVault Vault, string Reference, XrplWallet Wallet)> OpenAsync(
        CancellationToken token, long balanceDrops = 30_000_000, bool destinationExists = true)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);

        XrplWallet wallet = XrplWallet.Generate();
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "throwaway", "1", wallet.ClassicAddress, wallet.Seed), token);

        _connections.Ledger.Balances[wallet.ClassicAddress] = balanceDrops;

        // ThrowawayAccount.PreviewDeletionAsync refuses NotYet until the current ledger clears the
        // account's sequence (the fake sink's default, 10) plus DeletionSequenceDelta (254) — 264.
        // FakeLedgerSource starts at ledger 1, which no test here is about, so every test that opens
        // an account through this helper is given a ledger comfortably past that threshold.
        _connections.Ledger.CurrentLedger = 5_000;

        if (destinationExists)
        {
            _connections.Ledger.Destinations[Home] =
                new DestinationState(Exists: true, RequiresDestinationTag: false, AuthorisesDepositors: false);
        }

        CloseAccountModel model = new(vault, _connections, TimeProvider.System);
        return (model, vault, reference, wallet);
    }

    [Fact]
    public async Task With_no_destination_chosen_it_will_not_close()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.Null(model.Destination);
        Assert.False(model.CanClose);
    }

    [Fact]
    public async Task The_other_keys_in_the_vault_are_offered_as_destinations_and_this_account_is_not()
    {
        (CloseAccountModel model, KeyVault vault, string reference, XrplWallet wallet) =
            await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("somewhere else"), TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(model.Candidates, info => string.Equals(info.Address, wallet.ClassicAddress, StringComparison.Ordinal));
        Assert.Contains(model.Candidates, info => string.Equals(info.Label, "somewhere else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preflight_that_can_proceed_shows_the_destination_balance_and_what_will_move()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[Home] = 5_000_000;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Preview);
        Assert.Equal(DeletionOutcome.Ready, model.Preview.Outcome);
        Assert.Equal(5_000_000, model.DestinationBalanceDrops);
        Assert.True(model.Preview.ArrivingDrops > 0);
        Assert.True(model.CanClose);
    }

    [Fact]
    public async Task A_destination_the_ledger_does_not_carry_refuses_and_will_not_close()
    {
        (CloseAccountModel model, _, string reference, _) =
            await OpenAsync(TestContext.Current.CancellationToken, destinationExists: false);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationMissing, model.Preview!.Outcome);
        Assert.False(model.CanClose);
        Assert.Equal(CloseErrorMessages.For(DeletionOutcome.DestinationMissing), model.Message);
    }

    [Fact]
    public async Task Closing_records_the_closure_on_the_entry()
    {
        (CloseAccountModel model, KeyVault vault, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(
            await vault.ListAsync(TestContext.Current.CancellationToken),
            entry => string.Equals(entry.Reference, reference, StringComparison.Ordinal));

        Assert.NotNull(info.Closure);
        Assert.Equal(Home, info.Closure.Destination);
        Assert.Equal(model.Preview!.ArrivingDrops, info.Closure.ArrivedDrops);
    }

    [Fact]
    public async Task A_queued_deletion_records_nothing_yet()
    {
        (CloseAccountModel model, KeyVault vault, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Sink.QueueEverything = true;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(
            await vault.ListAsync(TestContext.Current.CancellationToken),
            entry => string.Equals(entry.Reference, reference, StringComparison.Ordinal));

        Assert.Null(info.Closure);
        Assert.Equal(CloseErrorMessages.For(DeletionOutcome.Queued), model.Message);
    }

    [Fact]
    public async Task Closing_without_a_preflight_does_nothing()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_connections.Sink.Submitted);
    }
}
