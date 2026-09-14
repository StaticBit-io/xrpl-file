using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Pins the one property <see cref="UnlockModel.RefreshAsync"/> must never give up, however this
/// model changes later: a refresh answers questions about the vault, it never answers them by
/// changing the vault. Before this fix, <c>RefreshAsync</c> answered
/// <see cref="UnlockModel.DeviceUnlockOffered"/> by actually calling
/// <see cref="IKeyVault.UnlockWithDeviceAsync"/> and, on success, calling
/// <see cref="IKeyVault.LockAsync"/> to put the vault back — two separate calls against a vault
/// whose gate releases between them. A real press on "unlock with device", landing in that gap,
/// left the vault locked underneath a screen that still said "in".
/// </summary>
public class UnlockModelRefreshInvariantTests : VaultHarness
{
    [Fact]
    public async Task Refresh_never_changes_a_locked_vaults_open_state()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await model.LockAsync();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Refresh_never_changes_an_unlocked_vaults_open_state()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(vault.IsUnlocked);
    }

    /// <summary>
    /// Reproduces the exact race the review described, deterministically rather than by timing
    /// luck: a real "unlock with device" press landing in the window a probe-based refresh used to
    /// open between its own internal unlock and its compensating lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RaceWindowVault"/> wraps a real <see cref="KeyVault"/> and can delay one
    /// <see cref="IKeyVault.LockAsync"/> call until released, and it signals the first time
    /// <see cref="IKeyVault.UnlockWithDeviceAsync"/> completes. Against the old, probe-based
    /// <c>RefreshAsync</c>, that signal fires partway through the refresh — the probe's own unlock
    /// has just genuinely opened the vault, and its compensating lock is the one being held open.
    /// That is the window: this test presses the real button right there, then releases the held
    /// lock and lets the refresh finish.
    /// </para>
    /// <para>
    /// Against the fixed <c>RefreshAsync</c>, which answers <see cref="UnlockModel.DeviceUnlockOffered"/>
    /// by reading the record instead of unlocking it, that signal never fires — refresh completes
    /// with no call to <see cref="IKeyVault.UnlockWithDeviceAsync"/> at all, so there is no window
    /// to land in. Racing the refresh against the signal with <see cref="Task.WhenAny(Task, Task)"/>
    /// lets one test exercise both shapes without hanging on either: the real button press and the
    /// final assertions run either way, in whichever order the implementation under test permits.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_real_unlock_landing_during_a_refresh_is_never_undone_by_it()
    {
        KeyVault innerVault = NewVault();
        RaceWindowVault vault = new(innerVault);
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        Assert.True(await innerVault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await model.LockAsync();

        TaskCompletionSource lockGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vault.DelayNextLock(lockGate);

        Task refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        Task raceWindowOpened = vault.WaitForFirstDeviceUnlockAsync();

        Task first = await Task.WhenAny(refresh, raceWindowOpened);

        if (first == raceWindowOpened)
        {
            // The old, buggy shape: refresh's own probe just unlocked the vault for real and is
            // blocked right here, about to lock it back. Press the real button in that exact gap.
            VaultUnlockResult pressed = await model.UnlockWithDeviceAsync(TestContext.Current.CancellationToken);
            Assert.Equal(VaultUnlockResult.Success, pressed);

            lockGate.SetResult();
            await refresh;
        }
        else
        {
            // The fixed shape: refresh finished on its own, having never touched the lock state.
            // There is no window to land in, so press the button afterwards instead.
            await refresh;
            VaultUnlockResult pressed = await model.UnlockWithDeviceAsync(TestContext.Current.CancellationToken);
            Assert.Equal(VaultUnlockResult.Success, pressed);
        }

        // Whichever shape ran: a deliberate press on "unlock with device" that reported Success
        // must leave the vault genuinely open. A refresh in flight — before or after — must never
        // be the thing that takes that back.
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
        Assert.True(vault.IsUnlocked);
    }

    /// <summary>
    /// Wraps a real <see cref="KeyVault"/> so a test can hold one <see cref="LockAsync"/> call open
    /// and observe when <see cref="UnlockWithDeviceAsync"/> first completes, without reimplementing
    /// any of the vault's own behaviour.
    /// </summary>
    private sealed class RaceWindowVault(IKeyVault inner) : IKeyVault
    {
        private readonly TaskCompletionSource _firstDeviceUnlockCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource? _lockGate;
        private int _deviceUnlockCalls;

        /// <summary>Completes the first time a call to <see cref="UnlockWithDeviceAsync"/> returns.</summary>
        public Task WaitForFirstDeviceUnlockAsync() => _firstDeviceUnlockCompleted.Task;

        /// <summary>Holds the next call to <see cref="LockAsync"/> open until <paramref name="gate"/> completes.</summary>
        public void DelayNextLock(TaskCompletionSource gate) => _lockGate = gate;

        public bool IsUnlocked => inner.IsUnlocked;

        public IReadOnlyCollection<string> References => inner.References;

        public IReadOnlyList<string> DamagedReferences => inner.DamagedReferences;

        public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) =>
            inner.AcknowledgeDamageAsync(cancellationToken);

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => inner.ExistsAsync(cancellationToken);

        public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
            inner.HasDeviceWrapAsync(cancellationToken);

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.CreateAsync(password, cancellationToken);

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.UnlockAsync(password, cancellationToken);

        public async Task LockAsync()
        {
            if (_lockGate is not null)
            {
                await _lockGate.Task.ConfigureAwait(false);
            }

            await inner.LockAsync().ConfigureAwait(false);
        }

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
            inner.UpdateAsync(reference, edit, cancellationToken);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            inner.DeleteAsync(reference, cancellationToken);

        public async Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken)
        {
            VaultUnlockResult result = await inner.UnlockWithDeviceAsync(cancellationToken).ConfigureAwait(false);

            if (Interlocked.Increment(ref _deviceUnlockCalls) == 1)
            {
                _firstDeviceUnlockCompleted.TrySetResult();
            }

            return result;
        }

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            inner.ChangePasswordAsync(oldPassword, newPassword, cancellationToken);

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.EnableDeviceUnlockAsync(cancellationToken);

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.DisableDeviceUnlockAsync(cancellationToken);
    }
}

