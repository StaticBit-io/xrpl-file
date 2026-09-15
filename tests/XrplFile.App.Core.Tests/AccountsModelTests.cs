using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class AccountsModelTests : VaultHarness
{
    private readonly FakeConnectionFactory _connections = new();

    private async Task<(AccountsModel Model, KeyVault Vault)> OpenAsync(CancellationToken token)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        return (new AccountsModel(vault, _connections), vault);
    }

    [Fact]
    public async Task Only_generated_keys_are_listed()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("imported", VaultEntryKind.Imported), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("generated", Assert.Single(model.Rows).Label);
    }

    [Fact]
    public async Task A_key_nobody_has_funded_is_a_key_only()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountStage.KeyOnly, Assert.Single(model.Rows).Stage);
        Assert.Equal(0, model.Rows[0].BalanceDrops);
    }

    [Fact]
    public async Task A_closed_account_is_shown_as_closed_without_asking_the_node()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        AccountClosure closure = new(DateTimeOffset.UnixEpoch, "rHome", 28_800_000, "ABC");
        await vault.UpdateAsync(reference, new VaultEntryEdit(Closure: closure), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(AccountStage.Closed, row.Stage);
        Assert.Equal(closure, row.Closure);
        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task Refreshing_one_row_from_the_ledger_gives_it_a_balance_and_a_deletable_ledger()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        NewVaultEntry entry = Entry("generated", VaultEntryKind.Generated);
        string reference = await vault.StoreAsync(entry, TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[entry.Address] = 30_000_000;

        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.RefreshFromLedgerAsync(reference, TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(30_000_000, row.BalanceDrops);
        Assert.NotNull(row.DeletableFromLedger);
    }

    [Fact]
    public async Task A_node_that_does_not_answer_leaves_the_row_and_says_why()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        await model.RefreshFromLedgerAsync(reference, TestContext.Current.CancellationToken);

        Assert.Single(model.Rows);
        Assert.NotNull(model.Message);
    }
}
