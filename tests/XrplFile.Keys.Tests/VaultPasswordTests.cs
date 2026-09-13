using Xunit;

namespace XrplFile.Keys.Tests;

public class VaultPasswordTests : VaultTestHarness
{
    [Fact]
    public async Task A_password_change_leaves_every_entry_byte_for_byte_alone()
    {
        // The point of wrapping a data key rather than encrypting under the password directly. A
        // change that rewrote entries would, when interrupted, destroy the ones it had not reached.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        byte[] before = EntryCiphertextFromFile(reference);

        Assert.True(await vault.ChangePasswordAsync(
            Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.Equal(before, EntryCiphertextFromFile(reference));
    }

    [Fact]
    public async Task The_old_password_stops_working_and_the_new_one_starts()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        Assert.True(await vault.ChangePasswordAsync(
            Password("old"), Password("new"), TestContext.Current.CancellationToken));
        await vault.LockAsync();

        Assert.Equal(
            VaultUnlockResult.WrongPassword,
            await vault.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));

        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_wrong_old_password_returns_false_and_changes_nothing()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        VaultRecord before = ReadRecordFromFile();

        Assert.False(await vault.ChangePasswordAsync(
            Password("wrong"), Password("new"), TestContext.Current.CancellationToken));

        VaultRecord after = ReadRecordFromFile();
        Assert.Equal(before.Salt, after.Salt);
        Assert.Equal(before.Dek, after.Dek);
        Assert.Equal(before.Mac, after.Mac);

        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_damaged_record_throws_instead_of_reporting_a_wrong_password()
    {
        // A disk fault reported as a wrong password is how a caller's failed-attempt counter
        // punishes the user for a hardware problem.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        await File.WriteAllTextAsync(VaultPath, "not a vault", TestContext.Current.CancellationToken);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public async Task Changing_the_password_of_a_vault_that_does_not_exist_refuses()
    {
        KeyVault vault = NewVault();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_password_change_works_on_a_locked_vault()
    {
        // The old and new password are both supplied, so nothing about the change needs the
        // cached data key of a session that happens to be open right now.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        Assert.True(await vault.ChangePasswordAsync(
            Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.False(vault.IsUnlocked);
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_password_change_on_an_open_vault_is_reflected_by_a_later_store_without_reverting_it()
    {
        // The cached record must be kept in sync with the new wrap. If a later persist re-derived
        // its bytes from a stale cache, it would silently resurrect the old password.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);

        Assert.True(await vault.ChangePasswordAsync(
            Password("old"), Password("new"), TestContext.Current.CancellationToken));

        string reference = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        Assert.Equal(
            VaultUnlockResult.WrongPassword,
            await vault.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOftwo",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }
}
