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

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.Equal(before, EntryCiphertextFromFile(reference));
    }

    [Fact]
    public async Task The_old_password_stops_working_and_the_new_one_starts()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
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
    public async Task A_wrong_old_password_is_reported_as_such_and_changes_nothing()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        VaultRecord before = ReadRecordFromFile();

        Assert.Equal(
            VaultPasswordChange.WrongPassword,
            await vault.ChangePasswordAsync(Password("wrong"), Password("new"), TestContext.Current.CancellationToken));

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

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

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

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

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

    [Fact]
    public async Task A_changed_password_leaves_no_copy_beside_the_vault_that_the_old_one_still_opens()
    {
        // The reason a password gets changed is that the old one leaked. A durable write keeps the
        // previous contents as a sibling, and that sibling is a whole vault the leaked password
        // opens — with no attacker needed to put it there.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);

        // A second write, so the swap has something to rename out of the way.
        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(VaultBackupPath));

        Assert.Equal(
            VaultPasswordChange.Changed,
            await vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));

        Assert.False(File.Exists(VaultBackupPath));

        // And the copy is gone rather than merely unread: losing the main file must not silently
        // revert the vault to the password that leaked.
        await vault.LockAsync();
        File.Delete(VaultPath);

        KeyVault after = NewVault();
        Assert.Equal(
            VaultUnlockResult.VaultMissing,
            await after.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_ordinary_write_still_keeps_its_backup()
    {
        // The backup is why an interrupted write is survivable. Only a password change may clear
        // it, and only once the new record is on disk.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);

        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(VaultBackupPath));
    }

    [Fact]
    public async Task A_copy_that_cannot_be_removed_is_reported_rather_than_passed_over()
    {
        // The removal is the security half of the change, so a failure to carry it out is a
        // failure of the change and says so. What it must not do is leave this instance's cached
        // record behind the file: the write has landed, and a later store reseals from that cache.
        static Task PlainWrite(string path, byte[] contents, CancellationToken cancellationToken) =>
            File.WriteAllBytesAsync(path, contents, cancellationToken);

        KeyVault vault = NewVault(PlainWrite);
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);

        // A directory where the kept copy would be: a path File.Delete refuses, on every platform.
        Directory.CreateDirectory(VaultBackupPath);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        Directory.Delete(VaultBackupPath);

        // The new password is what is on disk, and it stays that way through the next write.
        string reference = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOftwo",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_key_wrap_that_could_not_hold_a_data_key_throws_instead_of_reporting_a_wrong_password()
    {
        // A dek too short to be a wrap at all fails the unwrap on a length check, before a single
        // byte of cryptography runs. Reported as false, that is a damaged file charged to the
        // user's failed-attempt counter.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Dek = []);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public async Task A_truncated_key_wrap_throws_instead_of_reporting_a_wrong_password()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Dek = record.Dek[..20]);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public async Task Unlocking_over_a_key_wrap_that_could_not_hold_a_data_key_reports_the_record_corrupt()
    {
        // Same damage, same reasoning: only WrongPassword may advance a caller's failed-attempt
        // counter, and this record would not open for any password at all.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Dek = record.Dek[..20]);

        Assert.Equal(
            VaultUnlockResult.VaultCorrupt,
            await vault.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Changing_the_password_of_a_record_this_build_cannot_open_throws()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.V += 1);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Unsupported, error.Error);
    }

    [Fact]
    public async Task A_failed_write_during_a_password_change_leaves_the_old_password_working()
    {
        // The other half of "every other failure throws": a refused disk must not come back as a
        // mistyped password either, and must leave the vault exactly as it was.
        FailableWrites disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        VaultRecord before = ReadRecordFromFile();

        disk.Fail = true;
        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        VaultRecord after = ReadRecordFromFile();
        Assert.Equal(before.Salt, after.Salt);
        Assert.Equal(before.Dek, after.Dek);
        Assert.Equal(before.Mac, after.Mac);

        disk.Fail = false;
        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_device_store_that_cannot_answer_aborts_the_change_rather_than_dropping_device_unlock()
    {
        // A keychain locked for a moment is transient — the same event UnlockWithDeviceAsync calls
        // VaultUnavailable. Read as "there is no secret", it would delete a setting the user
        // enabled and never say so.
        KeyVault creator = NewVault();
        await creator.CreateAsync(Password("old"), TestContext.Current.CancellationToken);
        await creator.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        await creator.LockAsync();
        VaultRecord before = ReadRecordFromFile();

        KeyVault vault = NewVault(new FailingDeviceSecretStore());

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ChangePasswordAsync(Password("old"), Password("new"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        // Nothing was written, so the old password and the device wrap both still stand.
        VaultRecord after = ReadRecordFromFile();
        Assert.Equal(before.Salt, after.Salt);
        Assert.Equal(before.Dek, after.Dek);
        Assert.Equal(before.DeviceDek, after.DeviceDek);
        Assert.Equal(
            VaultUnlockResult.Success,
            await creator.UnlockAsync(Password("old"), TestContext.Current.CancellationToken));
    }
}
