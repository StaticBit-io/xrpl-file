using System.Buffers.Binary;

using Xrpl.AddressCodec;
using Xrpl.Wallet;

using Xunit;

using XrplFile.Keys;

namespace XrplFile.App.Core.Tests;

public class AddKeyModelTests : VaultHarness
{
    private static BackupPayload PayloadOf(params BackupWalletEntry[] wallets) => new() { Wallets = wallets };

    // Every "declared" field on a BackupWalletEntry (ClassicAddress, Algorithm) is only ever a
    // claim; the model re-derives from KeyValue instead of trusting it, so a helper that builds an
    // entry whose declared address does not match its own key would go undetected by the very
    // thing being tested. Building the wallet once and reusing both its seed and its real address
    // avoids that trap.
    private static BackupWalletEntry EntryFor(XrplWallet wallet, string label, string? network = null, string? kind = null) =>
        new(
            KeyValue: wallet.Seed,
            MnemonicPassphrase: null,
            ClassicAddress: wallet.ClassicAddress,
            KeyType: 0,
            Algorithm: XrplCodec.DecodeSeed(wallet.Seed).Type,
            DerivationPath: null,
            MasterAddress: null,
            Description: label,
            Network: network,
            Kind: kind);

    [Fact]
    public async Task A_generated_key_previews_an_address_and_stores_as_generated()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.True(model.Generate("mine", "testnet"));

        Assert.NotNull(model.Preview);
        Assert.Equal(XrplWallet.Ed25519, model.Preview!.Algorithm);
        string previewedAddress = model.Preview.Address;
        Assert.Null(model.Problem);

