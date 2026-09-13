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
        Assert.Null(await Devices.GetAsync(TestContext.Current.CancellationToken));

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

        await Devices.CreateAsync(TestContext.Current.CancellationToken);

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

        Assert.True(await vault.ChangePasswordAsync(
            Password("old"), Password("new"), TestContext.Current.CancellationToken));
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
        Assert.NotNull(await Devices.GetAsync(TestContext.Current.CancellationToken));

        disk.Fail = false;
        await vault.DisableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        Assert.Null(ReadRecordFromFile().DeviceDek);
        Assert.Null(await Devices.GetAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The disk, with a switch. Mirrors the double in <c>KeyVaultTests</c>.</summary>
    private sealed class FailableWrites
    {
        internal bool Fail { get; set; }

        internal Task WriteAsync(string path, byte[] contents, CancellationToken cancellationToken) =>
            Fail
                ? Task.FromException(new VaultException(VaultError.StorageFailed, "The disk refused the write."))
                : DurableFile.WriteAsync(path, contents, cancellationToken);
    }

    /// <summary>A device secret store that cannot even answer whether it holds a secret.</summary>
    private sealed class FailingDeviceSecretStore : IDeviceSecretStore
    {
        public Task<byte[]?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromException<byte[]?>(new IOException("The platform secret store is unavailable."));

        public Task<byte[]> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new IOException("The platform secret store is unavailable."));

        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
