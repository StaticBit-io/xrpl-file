using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class DeleteKeyModelTests : VaultHarness
{
    [Fact]
    public async Task Preparing_a_never_exported_entry_warns_it_is_never_exported_and_names_its_address()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(Entry("mine"), token);
        DeleteKeyModel model = new(vault);

        DeleteWarning warning = await model.PrepareAsync(reference, token);

        Assert.True(warning.NeverExported);
        Assert.Equal("rAddressOfmine", warning.Address);
    }

    [Fact]
    public async Task Preparing_an_exported_entry_does_not_warn_it_is_never_exported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(Entry("mine"), token);
        await vault.ExportAsync(reference, Password("container password"), token);
        DeleteKeyModel model = new(vault);

        DeleteWarning warning = await model.PrepareAsync(reference, token);

        Assert.False(warning.NeverExported);
        Assert.Equal("rAddressOfmine", warning.Address);
    }

    [Fact]
    public async Task Preparing_an_unknown_reference_throws_no_such_entry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        DeleteKeyModel model = new(vault);

        VaultException exception = await Assert.ThrowsAsync<VaultException>(
            () => model.PrepareAsync("no-such-reference", token));

        Assert.Equal(VaultError.NoSuchEntry, exception.Error);
    }

    [Fact]
    public async Task Preparing_a_damaged_entry_throws_entry_damaged_instead_of_warning_about_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string damaged = await vault.StoreAsync(Entry("damaged"), token);
        await vault.LockAsync();
        FlipOneByteOfEntry(damaged);
        Assert.Equal(VaultUnlockResult.VaultDegraded, await vault.UnlockAsync(Password("p"), token));
        DeleteKeyModel model = new(vault);

        VaultException exception = await Assert.ThrowsAsync<VaultException>(() => model.PrepareAsync(damaged, token));

        Assert.Equal(VaultError.EntryDamaged, exception.Error);
    }

    [Fact]
    public async Task Deleting_without_preparing_throws_and_deletes_nothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(Entry("mine"), token);
        DeleteKeyModel model = new(vault);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DeleteAsync(reference, token));

        Assert.Contains(reference, vault.References);
    }

    [Fact]
    public async Task Preparing_one_reference_does_not_authorize_deleting_a_different_one()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string first = await vault.StoreAsync(Entry("first"), token);
        string second = await vault.StoreAsync(Entry("second"), token);
        DeleteKeyModel model = new(vault);
        await model.PrepareAsync(first, token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DeleteAsync(second, token));

        Assert.Contains(second, vault.References);
    }

    [Fact]
    public async Task Deleting_after_preparing_removes_the_entry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(Entry("mine"), token);
        DeleteKeyModel model = new(vault);
        await model.PrepareAsync(reference, token);

        await model.DeleteAsync(reference, token);

        Assert.DoesNotContain(reference, vault.References);
    }

    [Fact]
    public async Task Deleting_twice_without_preparing_again_throws_the_second_time()
    {
        // A successful delete clears what was prepared, the same as AddKeyModel.StoreAsync clears
        // what it prepared only on success — so a second delete needs a second warning shown.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(Entry("mine"), token);
        DeleteKeyModel model = new(vault);
        await model.PrepareAsync(reference, token);
        await model.DeleteAsync(reference, token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DeleteAsync(reference, token));
    }

    [Fact]
    public async Task A_failed_delete_also_consumes_the_preparation_and_a_retry_needs_a_fresh_warning()
    {
        // Finding 2: a preparation must be good for one attempt only, whether that attempt
        // succeeds or fails. Prepare, then force the delete to fail (the vault locked underneath
        // it), then — without preparing again — try to delete the same reference a second time.
        // Under the old "clear on success only" rule, the failed first attempt would leave
        // _prepared still set, so this second attempt would go straight through to
        // IKeyVault.DeleteAsync and remove the entry with nobody having seen the warning for this
        // attempt. It must be refused instead.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(Entry("mine"), token);
        DeleteKeyModel model = new(vault);
        await model.PrepareAsync(reference, token);
        await vault.LockAsync();

        VaultException failure = await Assert.ThrowsAsync<VaultException>(() => model.DeleteAsync(reference, token));
        Assert.Equal(VaultError.Locked, failure.Error);

        Assert.Equal(VaultUnlockResult.Success, await vault.UnlockAsync(Password("p"), token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DeleteAsync(reference, token));
        Assert.Contains(reference, vault.References);

        await model.PrepareAsync(reference, token);
        await model.DeleteAsync(reference, token);
        Assert.DoesNotContain(reference, vault.References);
    }
}
