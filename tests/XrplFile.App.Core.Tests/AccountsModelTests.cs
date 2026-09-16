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
        string reference = await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);

        // ThrowForUnfundedAccounts makes this test a real regression guard, not just an assertion:
        // if RefreshFromLedgerAsync ever went back to asking this account's sequence before
        // noticing its balance is zero, this fake would answer exactly the RippledException a real
        // node's actNotFound produces (XrplClientLedgerSink.SequenceAsync has nothing catching it),
        // and this test would fail with that exception rather than with a wrong Stage.
        _connections.Sink.ThrowForUnfundedAccounts = true;

        // KeyOnly is a claim only a node can make — an unfunded address is confirmed, not assumed.
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.RefreshFromLedgerAsync(reference, TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(AccountStage.KeyOnly, row.Stage);
        Assert.Equal(0, row.BalanceDrops);
        Assert.Null(row.DeletableFromLedger);
        Assert.Null(row.LedgersRemaining);
        Assert.Null(model.Message);
    }

    [Fact]
    public async Task A_freshly_listed_row_is_unknown_not_key_only()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(AccountStage.Unknown, row.Stage);
        Assert.Null(row.BalanceDrops);
        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task A_cached_deletable_ledger_from_a_previous_session_is_shown_as_unknown_not_key_only()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        await vault.UpdateAsync(reference, new VaultEntryEdit(DeletableFromLedger: 12_345), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(AccountStage.Unknown, row.Stage);
        Assert.Null(row.BalanceDrops);
        Assert.Equal(12_345, row.DeletableFromLedger);
        Assert.Equal(0, _connections.Connections);
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
        Assert.Null(row.BalanceDrops);
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
    public async Task A_row_refreshed_from_the_ledger_keeps_what_it_learned_across_a_later_refresh()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        NewVaultEntry entry = Entry("generated", VaultEntryKind.Generated);
        string reference = await vault.StoreAsync(entry, TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[entry.Address] = 30_000_000;

        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.RefreshFromLedgerAsync(reference, TestContext.Current.CancellationToken);

        AccountRow refreshed = Assert.Single(model.Rows);

        // Only the vault-owned Label changes between the two listings; what the ledger taught the
        // row must survive untouched.
        await vault.UpdateAsync(reference, new VaultEntryEdit(Label: "renamed"), TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        AccountRow row = Assert.Single(model.Rows);
        Assert.Equal(refreshed.Stage, row.Stage);
        Assert.Equal(refreshed.BalanceDrops, row.BalanceDrops);
        Assert.Equal(refreshed.DeletableFromLedger, row.DeletableFromLedger);
        Assert.Equal(refreshed.LedgersRemaining, row.LedgersRemaining);
        Assert.Equal("renamed", row.Label);
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

    [Fact]
    public async Task A_locked_vault_says_why_instead_of_throwing_out_of_RefreshAsync()
    {
        (AccountsModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("generated", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await vault.LockAsync();

        // Every other model in this application catches VaultException around its own vault reads;
        // RefreshAsync used to be the one exception, reachable from the tab's own initialisation.
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VaultErrorMessages.For(VaultError.Locked), model.Message);
    }

    [Fact]
    public void HasActions_is_true_for_every_stage_except_closed()
    {
        static AccountRow Row(AccountStage stage) =>
            new("ref", "label", "rAddress", "1", stage, null, null, null, null);

        Assert.True(Row(AccountStage.Unknown).HasActions);
        Assert.True(Row(AccountStage.KeyOnly).HasActions);
        Assert.True(Row(AccountStage.Funded).HasActions);
        Assert.True(Row(AccountStage.Closable).HasActions);
        Assert.False(Row(AccountStage.Closed).HasActions);
    }

    [Fact]
    public void TimeRemainingDescription_follows_LedgersRemaining()
    {
        AccountRow noCountdown = new("ref", "label", "rAddress", "1", AccountStage.Closable, 0, 264, 0, null);
        AccountRow counting = new("ref", "label", "rAddress", "1", AccountStage.Funded, 5_000_000, 264, 64, null);

        Assert.Equal(LedgerTiming.Describe(LedgerTiming.Estimate(0)), noCountdown.TimeRemainingDescription);
        Assert.Equal(LedgerTiming.Describe(LedgerTiming.Estimate(64)), counting.TimeRemainingDescription);
        Assert.NotNull(counting.TimeRemainingDescription);
    }
}
