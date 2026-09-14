using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

/// <summary>
/// Pins the review's Major finding: an absent vault at a path somebody deliberately chose must
/// never read as an ordinary first run. Before the fix, <see cref="UnlockModel.RefreshAsync"/> set
/// <see cref="UnlockStage.NoVault"/> from <see cref="IKeyVault.ExistsAsync"/> alone, so a memory
/// stick unplugged or a synced folder not yet caught up looked exactly like a fresh install and
/// offered to create a vault — which would have created a second, different vault at the
/// remembered path, leaving the real keys at a location the application no longer points at.
/// </summary>
public class UnlockModelVaultUnreachableTests : VaultHarness
{
    [Fact]
    public async Task A_missing_vault_at_a_chosen_path_is_not_offered_as_a_first_run()
    {
        UnlockModel model = new(NewVault(), vaultPathIsDefault: false);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.VaultUnreachable, model.Stage);
        Assert.NotEqual(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task A_missing_vault_at_a_chosen_path_offers_no_device_unlock()
    {
        UnlockModel model = new(NewVault(), vaultPathIsDefault: false);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(model.DeviceUnlockOffered);
    }

    [Fact]
    public async Task An_ordinary_first_run_still_reads_as_one()
    {
        // No path was ever chosen: the default constructor argument (vaultPathIsDefault: true)
        // is what every pre-existing caller of this constructor already relied on.
        UnlockModel model = new(NewVault());

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task An_ordinary_first_run_still_reads_as_one_when_the_default_is_stated_explicitly()
    {
        UnlockModel model = new(NewVault(), vaultPathIsDefault: true);

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.NoVault, model.Stage);
    }

    [Fact]
    public async Task Once_the_chosen_vault_answers_the_stage_moves_past_unreachable()
    {
        KeyVault vault = NewVault();
        UnlockModel model = new(vault, vaultPathIsDefault: false);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UnlockStage.VaultUnreachable, model.Stage);

        // The medium reconnected, or the sync caught up: the same file, at the same path, now
        // answers. A retry must reach the ordinary Locked/NoVault split exactly as it would have on
        // a first refresh -- VaultUnreachable is not a trap a vault can never leave once entered.
        await model.CreateAsync(Password("passphrase"), TestContext.Current.CancellationToken);
        await model.LockAsync();

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UnlockStage.Locked, model.Stage);
    }
}
