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
}
