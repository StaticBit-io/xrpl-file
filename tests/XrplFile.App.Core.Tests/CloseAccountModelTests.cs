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
    public async Task CanPreflight_follows_whether_a_destination_has_been_named()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        Assert.False(model.CanPreflight);

        model.ChooseDestination(Home);
        Assert.True(model.CanPreflight);

        model.ChooseDestination(string.Empty);
        Assert.False(model.CanPreflight);
    }

    [Fact]
    public async Task The_other_keys_in_the_vault_are_offered_as_destinations_and_this_account_is_not()
    {
        (CloseAccountModel model, KeyVault vault, string reference, XrplWallet wallet) =
            await OpenAsync(TestContext.Current.CancellationToken);

        // Network "1", not VaultHarness.Entry's default "standalone": SelectAsync now filters
        // candidates to the network of the account being closed (see the network test below), and
        // this test is about the reference/address exclusion, not the network filter.
        await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "somewhere else", "1", "rAddressOfSomewhereElse", "sSecretOfSomewhereElse"),
            TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(model.Candidates, info => string.Equals(info.Address, wallet.ClassicAddress, StringComparison.Ordinal));
        Assert.Contains(model.Candidates, info => string.Equals(info.Label, "somewhere else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Candidates_exclude_the_same_address_a_different_network_and_an_already_closed_entry()
    {
        (CloseAccountModel model, KeyVault vault, string reference, XrplWallet wallet) =
            await OpenAsync(TestContext.Current.CancellationToken);

        // The same address as the account being closed, filed under a second reference — offering
        // it would point the remainder right back at the account being destroyed. On network "2"
        // rather than "1": the vault itself already refuses a second entry for the same address on
        // the same network (VaultError.DuplicateAddress), so the only way this situation can arise
        // at all is across two different networks — which the network filter below would also catch.
        // Kept as its own entry anyway, since the address check is the one that is actually correct
        // to rely on here (a network match is not the reason this candidate is unusable).
        string duplicateAddressReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "duplicate address", "2", wallet.ClassicAddress, wallet.Seed),
            TestContext.Current.CancellationToken);

        // A real destination, but on a network this deletion cannot reach.
        string otherNetworkReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "other network", "2", "rAddressOfOtherNetwork", "sSecretOfOtherNetwork"),
            TestContext.Current.CancellationToken);

        // An account that no longer exists.
        string closedReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "already closed", "1", "rAddressOfClosed", "sSecretOfClosed"),
            TestContext.Current.CancellationToken);
        await vault.UpdateAsync(
            closedReference,
            new VaultEntryEdit(Closure: new AccountClosure(DateTimeOffset.UnixEpoch, "rSomewhereElse", 1_000_000, "HASH")),
            TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(model.Candidates, info => string.Equals(info.Reference, duplicateAddressReference, StringComparison.Ordinal));
        Assert.DoesNotContain(model.Candidates, info => string.Equals(info.Reference, otherNetworkReference, StringComparison.Ordinal));
        Assert.DoesNotContain(model.Candidates, info => string.Equals(info.Reference, closedReference, StringComparison.Ordinal));
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
    public async Task Changing_the_destination_after_a_ready_preflight_clears_it()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[Home] = 5_000_000;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanClose);

        // A different destination than the one the preflight actually priced: confirming against
        // the old figures now would mean the money could go somewhere nobody checked.
        string anotherHome = XrplWallet.Generate().ClassicAddress;
        model.ChooseDestination(anotherHome);

        Assert.False(model.CanClose);
        Assert.Null(model.Preview);
    }

    [Fact]
    public async Task Selecting_a_different_account_after_a_ready_preflight_clears_it()
    {
        (CloseAccountModel model, KeyVault vault, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[Home] = 5_000_000;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanClose);

        XrplWallet other = XrplWallet.Generate();
        string otherReference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "another throwaway", "1", other.ClassicAddress, other.Seed),
            TestContext.Current.CancellationToken);

        // Switching to a different account without choosing a fresh destination or re-running the
        // preflight must not leave the stale Ready verdict (and its stale figures) confirmable.
        await model.SelectAsync(otherReference, TestContext.Current.CancellationToken);

        Assert.False(model.CanClose);
        Assert.Null(model.Preview);
    }

    [Fact]
    public async Task A_preflight_that_is_not_yet_ready_exposes_ledgers_remaining_matching_the_ledger_it_names()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[Home] = 5_000_000;

        // OpenAsync's default sequence (10) plus DeletionSequenceDelta (254) puts the deletable
        // ledger at 264; a current ledger short of that forces NotYet on the sequence threshold
        // specifically -- not the balance-too-low branch, which never names a DeletableFromLedger
        // for LedgersRemaining to be measured against.
        _connections.Ledger.CurrentLedger = 200;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, model.Preview!.Outcome);
        Assert.Equal(264, model.Preview.DeletableFromLedger);
        Assert.Equal(model.Preview.DeletableFromLedger!.Value - 200, model.LedgersRemaining);
        Assert.Equal(64, model.LedgersRemaining);

        // Design 5.2/6: how long the wait roughly is, not only how many ledgers remain.
        Assert.Equal(LedgerTiming.Describe(LedgerTiming.Estimate(64)), model.TimeRemainingDescription);
        Assert.NotNull(model.TimeRemainingDescription);
    }

    [Fact]
    public async Task A_failed_countdown_read_does_not_lose_a_notyet_preview()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Ledger.Balances[Home] = 5_000_000;
        _connections.Ledger.CurrentLedger = 200; // forces NotYet on the sequence threshold, as above

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);

        // ThrowawayAccount.PreviewDeletionAsync's own threshold read is the first call this fake
        // sees and must still succeed, or NotYet would never come back with a DeletableFromLedger to
        // count down from at all; only PreflightAsync's own second, independent read -- the one that
        // exists purely for the countdown -- is made to fail.
        _connections.Ledger.CurrentLedgerFailure = new XrplFileException(XrplFileError.NodeUnreachable, "no answer");
        _connections.Ledger.CurrentLedgerFailsFromCall = 2;

        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Preview);
        Assert.Equal(DeletionOutcome.NotYet, model.Preview.Outcome);
        Assert.Equal(264, model.Preview.DeletableFromLedger);
        Assert.Null(model.LedgersRemaining);
        Assert.Equal(CloseErrorMessages.For(DeletionOutcome.NotYet), model.Message);
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
    public async Task A_vault_failure_after_a_successful_submission_still_reports_the_closure_and_the_hash()
    {
        (CloseAccountModel _, KeyVault vault, string reference, XrplWallet _) =
            await OpenAsync(TestContext.Current.CancellationToken);

        // The deletion itself goes through the real vault (so SelectAsync/PreflightAsync/signing all
        // work normally); only the write that would record the closure fails — the ordinary way that
        // happens is the vault auto-locking while the person reads the confirmation, so a raw
        // exception unrelated to VaultException is used here to also prove the broader catch works,
        // not only the one written for a locked vault.
        CloseAccountModel model = new(new FailingUpdateVault(vault), _connections, TimeProvider.System);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model.Preview);
        Assert.Equal(DeletionOutcome.Submitted, model.Preview.Outcome);

        // The node already accepted the deletion: that must not be erased by the vault write failing.
        Assert.Null(model.Closed);
        Assert.NotNull(model.Message);
        Assert.StartsWith(CloseErrorMessages.For(DeletionOutcome.Submitted), model.Message, StringComparison.Ordinal);
        Assert.Contains(model.Preview.TxHash, model.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task A_refused_deletion_says_the_fee_was_still_claimed_and_will_not_close_again()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        _connections.Sink.Refusals[0] = "tecTOO_SOON";

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanClose);

        await model.CloseAsync(TestContext.Current.CancellationToken);

        // Not PublishErrorMessages.For's own SubmitRefused sentence — that one is written for the
        // publishing tab, where a refusal costs the open-ledger fee rather than a whole owner reserve.
        // tecTOO_SOON is a tec* result, which claims the fee -- true here.
        Assert.Equal(CloseErrorMessages.DeletionRefused(feeClaimed: true), model.Message);

        // The node has just proved the preflight's Ready conclusion false: a second click must not
        // be able to submit again against the same stale conclusion and burn a second reserve.
        Assert.Null(model.Preview);
        Assert.False(model.CanClose);
    }

    [Fact]
    public async Task A_refusal_that_never_applied_does_not_claim_a_fee_was_burned()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        // telINSUF_FEE_P is a local check, not a tec* result: it never reaches the ledger and never
        // claims the fee. Saying otherwise would tell somebody an owner reserve was just burned
        // proving what a typo, or a self-destination, already made obvious for free.
        _connections.Sink.Refusals[0] = "telINSUF_FEE_P";

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        await model.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloseErrorMessages.DeletionRefused(feeClaimed: false), model.Message);
        Assert.Null(model.Preview);
        Assert.False(model.CanClose);
    }

    [Fact]
    public async Task Closing_an_account_to_its_own_address_is_refused_without_submitting()
    {
        (CloseAccountModel model, _, string reference, XrplWallet wallet) =
            await OpenAsync(TestContext.Current.CancellationToken);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        // Not one of Model.Candidates -- SelectAsync already excludes this account's own address --
        // but nothing stops it from being typed in by hand, which is exactly how this is reached.
        model.ChooseDestination(wallet.ClassicAddress);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.DestinationRefuses, model.Preview!.Outcome);
        Assert.False(model.CanClose);
        Assert.Empty(_connections.Sink.Submitted);
    }

    [Fact]
    public async Task An_account_nobody_has_funded_is_checked_without_asking_the_node_for_its_sequence()
    {
        (CloseAccountModel model, _, string reference, _) =
            await OpenAsync(TestContext.Current.CancellationToken, balanceDrops: 0);

        // Makes this a real regression guard: if the balance gate inside
        // ThrowawayAccount.PreviewDeletionAsync ever moved back after the sequence read, this fake
        // would answer the RippledException a real node's actNotFound produces instead of NotYet.
        _connections.Sink.ThrowForUnfundedAccounts = true;

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DeletionOutcome.NotYet, model.Preview!.Outcome);
        Assert.Null(model.Preview.DeletableFromLedger);
        Assert.Null(model.LedgersRemaining);
        Assert.Equal(CloseErrorMessages.For(DeletionOutcome.NotYet), model.Message);
    }

    [Fact]
    public async Task A_non_refusal_failure_after_submitting_also_stops_a_second_click()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        // Not a refusal at all: the node accepted the submission or lost the connection right
        // after, and this call never learned which. SubmitRefused is the only code
        // ThrowawayAccount.DeleteAsync throws for an actual refusal -- everything else that can
        // escape from inside it (NodeUnreachable above all) must be just as disqualifying for a
        // second click, since a signed AccountDelete may already be on its way regardless of what
        // this exception says.
        _connections.Sink.Throws[0] = new XrplFileException(XrplFileError.NodeUnreachable, "the node vanished mid-submit");

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanClose);

        await model.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PublishErrorMessages.For(XrplFileError.NodeUnreachable), model.Message);
        Assert.Null(model.Preview);
        Assert.False(model.CanClose);
    }

    [Fact]
    public async Task A_cancellation_while_the_deletion_is_on_the_wire_leaves_an_explanation_not_silence()
    {
        (CloseAccountModel model, _, string reference, _) = await OpenAsync(TestContext.Current.CancellationToken);
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        model.ChooseDestination(Home);
        await model.PreflightAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanClose);

        // Long enough that the cancellation below reliably lands while FakeLedgerSink.SubmitAsync's
        // own delay is still in flight -- i.e. after DeleteAsync has already signed and started
        // submitting, the case this catch exists for.
        _connections.Sink.SubmitDelay = TimeSpan.FromMilliseconds(300);
        using CancellationTokenSource cts = new();
        Task closing = model.CloseAsync(cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        // Must complete, not fault or cancel: a Razor component's Dispose() is exactly where this
        // token gets cancelled from, and a screen tearing down is not a place an unhandled
        // cancellation can be usefully observed.
        await closing;

        Assert.Null(model.Preview);
        Assert.False(model.CanClose);
        Assert.Contains("interrupted", model.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentReference_is_set_only_once_a_select_has_actually_resolved()
    {
        (CloseAccountModel model, KeyVault vault, string reference, _) =
            await OpenAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.CurrentReference);

        await model.SelectAsync(reference, TestContext.Current.CancellationToken);
        Assert.Equal(reference, model.CurrentReference);

        await vault.LockAsync();
        await model.SelectAsync(reference, TestContext.Current.CancellationToken);

        // A failed select must not leave CurrentReference pointing at data that no longer belongs
        // to this reference -- this is the whole guard the closing screen renders behind.
        Assert.Null(model.CurrentReference);
        Assert.Equal(VaultErrorMessages.For(VaultError.Locked), model.Message);
    }

    /// <summary>
    /// Delegates every member to a real vault except <see cref="UpdateAsync"/>, which always fails —
    /// so a test can exercise a write that fails after everything before it (listing, revealing,
    /// signing) genuinely succeeded. Throws a raw exception rather than <see cref="VaultException"/>
    /// on purpose, to prove <see cref="CloseAccountModel.CloseAsync"/>'s catch around this one write
    /// is not narrowed to the vault's own exception type.
    /// </summary>
    private sealed class FailingUpdateVault(IKeyVault inner) : IKeyVault
    {
        public bool IsUnlocked => inner.IsUnlocked;

        public IReadOnlyCollection<string> References => inner.References;

        public IReadOnlyList<string> DamagedReferences => inner.DamagedReferences;

        public bool WritesAreRefused => inner.WritesAreRefused;

        public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) =>
            inner.AcknowledgeDamageAsync(cancellationToken);

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => inner.ExistsAsync(cancellationToken);

        public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
            inner.HasDeviceWrapAsync(cancellationToken);

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.CreateAsync(password, cancellationToken);

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.UnlockAsync(password, cancellationToken);

        public Task LockAsync() => inner.LockAsync();

        public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken);

        public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
            inner.StoreAsync(entry, cancellationToken);

        public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken) =>
            inner.RevealAsync(reference, reason, cancellationToken);

        public Task<byte[]> ExportAsync(
            string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
            inner.ExportAsync(reference, containerPassword, cancellationToken);

        public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
            throw new IOException("fake: the vault's storage did not respond");

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            inner.DeleteAsync(reference, cancellationToken);

        public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
            inner.UnlockWithDeviceAsync(cancellationToken);

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            inner.ChangePasswordAsync(oldPassword, newPassword, cancellationToken);

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.EnableDeviceUnlockAsync(cancellationToken);

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.DisableDeviceUnlockAsync(cancellationToken);
    }
}
