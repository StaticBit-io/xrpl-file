using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class DeviceUnlockModelTests : VaultHarness
{
    [Fact]
    public async Task Enabling_turns_the_switch_on_and_creates_a_device_wrap()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        DeviceUnlockModel model = new(vault);

        bool enabled = await model.EnableAsync(token);

        Assert.True(enabled);
        Assert.True(await model.IsEnabledAsync(token));
        Assert.True(await vault.HasDeviceWrapAsync(token));
    }

    [Fact]
    public async Task Disabling_is_safe_when_it_was_never_on()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        DeviceUnlockModel model = new(vault);

        await model.DisableAsync(token);

        Assert.False(await model.IsEnabledAsync(token));
        Assert.False(await vault.HasDeviceWrapAsync(token));
    }

    [Fact]
    public async Task Disabling_after_enabling_turns_the_switch_off_and_removes_the_wrap()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        DeviceUnlockModel model = new(vault);
        Assert.True(await model.EnableAsync(token));

        await model.DisableAsync(token);

        Assert.False(await model.IsEnabledAsync(token));
        Assert.False(await vault.HasDeviceWrapAsync(token));
    }

    [Fact]
    public async Task Enabling_with_no_device_store_available_returns_false_and_changes_nothing()
    {
        // NoDeviceSecretStore.CreateAsync throws NotSupportedException — the "this build has no
        // platform store at all" case KeyVault.EnableDeviceUnlockAsync answers with false rather
        // than a throw, distinct from a store that fails (VaultError.StorageFailed). Built with
        // the vault's public constructor directly rather than through VaultHarness.NewVault,
        // which always wires up the harness's own InMemoryDeviceSecretStore.
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("xrpl-file-app-no-device-store-").FullName;
        try
        {
            KdfParameters fast = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
            await using KeyVault vault = new(
                Path.Combine(directory, "vault.json"), new Argon2idKdf(), new NoDeviceSecretStore(), fast);
            await vault.CreateAsync(Password("open sesame"), token);
            DeviceUnlockModel model = new(vault);

            bool enabled = await model.EnableAsync(token);

            Assert.False(enabled);
            Assert.False(await model.IsEnabledAsync(token));
            Assert.False(await vault.HasDeviceWrapAsync(token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_password_change_that_drops_the_wrap_is_reflected_without_this_model_touching_it()
    {
        // Finding 1: Enabled must not be a field this model caches. Enable, then change the
        // password through PasswordChangeModel — a different model, going straight to the vault —
        // in circumstances where the vault cannot re-seal the wrap under the new header and drops
        // it (VaultPasswordChange.DeviceUnlockDropped; see
        // VaultDeviceUnlockTests.A_password_change_with_the_machine_secret_gone_drops_the_wrap_and_asks_the_store_to_forget_it
        // for the same recipe: delete the platform secret out from under the vault first). Nothing
        // in this DeviceUnlockModel instance is told about that change directly — only asking the
        // vault can catch it.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old password"), token);
        DeviceUnlockModel model = new(vault);
        Assert.True(await model.EnableAsync(token));
        Assert.True(await model.IsEnabledAsync(token));

        await Devices.DeleteAsync(Path.GetFullPath(VaultPath), token);
        PasswordChangeModel passwordChange = new(vault, VaultPath);
        PasswordChangeReport report = await passwordChange.ChangeAsync(
            Password("old password"), Password("new password"), token);
        Assert.True(report.Changed);

        Assert.False(await model.IsEnabledAsync(token));
        Assert.False(await vault.HasDeviceWrapAsync(token));
    }
}
