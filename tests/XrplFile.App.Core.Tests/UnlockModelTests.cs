using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class UnlockModelTests : VaultHarness
{
    [Fact]
    public async Task A_fresh_directory_offers_to_create_a_vault()
    {
        UnlockModel model = new(NewVault());

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task Creating_leaves_the_vault_open()
    {
        UnlockModel model = new(NewVault());
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }

    [Fact]
    public async Task A_wrong_password_reports_it_and_leaves_the_vault_shut()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        VaultUnlockResult result = await model.UnlockAsync(Password("wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.WrongPassword, result);
        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.True(model.LastMessage!.CountsAsAFailedAttempt);
    }

    [Fact]
    public async Task A_degraded_unlock_leaves_the_user_inside()
    {
        // VaultDegraded means the password worked and something inside is damaged.
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await model.LockAsync();
        FlipOneByteOfEntry(reference);

        VaultUnlockResult result = await model.UnlockAsync(Password("p"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }

    [Fact]
    public async Task A_right_password_leaves_no_failed_attempt_and_a_message()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        VaultUnlockResult result = await model.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.Success, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
        Assert.False(model.LastMessage!.CountsAsAFailedAttempt);
    }
}

public class UnlockModelDeviceUnlockTests : VaultHarness
{
    [Fact]
    public async Task No_vault_offers_no_device_unlock()
    {
        UnlockModel model = new(NewVault());

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
        Assert.False(model.DeviceUnlockOffered);
    }

    [Fact]
    public async Task A_locked_vault_with_no_device_wrap_offers_none()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.False(model.DeviceUnlockOffered);
    }

    [Fact]
    public async Task A_locked_vault_with_a_device_wrap_offers_it_without_opening()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        await model.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));
        await model.LockAsync();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        // The refresh answered "is this possible" by actually trying it, and put the vault back
        // exactly as it found it: looking at the screen again must never be what lets someone in.
        Assert.Equal(UnlockStage.Locked, model.Stage);
        Assert.True(model.DeviceUnlockOffered);
        Assert.False(vault.IsUnlocked);
        Assert.Null(model.LastMessage);

        VaultUnlockResult result = await model.UnlockWithDeviceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.Success, result);
        Assert.Equal(UnlockStage.Unlocked, model.Stage);
    }
}
