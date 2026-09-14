using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// Damage and absence are different words on a screen (design 9). These tests pin the taxonomy:
/// a byte flipped inside one entry names that entry and leaves its neighbours open; an entry
/// removed from the file shows up nowhere at all, because the vault cannot report a casualty it
/// has no record of; a record whose own integrity code disagrees with its bytes still opens, with
/// the disagreement reported rather than swallowed; and a wrong password over an already-damaged
/// record is never allowed to read as "your password is not the problem".
/// </summary>
public class VaultDamageTests : VaultTestHarness
{
    [Fact]
    public async Task A_damaged_entry_is_named_and_the_others_still_open()
    {
        // "The vault is broken" is the wrong sentence when one wallet out of five is.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string damaged = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string intact = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);

        VaultUnlockResult result = await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Equal(new[] { damaged }, vault.DamagedReferences);
        Assert.Equal("two", (await vault.RevealAsync(intact, "test", TestContext.Current.CancellationToken)).Info.Label);
    }

    [Fact]
    public async Task Every_damaged_entry_among_several_is_named()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string first = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string second = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        string third = await vault.StoreAsync(Entry("three"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(first);
        FlipOneByteOfEntry(third);

        VaultUnlockResult result = await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Equal(new HashSet<string> { first, third }, new HashSet<string>(vault.DamagedReferences));
        Assert.DoesNotContain(second, vault.DamagedReferences);
        Assert.Equal("sSecretOftwo", (await vault.RevealAsync(second, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_deleted_entry_appears_in_no_list_of_casualties()
    {
        // The vault cannot report a casualty it has no record of. The caller compares its own
        // stored reference against References to say "this publication's key is gone".
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string removed = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string kept = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);

        // Deleted through the vault's own API, so the record's integrity code is recomputed over
        // what is left — a clean removal, not damage. LockAsync and UnlockAsync round-trip the
        // record through disk so this is checked against the file, not against the live cache.
        await vault.DeleteAsync(removed, TestContext.Current.CancellationToken);
        await vault.LockAsync();

        VaultUnlockResult result = await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.Success, result);
        Assert.Empty(vault.DamagedReferences);
        Assert.DoesNotContain(removed, vault.References);
        Assert.Contains(kept, vault.References);
    }

    [Fact]
    public async Task A_record_whose_mac_does_not_match_opens_degraded_rather_than_not_at_all()
    {
        // Every entry stays byte-for-byte intact; only the record's own integrity code disagrees
        // with its current bytes, as a stale or hand-edited mac would. Nothing here fails its own
        // per-entry tag, so the sweep alone would say nothing is wrong — the whole-record check is
        // what catches this, and it must not refuse to open over it.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Mac[0] ^= 0x01);

        VaultUnlockResult result = await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Empty(vault.DamagedReferences);
        Assert.Equal(
            "sSecretOfone", (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task A_wrong_password_over_a_damaged_record_is_reported_as_corrupt_not_as_a_wrong_password()
    {
        // The two are indistinguishable, and calling it a wrong password would tell the user
        // something false at the one moment it matters most. The wrap's shape is checked before
        // any password is derived against it (design 9), so this holds for every password offered,
        // right or wrong — a wrap this shape opens for none of them.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Dek = record.Dek[..20]);

        VaultUnlockResult result = await vault.UnlockAsync(Password("wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultCorrupt, result);
    }

    [Fact]
    public async Task Locking_a_degraded_vault_clears_the_list_of_casualties()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string damaged = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);
        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(vault.DamagedReferences);

        await vault.LockAsync();

        Assert.Empty(vault.DamagedReferences);
    }

    [Fact]
    public async Task A_wrong_password_on_an_open_degraded_vault_leaves_its_casualty_list_untouched()
    {
        // WrongPassword returns before the sweep ever runs: nothing has been touched, so the
        // damage already known from the unlock that got the vault open must survive intact.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string damaged = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);
        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);
        string[] before = [.. vault.DamagedReferences];

        VaultUnlockResult result = await vault.UnlockAsync(Password("wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.WrongPassword, result);
        Assert.True(vault.IsUnlocked);
        Assert.Equal(before, vault.DamagedReferences);
    }

    [Fact]
    public async Task Unlocking_with_a_device_secret_runs_the_same_sweep_as_a_password()
    {
        // The taxonomy does not have a separate outcome for device unlock; whichever secret
        // produces the data key, the same sweep and the same mac check decide what is reported.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string damaged = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        string intact = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
        await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);

        VaultUnlockResult result = await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Equal(new[] { damaged }, vault.DamagedReferences);
        Assert.Equal("two", (await vault.RevealAsync(intact, "test", TestContext.Current.CancellationToken)).Info.Label);
    }

    [Fact]
    public async Task Damaged_references_cannot_be_mutated_by_casting()
    {
        // DamagedReferences must be wrapped read-only, so a caller cannot cast the result back
        // to string[] and rewrite it. This test pins that invariant.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string damaged = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);
        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);

        IReadOnlyList<string> damageList = vault.DamagedReferences;
        Assert.Single(damageList);
        Assert.Equal(damaged, damageList[0]);

        // Attempt to mutate through casting should fail if wrapping is in place
        try
        {
            string[] leaked = (string[])damageList;
            leaked[0] = "forged";
            // If we reach here without an exception, wrapping is missing
            Assert.Fail("DamagedReferences did not prevent mutation through casting.");
        }
        catch (InvalidCastException)
        {
            // Expected: ReadOnlyCollection<string> cannot be cast to string[]
        }

        // Verify the property still returns the correct value
        Assert.Equal(damaged, vault.DamagedReferences[0]);
    }

    [Fact]
    public async Task A_write_does_not_quietly_recompute_the_integrity_code_over_a_damaged_record()
    {
        // The mac is the only witness there is to an entry removed from the file or to a record
        // whose bytes were edited under it: nothing else in the design can see either (11). A
        // write that reseals over the damage answers the next unlock with Success, and the
        // evidence is gone for good.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Mac[0] ^= 0x01);
        Assert.Equal(
            VaultUnlockResult.VaultDegraded,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Degraded, error.Error);

        // The refusal is worth having only because of what it preserves: the next unlock still
        // finds the disagreement, rather than a record freshly resealed over it.
        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.VaultDegraded,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acknowledging_the_damage_is_what_lets_a_degraded_vault_be_written_again()
    {
        // Erasing the only evidence there is has to be a decision somebody takes, but it does
        // have to be takeable: a vault that could never be written again after one bad byte would
        // be a vault the user cannot repair, delete an entry from, or add a key to.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Mac[0] ^= 0x01);
        Assert.Equal(
            VaultUnlockResult.VaultDegraded,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));

        await vault.AcknowledgeDamageAsync(TestContext.Current.CancellationToken);
        string second = await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);

        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.Success,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOftwo",
            (await vault.RevealAsync(second, "test", TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task An_acknowledgement_lasts_as_long_as_the_session_that_gave_it()
    {
        // Nothing is written when the damage is acknowledged, so the file still carries the
        // disagreement — and a vault opened again is a user who has not been told yet.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Mac[0] ^= 0x01);
        await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.AcknowledgeDamageAsync(TestContext.Current.CancellationToken);

        await vault.LockAsync();
        Assert.Equal(
            VaultUnlockResult.VaultDegraded,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Degraded, error.Error);
    }

    [Fact]
    public async Task Acknowledging_damage_on_a_locked_vault_refuses()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.AcknowledgeDamageAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultError.Locked, error.Error);
    }

    [Fact]
    public async Task A_degraded_vault_still_reads_everything_that_is_intact()
    {
        // The point of VaultDegraded is that the user is in. Whatever the record's integrity code
        // says, listing, revealing and exporting the entries that still open must ask nothing of
        // the caller first — a degraded vault is how the keys get rescued.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Mac[0] ^= 0x01);
        Assert.Equal(
            VaultUnlockResult.VaultDegraded,
            await vault.UnlockAsync(Password("right"), TestContext.Current.CancellationToken));

        Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "sSecretOfone",
            (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);

        byte[] container = await vault.ExportAsync(
            reference, Password("export password"), TestContext.Current.CancellationToken);
        Assert.True(BackupContainer.TryUnpack(
            container, "export password", fromFile: true, out BackupPayload? payload, out BackupUnpackError error));
        Assert.Equal(BackupUnpackError.None, error);
        Assert.Equal("sSecretOfone", payload!.Wallets.Single().KeyValue);
    }

    [Fact]
    public async Task A_device_unlock_with_mac_mismatch_but_no_entry_damage_opens_degraded()
    {
        // Every entry stays byte-for-byte intact; only the record's own integrity code disagrees
        // with its current bytes. The device-unlock path should run the same whole-record check
        // as the password path, and this test ensures it does.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("right"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.EnableDeviceUnlockAsync(TestContext.Current.CancellationToken);
        await vault.LockAsync();

        MutateRecordInFile(record => record.Mac[0] ^= 0x01);

        VaultUnlockResult result = await vault.UnlockWithDeviceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockResult.VaultDegraded, result);
        Assert.Empty(vault.DamagedReferences);
        Assert.Equal(
            "sSecretOfone", (await vault.RevealAsync(reference, "test", TestContext.Current.CancellationToken)).Secret);
    }
}
