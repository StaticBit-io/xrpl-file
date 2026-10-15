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

    // The container route trusts BackupWalletEntry.Kind for the stored VaultEntryKind, falling back
    // to Imported when it is absent or unrecognised (ContainerEntryKind). Every other test in this
    // file builds its containers with kind: null, so only the fallback is actually exercised. This
    // is the round trip that matters: a Generated throwaway, exported through the vault (which is
    // what stamps Kind: "Generated" into the container -- see KeyVault.ToBackupPayload) and
    // reimported through this model, has to come back as Generated, because that is what keeps its
    // "this key exists nowhere else" warning alive across a backup and restore.
    [Fact]
    public async Task A_generated_key_exported_and_reimported_keeps_its_generated_kind()
    {
        KeyVault sourceVault = NewVault();
        await sourceVault.CreateAsync(Password("source password"), TestContext.Current.CancellationToken);
        AddKeyModel sourceModel = new(sourceVault);
        Assert.True(sourceModel.Generate("throwaway", "testnet"));
        string reference = await sourceModel.StoreAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(string.Empty, reference);

        byte[] container = await sourceVault.ExportAsync(
            reference, Password("container password"), TestContext.Current.CancellationToken);

        // A separate vault file: the exported address already lives in sourceVault, and a second
        // import of the same address on the same network into the same vault would be refused as a
        // duplicate rather than testing what this is here to test.
        KeyVault destinationVault = NewVault(Path.Combine(Path.GetDirectoryName(VaultPath)!, "reimport.json"));
        await destinationVault.CreateAsync(Password("destination password"), TestContext.Current.CancellationToken);
        AddKeyModel destinationModel = new(destinationVault);

        BackupUnpackError outcome = destinationModel.OpenContainer(container, "container password");

        Assert.Equal(BackupUnpackError.None, outcome);
        Assert.True(destinationModel.SelectContainerKey(0, "reimported", "testnet"));

        string reimportedReference = await destinationModel.StoreAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(string.Empty, reimportedReference);
        VaultEntryInfo info = Assert.Single(await destinationVault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultEntryKind.Generated, info.Kind);
    }

    // The other side of the same fallback: a container the Wallet application wrote never sets
    // Kind at all (BackupPayload's own remarks -- that field is this application's addition), and
    // that absence has to keep meaning Imported rather than throwing or defaulting to something
    // that would hide "an account they had before" behind a throwaway's warning.
    [Fact]
    public async Task A_container_with_no_kind_field_still_imports_as_imported()
    {
        XrplWallet wallet = XrplWallet.Generate();
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(wallet, "from a wallet-shaped backup", kind: null)), "p", iterations: 1000);

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);
        model.OpenContainer(container, "p");
        Assert.True(model.SelectContainerKey(0, "mine", "testnet"));

        await model.StoreAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultEntryKind.Imported, info.Kind);
    }

    // ---- what the screen must offer next --------------------------------------------------------

    [Fact]
    public async Task A_generated_key_is_reported_as_one_that_still_needs_exporting()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.True(model.Generate("fresh", "testnet"));
        await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.True(model.LastStoredNeedsExporting);
    }

    [Fact]
    public async Task An_imported_seed_is_not_reported_as_one_that_needs_exporting()
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.True(model.Import(new AddKeyDraft("theirs", "testnet", XrplWallet.Generate().Seed, null)));
        await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.False(model.LastStoredNeedsExporting);
    }

    // The defect this replaces: AddKey.razor decided whether to offer the export step from which
    // tab it was on, so a container this application wrote for a generated key, opened again
    // through "Open a backup file", produced a Generated entry that the key list flagged with the
    // red badge while the import route walked straight past the export step. The stored entry's
    // kind is the fact; the tab is not, and the answer here has to be the one the list gives.
    [Fact]
    public async Task A_generated_key_arriving_through_a_container_still_needs_exporting()
    {
        XrplWallet wallet = XrplWallet.Generate();
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(wallet, "born elsewhere", kind: nameof(VaultEntryKind.Generated))),
            "p",
            iterations: 1000);

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.Equal(BackupUnpackError.None, model.OpenContainer(container, "p"));
        Assert.True(model.SelectContainerKey(0, "back again", "testnet"));
        await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.True(model.LastStoredNeedsExporting);

        VaultListModel list = new(vault);
        await list.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(list.Rows.Single().NeedsExporting, model.LastStoredNeedsExporting);
    }

    // ---- a kind that is not one -----------------------------------------------------------------

    // Enum.TryParse accepts numeric text and hands back the number cast to the enum, so a container
    // carrying "kind": "7" stored (VaultEntryKind)7 -- which the key list rendered as the bare text
    // "7" and which matched no arm of any switch anywhere. A container's fields are untrusted
    // input; only a name this build actually declares is a kind.
    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("99999")]
    public async Task A_container_whose_kind_is_a_number_is_stored_as_Imported(string kind)
    {
        XrplWallet wallet = XrplWallet.Generate();
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(wallet, "numbered", kind: kind)), "p", iterations: 1000);

        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);
        Assert.Equal(BackupUnpackError.None, model.OpenContainer(container, "p"));
        Assert.True(model.SelectContainerKey(0, "mine", "testnet"));

        await model.StoreAsync(TestContext.Current.CancellationToken);

        VaultEntryInfo info = Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(VaultEntryKind.Imported, info.Kind);
        Assert.True(Enum.IsDefined(info.Kind));
    }

    // ---- closing a container --------------------------------------------------------------------

    // The whole point of CloseContainer: every seed OpenContainer decrypted, and the one
    // SelectContainerKey most recently prepared, stop being reachable through this model. A
    // selection attempted afterwards has to fail the same clean way an out-of-range one always did
    // -- Problem set, false returned -- rather than an ArgumentOutOfRangeException escaping because
    // the list it used to index into is now empty.
    [Fact]
    public void Closing_a_container_drops_its_keys_and_a_later_selection_fails_cleanly()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "p", iterations: 1000);
        AddKeyModel model = new(NewVault());
        model.OpenContainer(container, "p");
        Assert.Single(model.ContainerKeys);

        model.CloseContainer();

        Assert.Empty(model.ContainerKeys);
        Assert.False(model.SelectContainerKey(0, "mine", "testnet"));
        Assert.NotNull(model.Problem);
    }

    // A key already chosen out of the container -- Preview set, an entry pending -- must not survive
    // closing either: StoreAsync throwing InvalidOperationException afterwards is how the model says
    // "nothing is prepared", the same as before OpenContainer was ever called.
    [Fact]
    public async Task Closing_a_container_clears_a_key_selected_but_not_yet_stored()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "p", iterations: 1000);
        AddKeyModel model = new(NewVault());
        model.OpenContainer(container, "p");
        Assert.True(model.SelectContainerKey(0, "mine", "testnet"));
        Assert.NotNull(model.Preview);

        model.CloseContainer();

        Assert.Null(model.Preview);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.StoreAsync(TestContext.Current.CancellationToken));
    }

    // Closing is scoped to the container route: the model itself stays alive, and the two other
    // routes in still work afterwards -- this is what "leaves the model usable" means in practice.
    [Fact]
    public void Closing_a_container_leaves_the_other_two_routes_usable()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "p", iterations: 1000);
        AddKeyModel model = new(NewVault());
        model.OpenContainer(container, "p");

        model.CloseContainer();

        Assert.True(model.Generate("mine", "testnet"));
        Assert.NotNull(model.Preview);

        XrplWallet imported = XrplWallet.Generate();
        Assert.True(model.Import(new AddKeyDraft("typed", "testnet", imported.Seed, XrplWallet.Ed25519)));
        Assert.Equal(imported.ClassicAddress, model.Preview!.Address);
    }

    // ---- clearing a preview -----------------------------------------------------------------------

    // AddKey.razor's own fix for a stale preview surviving a tab switch: clearing the preview must
    // not also force the container route closed, unlike CloseContainer -- a container already opened
    // stays reachable so a different key can be chosen out of it without reopening the file.
    [Fact]
    public void Clearing_the_preview_drops_it_without_closing_an_open_container()
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "p", iterations: 1000);
        AddKeyModel model = new(NewVault());
        model.OpenContainer(container, "p");
        Assert.True(model.SelectContainerKey(0, "mine", "testnet"));
        Assert.NotNull(model.Preview);

        model.ClearPreview();

        Assert.Null(model.Preview);
        Assert.Single(model.ContainerKeys);
    }

    [Fact]
    public async Task Clearing_the_preview_leaves_nothing_prepared_to_store()
    {
        AddKeyModel model = new(NewVault());
        Assert.True(model.Generate("mine", "testnet"));

        model.ClearPreview();

        Assert.Null(model.Preview);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.StoreAsync(TestContext.Current.CancellationToken));
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

    // The network a key is stored under is what a link names and what Endpoints.For resolves, so a
    // value that differs only in case or padding must reach the vault in its canonical form, and one
    // no link can name must not reach it at all -- for each of the three ways in.
    private async Task<string> StoredNetworkAsync(Func<AddKeyModel, bool> prepare)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.True(prepare(model));
        await model.StoreAsync(TestContext.Current.CancellationToken);

        return Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken)).Network;
    }

    private static AddKeyModel OpenedContainerModel(KeyVault vault)
    {
        byte[] container = BackupContainer.Pack(
            PayloadOf(EntryFor(XrplWallet.Generate(), "a key")), "p", iterations: 1000);
        AddKeyModel model = new(vault);
        model.OpenContainer(container, "p");
        return model;
    }

    [Theory]
    [InlineData("TESTNET", "testnet")]
    [InlineData(" testnet ", "testnet")]
    [InlineData("Devnet", "devnet")]
    [InlineData("1", "1")]
    public async Task Generate_stores_the_canonical_network(string typed, string expected) =>
        Assert.Equal(expected, await StoredNetworkAsync(model => model.Generate("mine", typed)));

    [Theory]
    [InlineData("TESTNET", "testnet")]
    [InlineData(" testnet ", "testnet")]
    [InlineData("1", "1")]
    public async Task Import_stores_the_canonical_network(string typed, string expected) =>
        Assert.Equal(
            expected,
            await StoredNetworkAsync(model => model.Import(new AddKeyDraft("mine", typed, XrplWallet.Generate().Seed, null))));

    [Theory]
    [InlineData("TESTNET", "testnet")]
    [InlineData(" testnet ", "testnet")]
    [InlineData("1", "1")]
    public async Task SelectContainerKey_stores_the_canonical_network(string typed, string expected)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = OpenedContainerModel(vault);

        Assert.True(model.SelectContainerKey(0, "mine", typed));
        await model.StoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(await vault.ListAsync(TestContext.Current.CancellationToken)).Network);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("main net")]
    [InlineData("-1")]
    public async Task Generate_refuses_a_network_no_link_can_name_and_stores_nothing(string typed)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.False(model.Generate("mine", typed));

        Assert.Null(model.Preview);
        Assert.Contains($"'{typed}'", model.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.StoreAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await vault.ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("main net")]
    public async Task Import_refuses_a_network_no_link_can_name_and_stores_nothing(string typed)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = new(vault);

        Assert.False(model.Import(new AddKeyDraft("mine", typed, XrplWallet.Generate().Seed, null)));

        Assert.Null(model.Preview);
        Assert.Contains($"'{typed}'", model.Problem);
        Assert.Empty(await vault.ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("main net")]
    public async Task SelectContainerKey_refuses_a_network_no_link_can_name_and_stores_nothing(string typed)
    {
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), TestContext.Current.CancellationToken);
        AddKeyModel model = OpenedContainerModel(vault);

        Assert.False(model.SelectContainerKey(0, "mine", typed));

        Assert.Null(model.Preview);
        Assert.Contains($"'{typed}'", model.Problem);
        Assert.Empty(await vault.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_blank_network_still_asks_to_choose_one()
    {
        AddKeyModel model = new(NewVault());

        Assert.False(model.Generate("mine", ""));

        Assert.Equal("Choose a network.", model.Problem);
    }
}