/// <summary>
/// Pins the mechanism behind <see cref="UnlockModel.DeviceUnlockOffered"/>, not just its outcome:
/// answering it must cost a read of the record, never a real unlock and a compensating lock. The
/// outcome-only assertions in <see cref="UnlockModelDeviceUnlockTests"/> could not tell a correct
/// read apart from an unlock-then-relock that happened to land cleanly — this can, by counting.
/// </summary>
public class UnlockModelRefreshCostTests : VaultHarness
{
    [Fact]
    public async Task Offering_device_unlock_costs_no_call_to_unlock_or_lock_the_vault()
    {
        KeyVault innerVault = NewVault();
        CallCountingVault vault = new(innerVault);
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        Assert.True(await innerVault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await model.LockAsync();
        vault.ResetCounts();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(model.DeviceUnlockOffered);
        Assert.Equal(0, vault.UnlockWithDeviceCalls);
        Assert.Equal(0, vault.LockCalls);
    }

    [Fact]
    public async Task Withholding_device_unlock_also_costs_no_call_to_unlock_or_lock_the_vault()
    {
        KeyVault innerVault = NewVault();
        CallCountingVault vault = new(innerVault);
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        await model.LockAsync();
        vault.ResetCounts();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(model.DeviceUnlockOffered);
        Assert.Equal(0, vault.UnlockWithDeviceCalls);
        Assert.Equal(0, vault.LockCalls);
    }

    /// <summary>A real <see cref="KeyVault"/> wrapped to count how often its lock-touching members are called.</summary>
    private sealed class CallCountingVault(IKeyVault inner) : IKeyVault
    {
        public int UnlockWithDeviceCalls { get; private set; }

        public int LockCalls { get; private set; }

        public void ResetCounts()
        {
            UnlockWithDeviceCalls = 0;
            LockCalls = 0;
        }

        public bool IsUnlocked => inner.IsUnlocked;

        public IReadOnlyCollection<string> References => inner.References;

        public IReadOnlyList<string> DamagedReferences => inner.DamagedReferences;

        public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) =>
            inner.AcknowledgeDamageAsync(cancellationToken);

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => inner.ExistsAsync(cancellationToken);

        public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
            inner.HasDeviceWrapAsync(cancellationToken);

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.CreateAsync(password, cancellationToken);

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.UnlockAsync(password, cancellationToken);

        public Task LockAsync()
        {
            LockCalls++;
            return inner.LockAsync();
        }

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
            inner.UpdateAsync(reference, edit, cancellationToken);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            inner.DeleteAsync(reference, cancellationToken);

        public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken)
        {
            UnlockWithDeviceCalls++;
            return inner.UnlockWithDeviceAsync(cancellationToken);
        }

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            inner.ChangePasswordAsync(oldPassword, newPassword, cancellationToken);

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.EnableDeviceUnlockAsync(cancellationToken);

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.DisableDeviceUnlockAsync(cancellationToken);
    }
}
