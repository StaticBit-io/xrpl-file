using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class FundingModelTests : VaultHarness
{
    private readonly FakeConnectionFactory _connections = new();
    private readonly FakeClipboard _clipboard = new();

    private async Task<(FundingModel Model, string Reference, NewVaultEntry Entry, KeyVault Vault)> OpenAsync(CancellationToken token)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        NewVaultEntry entry = Entry("throwaway", VaultEntryKind.Generated);
        string reference = await vault.StoreAsync(entry, token);

        FundingModel model = new(vault, _connections, _clipboard);
        return (model, reference, entry, vault);
    }

    [Fact]
    public async Task Selecting_a_key_shows_its_address_and_a_QR_of_exactly_that_address()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, model.Address);
        Assert.Equal(entry.Address, model.QrPayload);
    }

    [Fact]
    public async Task An_unfunded_address_reads_as_no_account_yet()
    {
        (FundingModel model, string reference, _, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(model.AccountExists);
        Assert.Equal(0, model.BalanceDrops);
    }

    [Fact]
    public async Task A_funded_address_reads_back_its_balance()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[entry.Address] = 30_000_000;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(model.AccountExists);
        Assert.Equal(30_000_000, model.BalanceDrops);
    }

    [Fact]
    public async Task Copying_the_address_does_not_put_it_on_a_clearing_timer()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.CopyAddressAsync(TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, _clipboard.Copied);
        Assert.Null(_clipboard.Expiry);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_says_so_and_keeps_the_address()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, model.Address);
        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), model.Message);
    }

    [Fact]
    public async Task A_locked_vault_leaves_the_previous_selection_and_says_why()
    {
        (FundingModel model, string reference, NewVaultEntry entry, KeyVault vault) =
            await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await vault.LockAsync();

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.Equal(entry.Address, model.Address);
        Assert.Equal(VaultErrorMessages.For(VaultError.Locked), model.Message);

        // Address is deliberately left alone (asserted above), but nothing here vouches for it any
        // more -- a caller comparing CurrentReference to the reference it asked for is the only way
        // to tell that apart from an actually-resolved selection.
        Assert.Null(model.CurrentReference);
    }

    [Fact]
    public async Task A_refresh_after_a_failed_select_touches_nothing_and_keeps_the_explanation()
    {
        (FundingModel model, string reference, NewVaultEntry entry, KeyVault vault) =
            await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await vault.LockAsync();
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        Assert.Null(model.CurrentReference);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        // No node was ever asked -- RefreshAsync bailed out before opening a connection -- and the
        // lock's own message is still what explains the screen, not overwritten by RefreshAsync's
        // own Message = null housekeeping.
        Assert.Equal(0, _connections.Connections);
        Assert.Equal(entry.Address, model.Address);
        Assert.Equal(VaultErrorMessages.For(VaultError.Locked), model.Message);
        Assert.Null(model.CurrentReference);
    }
}
