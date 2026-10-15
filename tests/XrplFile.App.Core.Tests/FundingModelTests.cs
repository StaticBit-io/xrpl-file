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

    [Fact]
    public async Task Polling_asks_again_every_interval_until_the_account_exists_and_then_stops()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(model.ShouldPoll);
        int connectionsBefore = _connections.Connections;

        List<TimeSpan> waits = [];
        Task Delay(TimeSpan interval, CancellationToken token)
        {
            waits.Add(interval);
            if (waits.Count == 3) _connections.Ledger.Balances[entry.Address] = 20_000_000;
            return Task.CompletedTask;
        }

        await model.PollAsync(null, TestContext.Current.CancellationToken, Delay);

        Assert.Equal([FundingModel.PollInterval, FundingModel.PollInterval, FundingModel.PollInterval], waits);
        Assert.Equal(3, _connections.Connections - connectionsBefore);
        Assert.True(model.AccountExists);
        Assert.Equal(20_000_000, model.BalanceDrops);
        Assert.False(model.ShouldPoll);
    }

    [Fact]
    public void The_poll_interval_is_not_shorter_than_ten_seconds()
    {
        // Every lookup opens a WebSocket and public nodes rate-limit per IP.
        Assert.True(FundingModel.PollInterval >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Polling_an_account_that_already_exists_never_waits()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[entry.Address] = 30_000_000;
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        int waits = 0;

        await model.PollAsync(null, TestContext.Current.CancellationToken, (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task An_account_found_by_a_check_during_the_wait_is_not_looked_up_again()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        int connectionsBefore = _connections.Connections;

        await model.PollAsync(null, TestContext.Current.CancellationToken, async (_, token) =>
        {
            _connections.Ledger.Balances[entry.Address] = 20_000_000;
            await model.RefreshAsync(token);
        });

        Assert.Equal(1, _connections.Connections - connectionsBefore);
        Assert.True(model.AccountExists);
    }

    [Fact]
    public async Task Polling_does_not_start_after_a_select_that_failed()
    {
        (FundingModel model, string reference, _, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        await vault.LockAsync();
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        int waits = 0;

        Assert.False(model.ShouldPoll);
        await model.PollAsync(null, TestContext.Current.CancellationToken, (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, waits);
        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task Polling_ends_quietly_when_cancelled_and_asks_the_node_no_more()
    {
        (FundingModel model, string reference, _, _) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        using CancellationTokenSource cts = new();

        await model.PollAsync(null, cts.Token, (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        Assert.Equal(0, _connections.Connections);
    }

    [Fact]
    public async Task Polling_tells_the_screen_before_and_after_every_lookup()
    {
        (FundingModel model, string reference, NewVaultEntry entry, _) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        List<bool> refreshing = [];

        await model.PollAsync(
            () =>
            {
                refreshing.Add(model.IsRefreshing);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken,
            (_, _) =>
            {
                _connections.Ledger.Balances[entry.Address] = 20_000_000;
                return Task.CompletedTask;
            });

        Assert.Equal([false, false], refreshing);
        Assert.False(model.IsRefreshing);
    }

    [Fact]
    public async Task Two_refreshes_asked_together_are_run_one_after_the_other()
    {
        (_, string reference, _, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        HoldingConnections holding = new(_connections);
        FundingModel model = new(vault, holding, _clipboard);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Task first = model.RefreshAsync(TestContext.Current.CancellationToken);
        await holding.Entered.WaitAsync(TestContext.Current.CancellationToken);
        Task second = model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsRefreshing);
        Assert.Equal(1, holding.Started);

        holding.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(2, holding.Started);
        Assert.Equal(1, holding.MostAtOnce);
        Assert.False(model.IsRefreshing);
    }

    [Fact]
    public async Task An_answer_for_the_previous_account_is_not_shown_under_the_new_one()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), TestContext.Current.CancellationToken);
        NewVaultEntry first = Entry("first", VaultEntryKind.Generated);
        NewVaultEntry second = Entry("second", VaultEntryKind.Generated);
        string firstReference = await vault.StoreAsync(first, TestContext.Current.CancellationToken);
        string secondReference = await vault.StoreAsync(second, TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[first.Address] = 40_000_000;

        HoldingConnections holding = new(_connections);
        FundingModel model = new(vault, holding, _clipboard);
        await model.SelectAsync(firstReference, TestContext.Current.CancellationToken);

        Task late = model.RefreshAsync(TestContext.Current.CancellationToken);
        await holding.Entered.WaitAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(secondReference, TestContext.Current.CancellationToken);
        holding.Release();
        await late;

        Assert.Equal(second.Address, model.Address);
        Assert.Equal(0, model.BalanceDrops);
        Assert.False(model.AccountExists);
    }

    /// <summary>
    /// A connection factory that parks every lookup at the connect until <see cref="Release"/>,
    /// and counts how many are inside at once: a node answering slowly while the screen asks again.
    /// </summary>
    private sealed class HoldingConnections(IConnectionFactory inner) : IConnectionFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;

        public Task Entered => _entered.Task;

        public int Started { get; private set; }

        public int MostAtOnce { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<ILedgerConnection> ConnectAsync(string network, CancellationToken cancellationToken)
        {
            Started++;
            _inside++;
            MostAtOnce = Math.Max(MostAtOnce, _inside);
            _entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return await inner.ConnectAsync(network, cancellationToken);
            }
            finally
            {
                _inside--;
            }
        }
    }
}