        string reference = await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(string.Empty, reference);
        Assert.Null(model.Preview);
        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultEntryKind.Generated, info.Kind);
        Assert.Equal(previewedAddress, info.Address);
    }

    [Fact]
    public async Task An_imported_ed25519_seed_previews_the_address_that_seed_derives()
    {
        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Ed25519);
        AddKeyModel model = new(NewVault());

        Assert.True(model.Import(new AddKeyDraft("mine", "testnet", wallet.Seed, XrplWallet.Ed25519)));

        Assert.Equal(wallet.ClassicAddress, model.Preview!.Address);
        Assert.Equal(XrplWallet.Ed25519, model.Preview.Algorithm);
    }

    [Fact]
    public void A_secp256k1_seed_previews_the_address_it_actually_has()
    {
        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Secp256k1);
        AddKeyModel model = new(NewVault());

        Assert.True(model.Import(new AddKeyDraft("mine", "testnet", wallet.Seed, XrplWallet.Secp256k1)));

        Assert.Equal(wallet.ClassicAddress, model.Preview!.Address);
        Assert.Equal(XrplWallet.Secp256k1, model.Preview.Algorithm);
    }

    // Replaces the brief's "the same seed previews two different addresses on two curves": a seed
    // does not derive two ways at all -- an XRPL family seed encodes its own curve in its prefix,
    // and the SDK's derivation reads it from there, ignoring whatever algorithm is passed. What
    // actually needs proving is that the model does not just echo back a possibly-wrong claim: a
    // secp256k1 seed still previews as secp256k1 even when the caller says ed25519.
    [Fact]
    public void The_recorded_algorithm_comes_from_the_seed_not_from_what_was_typed()
    {
        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Secp256k1);
        AddKeyModel model = new(NewVault());

        Assert.True(model.Import(new AddKeyDraft("mine", "testnet", wallet.Seed, XrplWallet.Ed25519)));

        Assert.Equal(wallet.ClassicAddress, model.Preview!.Address);
        Assert.Equal(XrplWallet.Secp256k1, model.Preview.Algorithm);
    }

    [Fact]
    public void A_malformed_seed_sets_problem_and_prepares_nothing()
    {
        AddKeyModel model = new(NewVault());

        Assert.False(model.Import(new AddKeyDraft("mine", "testnet", "not a seed", null)));

        Assert.Null(model.Preview);
        Assert.NotNull(model.Problem);
    }

    [Theory]
    [InlineData("", "testnet")]
    [InlineData("   ", "testnet")]
    [InlineData("mine", "")]
    [InlineData("mine", "   ")]
    public void An_empty_label_or_network_sets_problem_and_prepares_nothing(string label, string network)
    {
        AddKeyModel model = new(NewVault());

        Assert.False(model.Generate(label, network));

        Assert.Null(model.Preview);
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public async Task Storing_an_address_the_vault_already_holds_surfaces_duplicate_address_as_a_problem()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        XrplWallet wallet = XrplWallet.Generate();
        await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "already here", "testnet", wallet.ClassicAddress, wallet.Seed),
            TestContext.Current.CancellationToken);

        AddKeyModel model = new(vault);
        Assert.True(model.Import(new AddKeyDraft("again", "testnet", wallet.Seed, XrplWallet.Ed25519)));

        string reference = await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, reference);
        Assert.NotNull(model.Problem);
        Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_locked_vault_surfaces_locked_as_a_problem()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        await vault.LockAsync();

        AddKeyModel model = new(vault);
        Assert.True(model.Generate("mine", "testnet"));

        string reference = await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, reference);
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public async Task StoreAsync_with_nothing_prepared_throws()
    {
        AddKeyModel model = new(NewVault());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.StoreAsync(TestContext.Current.CancellationToken));
    }

    // ---- the container route -------------------------------------------------------------------

    [Fact]
    public void Opening_a_container_with_the_wrong_password_reports_it_distinctly()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "right", iterations: 1000);
        AddKeyModel model = new(NewVault());

        BackupUnpackError outcome = model.OpenContainer(container, "wrong");

        Assert.Equal(BackupUnpackError.WrongPassword, outcome);
        Assert.NotNull(model.Problem);
        Assert.Empty(model.ContainerKeys);
    }

    [Fact]
    public void Opening_something_that_is_not_a_container_reports_it_distinctly()
    {
        AddKeyModel model = new(NewVault());

        BackupUnpackError outcome = model.OpenContainer("not a container at all"u8.ToArray(), "any");

        Assert.Equal(BackupUnpackError.NotOurContainer, outcome);
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public void Opening_a_password_less_container_from_a_file_is_refused_distinctly()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), password: null, iterations: 0);
        AddKeyModel model = new(NewVault());

        BackupUnpackError outcome = model.OpenContainer(container, null);

        Assert.Equal(BackupUnpackError.PasswordlessContainerNotAllowed, outcome);
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public void An_iteration_count_out_of_range_is_reported_distinctly()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "right", iterations: 1000);
        BinaryPrimitives.WriteInt32BigEndian(container.AsSpan(6, 4), 50_000_000);
        AddKeyModel model = new(NewVault());

        BackupUnpackError outcome = model.OpenContainer(container, "right");

        Assert.Equal(BackupUnpackError.IterationsOutOfRange, outcome);
        Assert.NotNull(model.Problem);
    }

    [Fact]
    public void A_container_holding_several_keys_lists_all_of_them()
    {
        XrplWallet first = XrplWallet.Generate(XrplWallet.Ed25519);
        XrplWallet second = XrplWallet.Generate(XrplWallet.Secp256k1);
        byte[] container = BackupContainer.Pack(
            PayloadOf(
                EntryFor(first, "first", network: "mainnet"),
                EntryFor(second, "second")),
            "p", iterations: 1000);
        AddKeyModel model = new(NewVault());

        BackupUnpackError outcome = model.OpenContainer(container, "p");

        Assert.Equal(BackupUnpackError.None, outcome);
        Assert.Null(model.Problem);
        Assert.Equal(2, model.ContainerKeys.Count);
        Assert.Equal("first", model.ContainerKeys[0].Label);
        Assert.Equal("mainnet", model.ContainerKeys[0].Network);
        Assert.Equal("second", model.ContainerKeys[1].Label);
        Assert.Null(model.ContainerKeys[1].Network);
    }

    [Fact]
    public async Task Selecting_a_container_key_previews_its_derived_address_and_stores_it()
    {
        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Secp256k1);
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(wallet, "from the wallet app")), "p", iterations: 1000);

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("vault password"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);
        model.OpenContainer(container, "p");

        Assert.True(model.SelectContainerKey(0, "renamed on the way in", "testnet"));

        Assert.Equal(wallet.ClassicAddress, model.Preview!.Address);
        Assert.Equal(XrplWallet.Secp256k1, model.Preview.Algorithm);

        string reference = await model.StoreAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reference, info.Reference);
        Assert.Equal("renamed on the way in", info.Label);
        Assert.Equal("testnet", info.Network);
        Assert.Equal(wallet.ClassicAddress, info.Address);
        Assert.Equal(VaultEntryKind.Imported, info.Kind);
    }

    [Fact]
    public async Task Every_key_in_a_multi_key_container_can_be_imported_one_at_a_time()
    {
        XrplWallet first = XrplWallet.Generate(XrplWallet.Ed25519);
        XrplWallet second = XrplWallet.Generate(XrplWallet.Secp256k1);
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(first, "first"), EntryFor(second, "second")), "p", iterations: 1000);

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("vault password"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);
        model.OpenContainer(container, "p");

        Assert.True(model.SelectContainerKey(0, "first", "testnet"));
        string firstReference = await model.StoreAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(string.Empty, firstReference);

        // ContainerKeys survives the store, so the second key can still be reached and imported
        // from the same opened container.
        Assert.Equal(2, model.ContainerKeys.Count);
        Assert.True(model.SelectContainerKey(1, "second", "testnet"));
        string secondReference = await model.StoreAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(string.Empty, secondReference);

        Assert.Equal(2, (await vault.ListAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task A_container_key_the_vault_already_holds_on_that_network_is_refused_at_store_time()
    {
        XrplWallet wallet = XrplWallet.Generate();
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "already here", "testnet", wallet.ClassicAddress, wallet.Seed),
            TestContext.Current.CancellationToken);

        byte[] container = BackupContainer.Pack(PayloadOf(EntryFor(wallet, "same key")), "p", iterations: 1000);
        AddKeyModel model = new(vault);
        model.OpenContainer(container, "p");
        Assert.True(model.SelectContainerKey(0, "duplicate", "testnet"));

        string reference = await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, reference);
        Assert.NotNull(model.Problem);
        Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Selecting_a_malformed_key_out_of_a_container_sets_problem()
    {
        BackupWalletEntry malformed = new(
            KeyValue: "not a seed",
            MnemonicPassphrase: null,
            ClassicAddress: "rSomeAddress",
            KeyType: 0,
            Algorithm: XrplWallet.Ed25519,
            DerivationPath: null,
            MasterAddress: null,
            Description: "broken");
        byte[] container = BackupContainer.Pack(PayloadOf(malformed), "p", iterations: 1000);
        AddKeyModel model = new(NewVault());
        model.OpenContainer(container, "p");

        Assert.False(model.SelectContainerKey(0, "mine", "testnet"));

        Assert.Null(model.Preview);
        Assert.NotNull(model.Problem);
    }
}
