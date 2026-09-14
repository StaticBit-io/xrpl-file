using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class VaultListModelTests : VaultHarness
{
    [Fact]
    public async Task Rows_carry_label_kind_network_and_address()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("mine", VaultEntryKind.Imported), TestContext.Current.CancellationToken);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        KeyRow row = Assert.Single(model.Rows);
        Assert.False(row.IsDamaged);
        Assert.Equal("mine", row.Info.Label);
        Assert.Equal(VaultEntryKind.Imported, row.Info.Kind);
        Assert.Equal("standalone", row.Info.Network);
        Assert.Equal("rAddressOfmine", row.Info.Address);
    }

    [Fact]
    public async Task A_generated_key_that_was_never_exported_is_flagged()
    {
        // The flag exists so the application can show which throwaway accounts exist nowhere else.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("throwaway", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        await vault.StoreAsync(Entry("mine", VaultEntryKind.Imported), TestContext.Current.CancellationToken);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(model.Rows.Single(r => r.Info.Kind == VaultEntryKind.Generated).NeedsExporting);
        Assert.False(model.Rows.Single(r => r.Info.Kind == VaultEntryKind.Imported).NeedsExporting);
    }

    [Fact]
    public async Task An_imported_key_never_needs_exporting_even_when_marked_exported()
    {
        // Imported means the account existed before this vault and will after it — its Exported
        // flag is not a warning whatever its value.
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(
            Entry("mine", VaultEntryKind.Imported), TestContext.Current.CancellationToken);
        await vault.UpdateAsync(
            reference, new VaultEntryEdit(Exported: true), TestContext.Current.CancellationToken);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(model.Rows).NeedsExporting);
    }

    [Fact]
    public async Task A_generated_key_that_was_exported_is_not_flagged()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(
            Entry("throwaway", VaultEntryKind.Generated), TestContext.Current.CancellationToken);
        await vault.UpdateAsync(
            reference, new VaultEntryEdit(Exported: true), TestContext.Current.CancellationToken);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(model.Rows).NeedsExporting);
    }

    [Fact]
    public async Task A_damaged_entry_is_flagged_and_the_others_are_not()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        string intact = await vault.StoreAsync(Entry("intact"), TestContext.Current.CancellationToken);
        string damaged = await vault.StoreAsync(Entry("damaged"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);
        VaultUnlockResult unlocked = await vault.UnlockAsync(Password("p"), TestContext.Current.CancellationToken);
        Assert.Equal(VaultUnlockResult.VaultDegraded, unlocked);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Rows.Count);
        KeyRow damagedRow = Assert.Single(model.Rows, r => r.IsDamaged);
        Assert.Equal(damaged, damagedRow.Info.Reference);
        KeyRow intactRow = Assert.Single(model.Rows, r => !r.IsDamaged);
        Assert.Equal(intact, intactRow.Info.Reference);
        Assert.Equal("intact", intactRow.Info.Label);
    }

    [Fact]
    public async Task A_deleted_entry_is_in_no_row_and_in_no_damaged_list()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        string kept = await vault.StoreAsync(Entry("kept"), TestContext.Current.CancellationToken);
        string gone = await vault.StoreAsync(Entry("gone"), TestContext.Current.CancellationToken);

        // DeleteAsync reseals the record, so this leaves no trace of damage behind — the vault
        // cannot report a casualty it has no record of, and its own References is how that
        // absence is noticed.
        await vault.DeleteAsync(gone, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(gone, vault.References);
        Assert.Empty(vault.DamagedReferences);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(model.Rows, r => r.Info.Reference == gone);
        Assert.Contains(model.Rows, r => r.Info.Reference == kept);
        Assert.False(model.IsDegraded);
    }

    [Fact]
    public async Task Writes_are_refused_while_degraded_and_allowed_after_acknowledging()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        string reference = await vault.StoreAsync(Entry("one"), TestContext.Current.CancellationToken);
        await vault.LockAsync();
        FlipOneByteOfEntry(reference);
        await vault.UnlockAsync(Password("p"), TestContext.Current.CancellationToken);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsDegraded);
        Assert.True(model.WritesAreRefused);

        await model.AcknowledgeDamageAsync(TestContext.Current.CancellationToken);

        Assert.False(model.WritesAreRefused);
        Assert.True(model.IsDegraded); // the entry is still unreadable, even once writing is allowed again

        // Proves WritesAreRefused was not a made-up flag: a write that would have thrown
        // VaultError.Degraded before AcknowledgeDamageAsync now goes through.
        await vault.StoreAsync(Entry("two"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Acknowledging_when_not_degraded_is_harmless()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);

        VaultListModel model = new(vault);
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(model.IsDegraded);
        Assert.False(model.WritesAreRefused);

        await model.AcknowledgeDamageAsync(TestContext.Current.CancellationToken);

        Assert.False(model.IsDegraded);
        Assert.False(model.WritesAreRefused);
    }
}
