using Xunit;

namespace XrplFile.Keys.Tests;

public class VaultDeviceUnlockTests : VaultTestHarness
{
    [Fact]
    public async Task Device_unlock_opens_the_vault_without_the_password()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await vault.LockAsync();

        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
        Assert.True(vault.IsUnlocked);
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task Enabling_device_unlock_creates_a_platform_secret_and_wraps_the_data_key_under_it()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.Equal(0, Devices.Creations);

        bool enabled = await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);

        Assert.True(enabled);
        Assert.Equal(1, Devices.Creations);
        Assert.NotNull(ReadRecordFromFile().DeviceDek);
    }

    [Fact]
    public async Task Two_vaults_on_one_machine_do_not_take_each_others_device_unlock()
    {
        // One machine, one store, two vaults — a throwaway vault beside the real one is the
        // obvious case. The secret is asked for per vault, so neither can see, seal under, or
        // delete the other's: turning the setting off on one used to take the machine's only
        // secret away, and the other vault silently stopped opening without its password.
        CancellationToken token = TestContext.Current.CancellationToken;
        InMemoryDeviceSecretStore machine = new();

        KeyVault first = NewVaultAt(VaultPath, machine);
        await first.CreateAsync(Password("first"), token);
        Assert.True(await first.EnableDeviceUnlockAsync(token));

        KeyVault second = NewVaultAt(AnotherVaultPath("second"), machine);
        await second.CreateAsync(Password("second"), token);
        string reference = await second.StoreAsync(Entry("kept"), token);
        Assert.True(await second.EnableDeviceUnlockAsync(token));

        // Two vaults, two secrets: neither wrap can have been sealed under the other's.
        Assert.Equal(2, machine.Creations);

        await first.DisableDeviceUnlockAsync(token);
        await second.LockAsync();

        Assert.Equal(VaultUnlockResult.Success, await second.UnlockWithDeviceAsync(token));
        Assert.Equal("sSecretOfkept", (await second.RevealAsync(reference, "test", token)).Secret);

        // And the one that was turned off stays off, rather than opening on the other's secret.
        await first.LockAsync();
        Assert.Equal(VaultUnlockResult.VaultMissing, await first.UnlockWithDeviceAsync(token));
    }

    [Fact]
    public async Task A_password_change_on_one_vault_leaves_the_other_vaults_secret_alone()
    {
        // The other half of the same separation: a change re-seals the device wrap under the new
        // header, and it must reach for this vault's own secret to do it — never the one beside it.
        CancellationToken token = TestContext.Current.CancellationToken;
        InMemoryDeviceSecretStore machine = new();

        KeyVault first = NewVaultAt(VaultPath, machine);
        await first.CreateAsync(Password("old"), token);
        Assert.True(await first.EnableDeviceUnlockAsync(token));

        KeyVault second = NewVaultAt(AnotherVaultPath("second"), machine);
        await second.CreateAsync(Password("second"), token);
        Assert.True(await second.EnableDeviceUnlockAsync(token));

        Assert.Equal(
            VaultPasswordChange.Changed,
            await first.ChangePasswordAsync(Password("old"), Password("new"), token));

        await second.LockAsync();
        Assert.Equal(VaultUnlockResult.Success, await second.UnlockWithDeviceAsync(token));

        await first.LockAsync();
        Assert.Equal(VaultUnlockResult.Success, await first.UnlockWithDeviceAsync(token));
    }

    [Fact]
    public async Task Enabling_device_unlock_on_a_locked_vault_refuses()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Locked, error.Error);
    }

    [Fact]
    public async Task Enabling_device_unlock_with_no_platform_store_underneath_returns_false_and_writes_nothing()
    {
        KeyVault vault = NewVault(new NoDeviceSecretStore());
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.False(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Disabling_device_unlock_drops_both_the_field_and_the_platform_secret()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(ReadRecordFromFile().DeviceDek);

        await vault.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken);

        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.Null(await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken));

        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));

        // The password wrap was never touched.
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disabling_device_unlock_when_it_was_never_enabled_is_a_silent_no_op()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        await vault.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken);

        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.Equal(0, Devices.Creations);
    }

    [Fact]
    public async Task Unlocking_with_the_device_reports_it_missing_when_the_record_has_no_device_wrap()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Unlocking_with_the_device_reports_it_missing_where_there_is_no_vault_at_all()
    {
        KeyVault vault = NewVault();

        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unlocking_with_the_device_reports_it_missing_when_the_wrap_does_not_open_with_this_secret()
    {
        // The device wrap is there, but the secret the store now holds is not the one it was
        // sealed under. There is no password on this path at all, so this can never be reported
        // as a wrong password — only as nothing left to open with.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        await vault.LockAsync();

        await Devices.CreateAsync(VaultDeviceId, TestContext.Current.CancellationToken);

        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unlocking_with_the_device_reports_the_store_unavailable_when_it_fails()
    {
        // The record genuinely has a device wrap — written by a working store earlier — so the
        // only thing standing between this attempt and success is the store itself refusing to
        // answer. That must come back as VaultUnavailable, not as the VaultMissing a caller would
        // otherwise read as "turn the setting off".
        KeyVault creator = NewVault();
        await creator.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await creator.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        await creator.LockAsync();

        KeyVault vault = NewVault(new FailingDeviceSecretStore());

        Assert.Equal(
            VaultUnlockResult.VaultUnavailable,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_device_unlock_leaves_an_already_open_vault_open()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_password_change_does_not_invalidate_device_unlock()
    {
        // The data key is untouched by a rewrap, so the device copy still opens it.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        await vault.LockAsync();

        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);

        // And the new password still works too — the change was real, not skipped.
        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("new"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_vault_copied_where_the_device_secret_is_absent_still_opens_with_the_password()
    {
        // Portability, which is the whole reason this vault has one factor. The device wrap is
        // inert on the new machine; it must not stop the password from working.
        KeyVault original = NewVault(devices: new InMemoryDeviceSecretStore());
        await original.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await original.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        string reference = await original.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        string elsewhere = CopyVaultFileToAnotherDirectory();
        KeyVault moved = new(elsewhere, new Argon2idKdf(), new InMemoryDeviceSecretStore(), FastParameters);

        Assert.Equal(VaultUnlockResult.Success,
            await moved.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.Equal("one", (await moved.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Info.Label);
        Assert.Equal(VaultUnlockResult.VaultMissing,
            await moved.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));

        await moved.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_write_while_enabling_device_unlock_leaves_the_vault_working_once_the_disk_does()
    {
        FailableWrites disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        disk.Fail = true;
        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);
        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.True(vault.IsUnlocked);

        disk.Fail = false;
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(ReadRecordFromFile().DeviceDek);
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_failed_write_while_disabling_device_unlock_leaves_the_wrap_and_the_platform_secret_in_place()
    {
        FailableWrites disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);

        disk.Fail = true;
        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        // The rollback undid the record write, so the store must still hold what the record
        // still points at — dropping it here would strand a wrap nothing could ever open.
        Assert.NotNull(ReadRecordFromFile().DeviceDek);
        Assert.NotNull(await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken));

        disk.Fail = false;
        await vault.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.Null(await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_password_change_with_the_machine_secret_gone_drops_the_wrap_and_asks_the_store_to_forget_it()
    {
        // Genuine absence — the store says it holds nothing — is the one case that may drop the
        // wrap: one bound to a header nobody will present again is worse than none. It must be
        // dropped out loud, and it must not leave dead key material behind in the platform store.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        await Devices.DeleteAsync(VaultDeviceId, TestContext.Current.CancellationToken);
        int deletionsBefore = Devices.Deletions;

        Assert.Equal(
            VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.True(Devices.Deletions > deletionsBefore);

        // The password half of the change is untouched by the drop.
        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_password_change_on_a_machine_the_wrap_was_not_sealed_for_does_not_re_seal_it_there()
    {
        // The store answers per vault, but a path outlives the file that was at it: a secret
        // stored for this path may have been enrolled for a vault that is no longer here.
        // Re-sealing under whatever secret is found hands passwordless unlock to a machine whose
        // owner never enabled it for this file — off the back of a password change, of all things.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        // A different secret is now stored for this path — one this vault's wrap was never
        // sealed under, which is what a vault replaced at a path leaves behind.
        await Devices.CreateAsync(VaultDeviceId, TestContext.Current.CancellationToken);

        Assert.Equal(
            VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.Null(ReadRecordFromFile().DeviceDek);
        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_password_change_gives_device_unlock_to_no_vault_that_never_asked_for_it()
    {
        // A secret is stored for this path, left by whatever was here before. This vault never
        // enabled the setting, and a password change is not where it gets turned on.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await Devices.CreateAsync(VaultDeviceId, TestContext.Current.CancellationToken);

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.Null(ReadRecordFromFile().DeviceDek);
    }

    [Fact]
    public async Task A_password_change_that_cannot_open_the_wrap_leaves_the_stored_secret_where_it_is()
    {
        // The wrap did not open under the secret stored for this path, which is the proof that it
        // was enrolled for a different file — a vault copied over the one that was here. Dropping
        // our own dead wrap is right; dropping that live secret is not ours to do, and it costs
        // whatever enrolled it its device unlock.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        // A different secret is stored for this path — this vault's wrap was never sealed under it.
        await Devices.CreateAsync(VaultDeviceId, TestContext.Current.CancellationToken);
        byte[]? theOtherFilesSecret = await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken);
        int deletionsBefore = Devices.Deletions;

        Assert.Equal(
            VaultPasswordChange.Changed | VaultPasswordChange.DeviceUnlockDropped,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.Equal(deletionsBefore, Devices.Deletions);
        Assert.Equal(theOtherFilesSecret, await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disabling_device_unlock_that_was_never_enabled_leaves_the_stored_secret_alone()
    {
        // Nothing in this record ever pointed at a stored secret, so there is nothing here to
        // clean up — and the secret that is there belongs to whatever did enrol it.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await Devices.CreateAsync(VaultDeviceId, TestContext.Current.CancellationToken);
        byte[]? theOtherFilesSecret = await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken);

        await vault.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken);

        Assert.Equal(theOtherFilesSecret, await Devices.GetAsync(VaultDeviceId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disabling_device_unlock_on_a_wrap_this_machine_cannot_open_leaves_its_secret_alone()
    {
        // A vault carried to another machine keeps its device wrap, and the wrap is inert there
        // (4.2). Turning the setting off on the copy must clear that inert wrap and stop — the
        // secret on this machine was sealed for some other vault, and it is still opening it.
        InMemoryDeviceSecretStore machineA = new();
        KeyVault onA = NewVault(machineA);
        await onA.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.True(await onA.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        string elsewhere = CopyVaultFileToAnotherDirectory();
        InMemoryDeviceSecretStore machineB = new();
        await machineB.CreateAsync(Path.GetFullPath(elsewhere), TestContext.Current.CancellationToken);
        byte[]? machineBsSecret = await machineB.GetAsync(Path.GetFullPath(elsewhere), TestContext.Current.CancellationToken);

        KeyVault onB = new(elsewhere, new Argon2idKdf(), machineB, FastParameters);
        try
        {
            Assert.Equal(
                VaultUnlockResult.Success,
                await onB.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));

            await onB.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken);

            Assert.Equal(machineBsSecret, await machineB.GetAsync(Path.GetFullPath(elsewhere), TestContext.Current.CancellationToken));
        }
        finally
        {
            await onB.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enabling_device_unlock_over_a_store_that_fails_reports_a_typed_vault_failure()
    {
        // Every other failure on this type is a VaultException a caller can switch on. A raw
        // IOException out of the platform store is the one that would escape unhandled.
        KeyVault vault = NewVault(new FailingDeviceSecretStore());
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Asking_for_a_device_wrap_with_no_file_says_no_and_touches_nothing()
    {
        KeyVault vault = NewVault();

        Assert.False(await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Asking_for_a_device_wrap_on_a_locked_vault_with_none_says_no_without_opening_it()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        Assert.False(await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Asking_for_a_device_wrap_on_a_locked_vault_that_carries_one_says_yes_without_opening_it()
    {
        // This is the read that replaces the old unlock-then-relock probe (design review, fix
        // round 1): a parse of the record, never a real unlock.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await vault.LockAsync();

        Assert.True(await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Asking_for_a_device_wrap_on_an_open_vault_answers_from_the_cache()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.False(await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken));

        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        Assert.True(await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken));
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Asking_for_a_device_wrap_the_platform_secret_no_longer_opens_still_says_yes()
    {
        // The honest limit of this read: it answers from the record alone, so a wrap the store can
        // no longer open still reads as present. That gap is why this is not the same question
        // UnlockWithDeviceAsync answers -- see the interface's remarks.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await vault.LockAsync();
        await Devices.DeleteAsync(VaultDeviceId, TestContext.Current.CancellationToken);

        Assert.True(await vault.HasDeviceWrapAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
    }
}
