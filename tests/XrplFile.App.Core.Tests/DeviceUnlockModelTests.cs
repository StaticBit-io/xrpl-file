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
        Assert.True(model.Enabled);
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

        Assert.False(model.Enabled);
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

        Assert.False(model.Enabled);
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
            Assert.False(model.Enabled);
            Assert.False(await vault.HasDeviceWrapAsync(token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
