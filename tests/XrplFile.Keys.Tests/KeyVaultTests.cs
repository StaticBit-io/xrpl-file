using System.Security.Cryptography;

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
        // What this pins is the outcome, not the mechanism: the recorded cost is an input to the
        // derivation, so lowering it changes the key as well and the unwrap would fail on that
        // alone. The associated-data binding that also covers it is pinned separately, by
        // A_header_field_the_derivation_never_sees_is_still_bound_to_the_wrap.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.KdfParams = record.KdfParams with { Iterations = 2 });

        Assert.Equal(
            VaultUnlockResult.WrongPassword,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_kdf_cost_edited_below_what_argon2id_accepts_reports_the_record_corrupt()
    {
        // Zero memory is not a cost — it is a value no build ever wrote. Handed straight to the
        // KDF it comes back as a raw ArgumentOutOfRangeException, out of a method whose whole
        // contract is an enumeration the caller switches on.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.KdfParams = record.KdfParams with { MemoryKib = 0 });

        Assert.Equal(
            VaultUnlockResult.VaultCorrupt,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_kdf_cost_edited_beyond_what_any_machine_could_pay_reports_the_record_corrupt()
    {
        // Two terabytes of Argon2id memory. Unchecked, every unlock attempt on this record pays
        // for the derivation before the header binding is ever consulted — and it does not
        // survive paying for it.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.KdfParams = record.KdfParams with { MemoryKib = 2_000_000_000 });

        Assert.Equal(
            VaultUnlockResult.VaultCorrupt,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_header_field_the_derivation_never_sees_is_still_bound_to_the_wrap()
    {
        // The record's header is the associated data of the key wrap, so an attacker who edits the
        // header of a stolen file gets a wrap that no longer opens rather than a cheaper attack.
        // Most header fields cannot show that on their own: the salt and the three cost figures
        // are also inputs to the derivation, so editing one of them changes the derived key too
        // and the unwrap would fail either way. `kdfId` is the one field that is associated data
        // and nothing else — it never reaches Derive — and the only reason it is normally out of
        // reach is that a record naming a KDF this build does not have is refused before the
        // unwrap is attempted. A KDF that derives exactly as Argon2id does but answers to another
        // identifier removes that refusal and leaves the binding, and nothing else, deciding.
        const int AnotherIdentifier = 7;

        KeyVault creator = NewVault();
        await creator.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await creator.LockAsync();

        VaultRecord written = ReadRecordFromFile();
        Assert.Equal(new Argon2idKdf().KdfId, written.KdfId);

        // The relabelling changes the label and not the key: the two KDFs derive the same bytes
        // from the salt and cost this record carries, so nothing below turns on the derivation.
        IPasswordKdf relabelled = new RelabelledKdf(AnotherIdentifier, new Argon2idKdf());
        Assert.Equal(
            new Argon2idKdf().Derive(Password("right").Span, written.Salt, written.KdfParams, 32),
            relabelled.Derive(Password("right").Span, written.Salt, written.KdfParams, 32));

        // Only the header changes. Salt, cost and wrapped bytes are untouched.
        MutateRecordInFile(record => record.KdfId = AnotherIdentifier);

        KeyVault opener = NewVault(relabelled);

        Assert.Equal(
            VaultUnlockResult.WrongPassword,
            await opener.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.False(opener.IsUnlocked);
    }

    /// <summary>
    /// Argon2id under another identifier. It exists so that a test can edit the one header field
    /// that is associated data and nothing else, and still reach the unwrap.
    /// </summary>
    private sealed class RelabelledKdf(int kdfId, IPasswordKdf inner) : IPasswordKdf
    {
        public int KdfId => kdfId;

        public byte[] Derive(
            ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int outputLength) =>
            inner.Derive(password, salt, parameters, outputLength);
    }

    [Fact]
    public async Task A_store_whose_write_fails_leaves_the_vault_saying_what_the_file_holds()
    {
        // The in-memory record is what References and every later reveal are answered from, and it
        // is mutated before the write rather than after it. A write that fails and is not rolled
        // back would therefore hand back a reference for an entry the file never received — the
        // key-less publication this vault exists to prevent.
        FailableWrites disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string kept = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        disk.Fail = true;
        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        Assert.Equal([kept], vault.References);
        Assert.Equal([kept], ReadRecordFromFile().Entries.Keys);

        // The rollback undid the attempt, not the vault: it still works once the disk does.
        disk.Fail = false;
        string second = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        string[] both = [kept, second];
        Assert.Equal(both.Order(StringComparer.Ordinal), vault.References.Order(StringComparer.Ordinal));
        Assert.Equal(
            both.Order(StringComparer.Ordinal),
            ReadRecordFromFile().Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(kept, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_delete_whose_write_fails_leaves_the_entry_the_file_still_holds()
    {
        // The mirror of the store case. A vault that forgot an entry the file still carries would
        // report a usable key as gone, and nothing would ever put it back.
        FailableWrites disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        disk.Fail = true;
        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.DeleteAsync(reference, TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.StorageFailed, error.Error);

        Assert.Equal([reference], vault.References);
        Assert.Equal([reference], ReadRecordFromFile().Entries.Keys);
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);

        disk.Fail = false;
        await vault.DeleteAsync(reference, TestContext.Current.CancellationToken);
        Assert.Empty(vault.References);
        Assert.Empty(ReadRecordFromFile().Entries);
    }

    [Fact]
    public async Task A_create_whose_write_fails_does_not_leave_the_data_key_in_memory()
    {
        // A vault that never opened is the one case where nothing later wipes the key it minted:
        // the handover to the vault's own field never happened, so the failure path is the only
        // place those thirty-two bytes can be wiped.
        const int DataKeyLength = 32;
        byte[]? dataKey = null;

        FailableWrites disk = new() { Fail = true };
        KeyVault vault = NewVault(
            disk.WriteAsync,
            length =>
            {
                byte[] bytes = RandomNumberGenerator.GetBytes(length);
                if (length == DataKeyLength)
                {
                    dataKey = bytes;
                }

                return bytes;
            });

        await Assert.ThrowsAsync<VaultException>(
            () => vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken));

        Assert.NotNull(dataKey);
        Assert.All(dataKey, b => Assert.Equal(0, b));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task References_cannot_be_written_through_by_whoever_reads_it()
    {
        // Handing out the live array lets a caller cast it back and rewrite the vault's own idea
        // of what it holds — silently, and without the file agreeing.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        IReadOnlyCollection<string> references = vault.References;

        Assert.IsNotType<string[]>(references);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)references)[0] = "tampered");
        Assert.Equal([reference], vault.References);
    }

    [Fact]
    public async Task An_operation_arriving_after_disposal_is_refused_rather_than_faulted()
    {
        // Disposal is a lock that also ends the vault, so what a late caller meets is a locked
        // vault — not an ObjectDisposedException out of the gate it happened to race.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        await vault.DisposeAsync();

        Assert.False(vault.IsUnlocked);
        await AssertLocked(() => vault.ListAsync(TestContext.Current.CancellationToken));
        await AssertLocked(() => vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken));
        await AssertLocked(() => vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken));

        // And disposing twice is still a no-op.
        await vault.DisposeAsync();
    }

    [Fact]
    public async Task A_disposed_vault_does_not_open_again_for_a_password()
    {
        // Disposal is the end of the vault, not a lock it can be talked out of. Sign-out disposes
        // the vault and the idle lock together; a lingering caller that could still unlock would
        // hold a resident data key with no timer left to take it away, and a further disposal
        // could not reach it either.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);

        await vault.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);

        // A second disposal is still a no-op, and still leaves nothing resident.
        await vault.DisposeAsync();
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_disposed_vault_does_not_open_again_for_the_device_secret()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.True(await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken));

        await vault.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_disposed_vault_does_not_create_one_either()
    {
        // The same instance, pointed at a path with nothing on it: creating would leave the
        // disposed vault holding a fresh data key, which is the same failure by another door.
        KeyVault vault = NewVault();

        await vault.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.False(vault.IsUnlocked);
        Assert.False(File.Exists(VaultPath));
    }

    private static async Task AssertLocked(Func<Task> operation)
    {
        VaultException error = await Assert.ThrowsAsync<VaultException>(operation);
        Assert.Equal(VaultError.Locked, error.Error);
    }
}
