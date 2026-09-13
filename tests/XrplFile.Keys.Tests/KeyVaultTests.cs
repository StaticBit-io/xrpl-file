using Xunit;

namespace XrplFile.Keys.Tests;

public class KeyVaultTests : VaultTestHarness
{
    [Fact]
    public async Task A_vault_is_absent_until_it_is_created()
    {
        KeyVault vault = NewVault();

        Assert.False(await vault.ExistsAsync(TestContext.Current.CancellationToken));

        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.True(await vault.ExistsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Creating_leaves_the_vault_open_and_a_file_on_disk()
    {
        KeyVault vault = NewVault();

        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.True(vault.IsUnlocked);
        Assert.True(File.Exists(VaultPath));
        Assert.Empty(vault.References);
    }

    [Fact]
    public async Task Creating_over_a_vault_that_is_already_there_refuses()
    {
        KeyVault first = NewVault();
        await first.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        KeyVault second = NewVault();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.CreateAsync(Password("other"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_fresh_instance_opens_with_the_right_password()
    {
        KeyVault creator = NewVault();
        await creator.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await creator.LockAsync();

        KeyVault opener = NewVault();
        VaultUnlockResult result = await opener.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.Success, result);
        Assert.True(opener.IsUnlocked);
    }

    [Fact]
    public async Task A_fresh_instance_refuses_the_wrong_password_and_stays_shut()
    {
        KeyVault creator = NewVault();
        await creator.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        KeyVault opener = NewVault();
        VaultUnlockResult result = await opener.UnlockAsync(Password("wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.WrongPassword, result);
        Assert.False(opener.IsUnlocked);
    }

    [Fact]
    public async Task A_wrong_password_does_not_close_a_vault_that_is_already_open()
    {
        // Confirming a password and opening a vault are the same call. A mistyped confirmation must
        // not end the session it was only meant to re-check.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        VaultUnlockResult result = await vault.UnlockAsync(Password("wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.WrongPassword, result);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_wrong_password_leaves_the_keys_of_an_open_vault_reachable()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        await vault.UnlockAsync(Password("wrong"), TestContext.Current.CancellationToken);

        VaultEntry entry = await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken);
        Assert.Equal("sSecretOfone", entry.Secret);
    }

    [Fact]
    public async Task Unlocking_where_there_is_no_vault_reports_it_missing()
    {
        KeyVault vault = NewVault();

        VaultUnlockResult result = await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultMissing, result);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Storing_returns_a_reference_that_is_a_guid()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        Assert.True(Guid.TryParseExact(reference, "N", out _));
        Assert.Contains(reference, vault.References);
    }

    [Fact]
    public async Task Revealing_returns_the_secret_that_was_stored()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(
            Entry("one", VaultEntryKind.Imported, network: "testnet", address: "rOne", secret: "sOneSeed"),
            TestContext.Current.CancellationToken);
        await vault.LockAsync();
        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        VaultEntry entry = await vault.RevealAsync(reference, "signing a publication", TestContext.Current.CancellationToken);

        Assert.Equal("sOneSeed", entry.Secret);
        Assert.Equal(reference, entry.Info.Reference);
        Assert.Equal(VaultEntryKind.Imported, entry.Info.Kind);
        Assert.Equal("one", entry.Info.Label);
        Assert.Equal("testnet", entry.Info.Network);
        Assert.Equal("rOne", entry.Info.Address);
        Assert.False(entry.Info.Exported);
    }

    [Fact]
    public async Task Revealing_a_reference_the_vault_never_issued_says_so()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.RevealAsync(Guid.NewGuid().ToString("N"), "test", TestContext.Current.CancellationToken));

        Assert.Equal(VaultError.NoSuchEntry, error.Error);
    }

    [Fact]
    public async Task Listing_describes_every_entry_without_any_reveal()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, listed.Count);
        Assert.Equal(["one", "two"], listed.Select(info => info.Label).Order(StringComparer.Ordinal));
        Assert.All(listed, info => Assert.Equal("standalone", info.Network));
        Assert.All(listed, info => Assert.Null(info.LastKnownSequence));
    }

    [Fact]
    public async Task An_edit_changes_the_metadata_and_outlives_a_reload()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        await vault.UpdateAsync(
            reference,
            new VaultEntryEdit(Label: "renamed", LastKnownSequence: 42, DeletableFromLedger: 296, Exported: true),
            TestContext.Current.CancellationToken);
        await vault.LockAsync();
        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        VaultEntry entry = await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken);
        Assert.Equal("renamed", entry.Info.Label);
        Assert.Equal(42u, entry.Info.LastKnownSequence);
        Assert.Equal(296L, entry.Info.DeletableFromLedger);
        Assert.True(entry.Info.Exported);

        // An edit reaches the metadata and nothing else.
        Assert.Equal("sSecretOfone", entry.Secret);
        Assert.Equal("rAddressOfone", entry.Info.Address);
        Assert.Equal(VaultEntryKind.Generated, entry.Info.Kind);
    }

    [Fact]
    public async Task Deleting_an_entry_removes_it_from_the_file_as_well_as_from_memory()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string first = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string second = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);

        await vault.DeleteAsync(first, TestContext.Current.CancellationToken);

        Assert.Equal([second], vault.References);
        Assert.DoesNotContain(first, ReadRecordFromFile().Entries.Keys);
    }

    [Fact]
    public async Task Deleting_a_reference_that_is_not_there_succeeds_silently()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        await vault.DeleteAsync(Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        Assert.Empty(vault.References);
    }

    [Fact]
    public async Task Every_operation_on_a_locked_vault_refuses()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        Assert.False(vault.IsUnlocked);
        Assert.Empty(vault.References);

        await AssertLocked(() => vault.ListAsync(TestContext.Current.CancellationToken));
        await AssertLocked(() => vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken));
        await AssertLocked(() => vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken));
        await AssertLocked(() => vault.UpdateAsync(reference, new VaultEntryEdit(Label: "x"), TestContext.Current.CancellationToken));
        await AssertLocked(() => vault.DeleteAsync(reference, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_lock_arriving_mid_store_waits_for_the_store_to_land()
    {
        // The gate is taken before StoreAsync yields, so the lock below is queued behind a store
        // already in flight. The store must finish and its entry must be in the file: a lock that
        // cut it off would leave a reference returned for an entry that does not exist.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);

        Task<string> storing = vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        Task locking = vault.LockAsync();

        string reference = await storing;
        await locking;

        Assert.False(vault.IsUnlocked);
        Assert.Equal(VaultUnlockResult.Success, await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.Equal("sSecretOfone", (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_lock_waits_for_the_operation_in_flight_rather_than_cutting_it_off()
    {
        // Unlocking takes the gate before it hands its task back, so the lock below is queued
        // behind a derivation that is unmistakably still running. The unlock has to finish first.
        // A lock that could overtake it would be a lock landing in the middle of an operation —
        // which is how an entry ends up sealed under a half-wiped key.
        KeyVault vault = NewVault(SlowEnoughToRaceParameters);
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        Task<VaultUnlockResult> unlocking = vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);
        Task locking = vault.LockAsync();

        Assert.Same(unlocking, await Task.WhenAny(unlocking, locking));
        Assert.Equal(VaultUnlockResult.Success, await unlocking);

        await locking;
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task An_entry_moved_to_another_reference_fails_its_tag()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string first = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string second = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        byte[] wasAtSecond = EntryCiphertextFromFile(second);

        // Swap the two ciphertexts in the file. Each is authentic; neither belongs where it now sits.
        SwapEntriesInFile(first, second);

        // The bytes really did move, so what fails below is the binding rather than the entry.
        Assert.Equal(wasAtSecond, EntryCiphertextFromFile(first));

        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);
        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.RevealAsync(first, "test", TestContext.Current.CancellationToken));

        Assert.Equal(VaultError.EntryDamaged, error.Error);
    }

    [Fact]
    public async Task A_damaged_entry_is_refused_while_its_neighbours_still_open()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string first = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string second = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        FlipOneByteOfEntry(first);

        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.RevealAsync(first, "test", TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.EntryDamaged, error.Error);

        Assert.Equal("sSecretOftwo", (await vault.RevealAsync(second, "test", TestContext.Current.CancellationToken)).Secret);

        // What is in References and missing from the listing is what is damaged.
        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([second], listed.Select(info => info.Reference));
        Assert.Contains(first, vault.References);
    }

    [Fact]
    public async Task A_record_written_by_a_later_format_is_not_opened()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.V = 99);

        Assert.Equal(
            VaultUnlockResult.VaultUnsupported,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_record_wrapped_by_an_unknown_kdf_is_not_opened()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.KdfId = 99);

        Assert.Equal(
            VaultUnlockResult.VaultUnsupported,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_file_that_is_not_a_record_at_all_reports_corruption_rather_than_a_wrong_password()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        await File.WriteAllTextAsync(VaultPath, "not a vault", TestContext.Current.CancellationToken);

        Assert.Equal(
            VaultUnlockResult.VaultCorrupt,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_kdf_cost_edited_in_the_file_does_not_open_the_vault()
    {
        // The recorded cost both drives the derivation and is bound into the wrap as associated
        // data, so an attacker who lowers it in the file gets a wrap that no longer opens rather
        // than a cheaper attack — which is the whole of what stands behind a single password.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.KdfParams = record.KdfParams with { Iterations = 2 });

        Assert.Equal(
            VaultUnlockResult.WrongPassword,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    private static async Task AssertLocked(Func<Task> operation)
    {
        VaultException error = await Assert.ThrowsAsync<VaultException>(operation);
        Assert.Equal(VaultError.Locked, error.Error);
    }
}
