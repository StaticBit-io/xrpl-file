using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public sealed class NewAccountModelTests : VaultHarness
{
    private async Task<(NewAccountModel Model, KeyVault Vault)> OpenAsync(CancellationToken token)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("a master password"), token);
        return (new NewAccountModel(vault), vault);
    }

    [Fact]
    public async Task A_created_key_is_stored_as_generated_on_the_chosen_network()
    {
        (NewAccountModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "testnet";

        string? reference = await model.CreateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reference);
        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultEntryKind.Generated, info.Kind);
        Assert.Equal("for the photo", info.Label);
        Assert.Equal("testnet", info.Network);
        Assert.StartsWith("r", info.Address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_label_is_refused()
    {
        (NewAccountModel model, _) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "   ";
        model.Network = "testnet";

        Assert.False(model.CanCreate);
        Assert.Null(await model.CreateAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }

    [Fact]
    public async Task A_network_no_link_can_name_is_refused()
    {
        (NewAccountModel model, _) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "standalone";

        Assert.False(model.CanCreate);
        Assert.Null(await model.CreateAsync(TestContext.Current.CancellationToken));
        Assert.Contains("standalone", model.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_in_another_case_is_stored_canonically()
    {
        (NewAccountModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "TESTNET";

        await model.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("testnet", Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken)).Network);
    }

    [Fact]
    public async Task A_locked_vault_refuses_and_says_so()
    {
        (NewAccountModel model, KeyVault vault) = await OpenAsync(TestContext.Current.CancellationToken);
        model.Label = "for the photo";
        model.Network = "testnet";
        await vault.LockAsync();

        Assert.Null(await model.CreateAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(model.Message);
    }
}
