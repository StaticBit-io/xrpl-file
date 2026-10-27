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
        Assert.Equal(AccountsModel.ReadFailedMessage, model.Message);
    }

    [Fact]
    public void A_failed_read_does_not_claim_that_nothing_was_spent()
    {
        Assert.DoesNotContain("spent", AccountsModel.ReadFailedMessage, StringComparison.OrdinalIgnoreCase);
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

    private static NewVaultEntry GeneratedOn(string label, string network) =>
        new(VaultEntryKind.Generated, label, network, "rAddressOf" + label, "sSecretOf" + label);

    [Fact]
    public async Task Opening_the_list_reads_every_accounts_balance_with_one_connection_per_network()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        NewVaultEntry funded = GeneratedOn("funded", "testnet");
        NewVaultEntry unfunded = GeneratedOn("unfunded", "testnet");
        NewVaultEntry other = GeneratedOn("other", "devnet");
        await vault.StoreAsync(funded, token);
        await vault.StoreAsync(unfunded, token);
        await vault.StoreAsync(other, token);
        _connections.Ledger.Balances[funded.Address] = 25_000_000;
        _connections.Ledger.Balances[other.Address] = 5_000_000;

        await model.RefreshAsync(token);
        await model.RefreshAllFromLedgerAsync(token);

        Assert.Equal(["devnet", "testnet"], _connections.ConnectedNetworks.Order(StringComparer.Ordinal));
        Assert.Equal(2, _connections.Connections);
        Assert.Equal(XrpAmount.Format(25_000_000), model.BalanceTextOf(RowOf(model, "funded")));
        Assert.Equal(XrpAmount.Format(5_000_000), model.BalanceTextOf(RowOf(model, "other")));

        // A key with no account yet leaves the Balance cell empty: its Status says so.
        Assert.Equal(AccountStage.KeyOnly, RowOf(model, "unfunded").Stage);
        Assert.Equal(string.Empty, model.BalanceTextOf(RowOf(model, "unfunded")));
    }

    [Fact]
    public async Task A_row_reads_as_checking_while_its_balance_is_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        await vault.StoreAsync(GeneratedOn("first", "testnet"), token);
        await vault.StoreAsync(GeneratedOn("second", "devnet"), token);
        await model.RefreshAsync(token);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.BeforeConnect = (_, _) => gate.Task;

        Task reading = model.RefreshAllFromLedgerAsync(token);

        Assert.All(model.Rows, row => Assert.Equal(AccountsModel.CheckingBalance, model.BalanceTextOf(row)));

        gate.SetResult();
        await reading;

        Assert.All(model.Rows, row => Assert.NotEqual(AccountsModel.CheckingBalance, model.BalanceTextOf(row)));
        Assert.All(model.Rows, row => Assert.False(model.IsChecking(row.Reference)));
    }

    [Fact]
    public async Task A_network_that_cannot_be_reached_marks_only_its_rows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        NewVaultEntry reachable = GeneratedOn("reachable", "testnet");
        NewVaultEntry cut = GeneratedOn("cut", "devnet");
        await vault.StoreAsync(reachable, token);
        await vault.StoreAsync(cut, token);
        _connections.Ledger.Balances[reachable.Address] = 25_000_000;
        _connections.Ledger.Balances[cut.Address] = 5_000_000;
        XrplFileException unreachable = new(XrplFileError.NodeUnreachable, "no answer");
        _connections.BeforeConnect = (network, _) =>
            string.Equals(network, "devnet", StringComparison.Ordinal) ? throw unreachable : Task.CompletedTask;
        await model.RefreshAsync(token);

        await model.RefreshAllFromLedgerAsync(token);

        AccountRow failed = RowOf(model, "cut");
        Assert.True(failed.CheckFailed);
        Assert.Equal(AccountsModel.UnreadBalance, model.BalanceTextOf(failed));
        Assert.Equal(AccountsModel.ReadFailedMessage, model.Message);

        AccountRow read = RowOf(model, "reachable");
        Assert.False(read.CheckFailed);
        Assert.Equal(XrpAmount.Format(25_000_000), model.BalanceTextOf(read));
    }

    [Fact]
    public async Task A_closed_account_is_not_asked_about_when_the_list_opens()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        string reference = await vault.StoreAsync(GeneratedOn("closed", "testnet"), token);
        await vault.UpdateAsync(
            reference,
            new VaultEntryEdit(Closure: new AccountClosure(DateTimeOffset.UnixEpoch, "rHome", 28_800_000, "ABC")),
            token);
        await model.RefreshAsync(token);

        await model.RefreshAllFromLedgerAsync(token);

        Assert.Equal(0, _connections.Connections);
        Assert.Equal(string.Empty, model.BalanceTextOf(Assert.Single(model.Rows)));
    }

    [Fact]
    public async Task Reading_balances_writes_the_vault_only_when_the_closable_ledger_moved()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        NewVaultEntry entry = GeneratedOn("funded", "testnet");
        await vault.StoreAsync(entry, token);
        _connections.Ledger.Balances[entry.Address] = 25_000_000;
        await model.RefreshAsync(token);
        byte[] beforeFirstRead = await File.ReadAllBytesAsync(VaultPath, token);

        await model.RefreshAllFromLedgerAsync(token);
        byte[] afterFirstRead = await File.ReadAllBytesAsync(VaultPath, token);

        await model.RefreshAllFromLedgerAsync(token);
        byte[] afterSecondRead = await File.ReadAllBytesAsync(VaultPath, token);

        Assert.NotEqual(beforeFirstRead, afterFirstRead);
        Assert.Equal(afterFirstRead, afterSecondRead);
    }

    [Fact]
    public async Task Leaving_while_balances_are_read_leaves_no_row_checking()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        await vault.StoreAsync(GeneratedOn("first", "testnet"), token);
        await vault.StoreAsync(GeneratedOn("second", "devnet"), token);
        await model.RefreshAsync(token);
        IReadOnlyList<AccountRow> before = model.Rows;
        _connections.BeforeConnect = (_, cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        using CancellationTokenSource leaving = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task reading = model.RefreshAllFromLedgerAsync(leaving.Token);
        Assert.All(model.Rows, row => Assert.True(model.IsChecking(row.Reference)));
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.All(model.Rows, row => Assert.False(model.IsChecking(row.Reference)));
        Assert.Equal(before, model.Rows);
    }

    [Fact]
    public async Task A_vault_error_survives_the_balance_read_that_follows_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        await vault.LockAsync();

        // The screen reads balances right after listing; the reason the list is empty must not be
        // wiped by that second call.
        await model.RefreshAsync(token);
        await model.RefreshAllFromLedgerAsync(token);

        Assert.Equal(VaultErrorMessages.For(VaultError.Locked), model.Message);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_node_answer_marks_its_network_and_the_others_still_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        NewVaultEntry reachable = GeneratedOn("reachable", "testnet");
        await vault.StoreAsync(reachable, token);
        await vault.StoreAsync(GeneratedOn("cut", "devnet"), token);
        _connections.Ledger.Balances[reachable.Address] = 25_000_000;
        _connections.BeforeConnect = (network, _) =>
            string.Equals(network, "devnet", StringComparison.Ordinal)
                ? throw new InvalidOperationException("the socket closed")
                : Task.CompletedTask;
        await model.RefreshAsync(token);

        await model.RefreshAllFromLedgerAsync(token);

        Assert.True(RowOf(model, "cut").CheckFailed);
        Assert.NotNull(model.Message);
        Assert.Equal(XrpAmount.Format(25_000_000), model.BalanceTextOf(RowOf(model, "reachable")));
        Assert.All(model.Rows, row => Assert.False(model.IsChecking(row.Reference)));
    }

    [Fact]
    public async Task A_connect_timeout_that_is_not_the_screen_closing_marks_the_row_instead_of_ending_the_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        NewVaultEntry reachable = GeneratedOn("reachable", "testnet");
        await vault.StoreAsync(reachable, token);
        await vault.StoreAsync(GeneratedOn("slow", "devnet"), token);
        _connections.Ledger.Balances[reachable.Address] = 25_000_000;
        _connections.BeforeConnect = (network, _) =>
            string.Equals(network, "devnet", StringComparison.Ordinal)
                ? throw new OperationCanceledException("connect timed out")
                : Task.CompletedTask;
        await model.RefreshAsync(token);

        await model.RefreshAllFromLedgerAsync(token);

        Assert.True(RowOf(model, "slow").CheckFailed);
        Assert.Equal(XrpAmount.Format(25_000_000), model.BalanceTextOf(RowOf(model, "reachable")));
    }

    [Fact]
    public async Task A_row_already_being_checked_is_skipped_by_the_balance_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        string reference = await vault.StoreAsync(GeneratedOn("busy", "testnet"), token);
        await model.RefreshAsync(token);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.BeforeConnect = (_, _) => gate.Task;
        Task check = model.RefreshFromLedgerAsync(reference, token);
        _connections.BeforeConnect = null;

        await model.RefreshAllFromLedgerAsync(token);

        Assert.Equal(0, _connections.Connections);
        Assert.True(model.IsChecking(reference));

        gate.SetResult();
        await check;

        Assert.Equal(1, _connections.Connections);
        Assert.False(model.IsChecking(reference));
    }

    [Fact]
    public async Task A_check_that_cannot_connect_marks_the_row_like_the_balance_read_does()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        string reference = await vault.StoreAsync(GeneratedOn("cut", "testnet"), token);
        await model.RefreshAsync(token);
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");

        await model.RefreshFromLedgerAsync(reference, token);

        Assert.Equal(AccountsModel.UnreadBalance, model.BalanceTextOf(Assert.Single(model.Rows)));
    }

    [Fact]
    public async Task A_network_that_hangs_on_connect_does_not_hold_another_networks_rows_at_checking()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);

        // The hanging network's row is stored first, so a read that went network by network would wait on it
        // before it ever reached the other one. The reachable row has no account: its read needs no vault write.
        await vault.StoreAsync(GeneratedOn("hung", "devnet"), token);
        await vault.StoreAsync(GeneratedOn("reachable", "testnet"), token);
        await model.RefreshAsync(token);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.BeforeConnect = (network, _) =>
            string.Equals(network, "devnet", StringComparison.Ordinal) ? gate.Task : Task.CompletedTask;

        Task reading = model.RefreshAllFromLedgerAsync(token);

        AccountRow reachable = RowOf(model, "reachable");
        Assert.False(model.IsChecking(reachable.Reference));
        Assert.Equal(AccountStage.KeyOnly, reachable.Stage);
        Assert.True(model.IsChecking(RowOf(model, "hung").Reference));

        gate.SetResult();
        await reading;

        Assert.All(model.Rows, row => Assert.False(model.IsChecking(row.Reference)));
    }

    [Fact]
    public async Task A_read_asks_for_the_current_ledger_once_per_connection_not_once_per_funded_row()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);

        foreach (string network in new[] { "testnet", "devnet" })
        {
            foreach (string number in new[] { "1", "2", "3" })
            {
                NewVaultEntry entry = GeneratedOn(network + number, network);
                await vault.StoreAsync(entry, token);
                _connections.Ledger.Balances[entry.Address] = 25_000_000;
            }
        }

        await model.RefreshAsync(token);
        await model.RefreshAllFromLedgerAsync(token);

        Assert.All(model.Rows, row => Assert.Equal(AccountStage.Funded, row.Stage));
        Assert.Equal(2, _connections.Connections);
        Assert.Equal(2, _connections.Ledger.CurrentLedgerReads);
    }

    [Fact]
    public async Task A_check_on_a_row_that_is_already_being_checked_leaves_the_hold_to_the_read_that_owns_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        string reference = await vault.StoreAsync(GeneratedOn("busy", "testnet"), token);
        await model.RefreshAsync(token);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.BeforeConnect = (_, _) => gate.Task;
        Task bulk = model.RefreshAllFromLedgerAsync(token);
        Assert.True(model.IsChecking(reference));

        // Returns at once when it leaves the row alone; the bound makes a regression fail rather than wait on the gate.
        await model.RefreshFromLedgerAsync(reference, token).WaitAsync(TimeSpan.FromSeconds(30), token);

        Assert.True(model.IsChecking(reference));

        gate.SetResult();
        await bulk;

        Assert.False(model.IsChecking(reference));
        Assert.Equal(1, _connections.Connections);
    }

    [Fact]
    public async Task An_unknown_row_says_not_checked_yet_only_until_a_check_has_run()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (AccountsModel model, KeyVault vault) = await OpenAsync(token);
        string reference = await vault.StoreAsync(GeneratedOn("fresh", "testnet"), token);
        await model.RefreshAsync(token);
        Assert.Equal(AccountsModel.NotCheckedStatus, model.UnknownStatusTextOf(RowOf(model, "fresh")));

        // While a node is asked, the Balance cell says so.
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections.BeforeConnect = (_, _) => gate.Task;
        _connections.ConnectThrows = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");
        Task check = model.RefreshFromLedgerAsync(reference, token);

        Assert.Equal(string.Empty, model.UnknownStatusTextOf(RowOf(model, "fresh")));

        // After a failed one, the Balance cell says that.
        gate.SetResult();
        await check;

        Assert.True(RowOf(model, "fresh").CheckFailed);
        Assert.Equal(AccountStage.Unknown, RowOf(model, "fresh").Stage);
        Assert.Equal(string.Empty, model.UnknownStatusTextOf(RowOf(model, "fresh")));

        // After an answer the row has left the Unknown stage, and the Status column has its own words.
        _connections.BeforeConnect = null;
        _connections.ConnectThrows = null;
        await model.RefreshFromLedgerAsync(reference, token);

        Assert.Equal(AccountStage.KeyOnly, RowOf(model, "fresh").Stage);
        Assert.Equal(string.Empty, model.UnknownStatusTextOf(RowOf(model, "fresh")));
    }

    private static AccountRow RowOf(AccountsModel model, string label) =>
        model.Rows.Single(row => string.Equals(row.Label, label, StringComparison.Ordinal));
}
