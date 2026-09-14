using System.Buffers.Binary;

using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// What a vault does around a container: reveal, seal, mark, and roll back when the mark cannot be
/// written. The container's own format is <see cref="BackupContainerTests"/>.
/// </summary>
/// <remarks>
/// The class keeps its name and file from the <c>XFK1</c> container it was written for, and so do
/// the tests that pinned behaviour rather than layout -- the rollback paths and <c>Exported</c>
/// being set only on success are the same guarantees over a different container, and a test that
/// still passes untouched is the evidence that replacing the format moved none of them. The tests
/// that pinned the old header byte for byte are gone; each is named in the task report against what
/// replaced it.
/// </remarks>
public class ExportContainerTests : VaultTestHarness
{
    /// <summary>
    /// The password every export in this class is sealed under, as the container's API takes it.
    /// <see cref="VaultTestHarness.Password"/> is still what the vault itself is handed -- that
    /// surface takes bytes it can erase -- and the two must be the same text or nothing opens.
    /// </summary>
    private const string ContainerPassword = "export password";

    /// <summary>Opens what <see cref="IKeyVault.ExportAsync"/> returned, failing the test if it will not open.</summary>
    private static BackupPayload Open(byte[] container, string password = ContainerPassword)
    {
        Assert.True(
            BackupContainer.TryUnpack(
                container, password, fromFile: true, out BackupPayload? payload, out BackupUnpackError error),
            $"The container did not open: {error}.");

        return payload!;
    }

    [Fact]
    public async Task An_exported_entry_arrives_in_the_container_as_the_designs_table_says()
    {
        // The mapping is the whole point of adopting the Wallet's payload: the secret is the key
        // value, the address is the classic address, the label is the description, and the four
        // fields this application added carry what a VaultEntryInfo holds and their record does not.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "a label", "testnet", "rAddressOfone", "sSecretOfone"),
            token);
        await vault.UpdateAsync(
            reference, new VaultEntryEdit(LastKnownSequence: 42, DeletableFromLedger: 1_000), token);

        BackupWalletEntry exported = Open(
            await vault.ExportAsync(reference, Password(ContainerPassword), token)).Wallets.Single();

        Assert.Equal("sSecretOfone", exported.KeyValue);
        Assert.Equal("rAddressOfone", exported.ClassicAddress);
        Assert.Equal("a label", exported.Description);
        Assert.Equal("testnet", exported.Network);
        Assert.Equal("Imported", exported.Kind);
        Assert.Equal(42u, exported.LastKnownSequence);
        Assert.Equal(1_000L, exported.DeletableFromLedger);

        // This entry recorded no algorithm, which the container reads as ed25519 -- the same
        // meaning "absent" has always had.
        Assert.Equal("ed25519", exported.Algorithm);

        // Not ours, and written as null -- so, under WhenWritingNull, not written at all.
        Assert.Null(exported.MnemonicPassphrase);
        Assert.Null(exported.DerivationPath);
        Assert.Null(exported.MasterAddress);
        Assert.Null(exported.Folder);
        Assert.Null(exported.DestinationTag);
    }

    [Fact]
    public async Task An_entrys_own_algorithm_travels_to_the_container_instead_of_the_ed25519_default()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(
            new NewVaultEntry(
                VaultEntryKind.Imported, "a label", "testnet", "rAddressOfone", "sSecretOfone", "secp256k1"),
            token);

        BackupWalletEntry exported = Open(
            await vault.ExportAsync(reference, Password(ContainerPassword), token)).Wallets.Single();

        Assert.Equal("secp256k1", exported.Algorithm);
    }

    [Fact]
    public async Task An_export_is_sealed_at_the_shared_iteration_count()
    {
        // The header carries the count, and the other product honours what it finds there. Writing
        // one outside the shared range would produce a backup the Wallet refuses before it derives.
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);

        byte[] container = await vault.ExportAsync(reference, Password(ContainerPassword), token);

        int iterations = BinaryPrimitives.ReadInt32BigEndian(container.AsSpan(6, 4));
        Assert.Equal(Pbkdf2Kdf.DefaultIterations, iterations);
        Assert.InRange(iterations, Pbkdf2Kdf.IterationsFloor, Pbkdf2Kdf.IterationsCeiling);
        Assert.False(BackupContainer.DeclaresNoPassword(container));
    }

    [Fact]
    public async Task ExportAsync_on_a_locked_vault_throws_Locked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        await vault.LockAsync();

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ExportAsync(reference, Password(ContainerPassword), token));

        Assert.Equal(VaultError.Locked, error.Error);
    }

    [Fact]
    public async Task ExportAsync_of_an_unknown_reference_throws_NoSuchEntry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ExportAsync("no-such-reference", Password(ContainerPassword), token));

        Assert.Equal(VaultError.NoSuchEntry, error.Error);
    }

    [Fact]
    public async Task ExportAsync_of_a_damaged_entry_throws_EntryDamaged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);
        await vault.LockAsync();
        FlipOneByteOfEntry(reference);
        await vault.UnlockAsync(Password("open sesame"), token);

        VaultException error = await Assert.ThrowsAsync<VaultException>(
            () => vault.ExportAsync(reference, Password(ContainerPassword), token));

        Assert.Equal(VaultError.EntryDamaged, error.Error);
    }

    [Fact]
    public async Task ExportAsync_seals_the_entry_and_marks_only_it_Exported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string exportedReference = await vault.StoreAsync(Entry("exported"), token);
        string otherReference = await vault.StoreAsync(Entry("other"), token);

        byte[] container = await vault.ExportAsync(exportedReference, Password(ContainerPassword), token);

        Assert.Equal("sSecretOfexported", Open(container).Wallets.Single().KeyValue);

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(token);
        Assert.True(listed.Single(entry => entry.Reference == exportedReference).Exported);
        Assert.False(listed.Single(entry => entry.Reference == otherReference).Exported);
    }

    [Fact]
    public async Task An_export_no_longer_depends_on_the_vaults_own_KDF_cost()
    {
        // This replaces No_vault_can_hold_a_cost_its_own_export_container_could_not_carry. That test
        // pinned a coupling the XFK1 container created and this one does not have: the old header
        // carried the vault's Argon2id triple, so a cost the container could not write was a vault
        // that could never be exported from -- and export is the only way back from a forgotten
        // password (D10). The Wallet's header carries an iteration count and nothing else, derived
        // from the container's own password, so the two costs are now independent and the widest
        // vault this library can build exports exactly like the narrowest.
        CancellationToken token = TestContext.Current.CancellationToken;

        KdfParameters widest = new(
            MemoryKib: KdfParameterBounds.MinimumMemoryKibPerLane * KdfParameterBounds.MaximumParallelism,
            Iterations: 1,
            Parallelism: KdfParameterBounds.MaximumParallelism);
        KeyVault vault = NewVault(widest);
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);

        byte[] container = await vault.ExportAsync(reference, Password(ContainerPassword), token);

        Assert.Equal("sSecretOfone", Open(container).Wallets.Single().KeyValue);
        Assert.True((await vault.ListAsync(token)).Single().Exported);
    }

    [Fact]
    public async Task A_failure_recording_Exported_still_returns_the_sealed_container()
    {
        // The seal already succeeded before the write is attempted; only the flag's persistence
        // fails. Losing the container here would make the one guaranteed recovery path (D10)
        // unreliable exactly when storage already is, so the container comes back regardless —
        // and the entry is left exactly as the file has it, unexported, so the caller is nagged to
        // export it again rather than told a backup exists that the disk never actually received.
        CancellationToken token = TestContext.Current.CancellationToken;
        FailableWrites disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);

        disk.Fail = true;
        byte[] container = await vault.ExportAsync(reference, Password(ContainerPassword), token);

        Assert.NotEmpty(container);
        Assert.Equal("sSecretOfone", Open(container).Wallets.Single().KeyValue);

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(token);
        Assert.False(listed.Single().Exported);

        disk.Fail = false;
    }

    [Fact]
    public async Task A_cancelled_export_leaves_the_entry_saying_what_the_file_says()
    {
        // The container never reaches the caller when the token is cancelled, so nothing was
        // backed up anywhere — and the cache must not be left claiming otherwise, behind a file
        // that says Exported is false. A vault claiming a key was backed up when it never left is
        // the one lie this flag cannot afford: the warning the whole no-recovery-phrase decision
        // rests on stops firing for exactly that key.
        CancellationToken token = TestContext.Current.CancellationToken;
        CancelDuringWrite disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("open sesame"), token);
        string reference = await vault.StoreAsync(Entry("one"), token);

        disk.CancelTheNextWrite = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => vault.ExportAsync(reference, Password(ContainerPassword), disk.Cancellation.Token));

        disk.CancelTheNextWrite = false;
        Assert.False((await vault.ListAsync(token)).Single().Exported);

        // And the next ordinary write is what would commit a cache left lying, permanently.
        await vault.UpdateAsync(reference, new VaultEntryEdit(Label: "renamed"), token);
        Assert.False(await ExportedOnDiskAsync(reference));
    }

    [Fact]
    public async Task A_cancelled_store_leaves_no_entry_the_file_does_not_hold()
    {
        // The sibling rule the export above has to match: a cancelled write rolls the cache back
        // exactly as a refused one does, because neither of the two landed.
        CancellationToken token = TestContext.Current.CancellationToken;
        CancelDuringWrite disk = new();
        KeyVault vault = NewVault(disk.WriteAsync);
        await vault.CreateAsync(Password("open sesame"), token);

        disk.CancelTheNextWrite = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => vault.StoreAsync(Entry("one"), disk.Cancellation.Token));

        disk.CancelTheNextWrite = false;
        Assert.Empty(vault.References);
        Assert.Empty(await vault.ListAsync(token));
    }

    /// <summary>Whether the file — rather than the cache of the vault under test — says this entry was exported.</summary>
    private async Task<bool> ExportedOnDiskAsync(string reference)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault reader = NewVault();
        Assert.Equal(VaultUnlockResult.Success, await reader.UnlockAsync(Password("open sesame"), token));

        IReadOnlyList<VaultEntryInfo> listed = await reader.ListAsync(token);
        return listed.Single(entry => entry.Reference == reference).Exported;
    }

    /// <summary>
    /// The disk with a cancellation in it: the write the vault is midway through observes a
    /// cancelled token. It is the one write failure no test in this suite reached before.
    /// </summary>
    private sealed class CancelDuringWrite
    {
        internal CancellationTokenSource Cancellation { get; } = new();

        internal bool CancelTheNextWrite { get; set; }

        internal Task WriteAsync(string path, byte[] contents, CancellationToken cancellationToken)
        {
            if (!CancelTheNextWrite)
            {
                return DurableFile.WriteAsync(path, contents, cancellationToken);
            }

            Cancellation.Cancel();
            return Task.FromCanceled(Cancellation.Token);
        }
    }

    [Fact]
    public async Task Importing_a_container_creates_an_Imported_entry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);

        BackupPayload payload = new()
        {
            Wallets =
            [
                new BackupWalletEntry(
                    KeyValue: "sSecretOforiginal",
                    MnemonicPassphrase: null,
                    ClassicAddress: "rImportMe",
                    KeyType: 1,
                    Algorithm: "secp256k1",
                    DerivationPath: null,
                    MasterAddress: null,
                    Description: "original",
                    Network: "testnet",
                    Kind: nameof(VaultEntryKind.Generated)),
            ],
        };

        byte[] container = BackupContainer.Pack(payload, ContainerPassword, Pbkdf2Kdf.IterationsFloor);
        BackupWalletEntry recovered = Open(container).Wallets.Single();

        // The kind the container carries is where the key came from in the vault that wrote it, not
        // what it is here: arriving from outside makes it Imported whatever it used to be. The
        // algorithm travels unchanged — not secp256k1 by coincidence, but because the importer reads
        // the field the container actually carries rather than assuming the SDK's ed25519 default.
        string reference = await vault.StoreAsync(
            new NewVaultEntry(
                VaultEntryKind.Imported,
                recovered.Description!,
                recovered.Network!,
                recovered.ClassicAddress,
                recovered.KeyValue,
                recovered.Algorithm),
            token);

        VaultEntry stored = await vault.RevealAsync(reference, "import", token);
        Assert.Equal(VaultEntryKind.Imported, stored.Info.Kind);
        Assert.Equal("rImportMe", stored.Info.Address);
        Assert.Equal("sSecretOforiginal", stored.Secret);
        Assert.Equal("secp256k1", stored.Info.Algorithm);
    }

    [Fact]
    public async Task Importing_a_key_the_vault_already_holds_on_this_network_is_refused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "original", "testnet", "rSameAddress", "sOriginalSeed"),
            token);

        // The same address and network a bare-seed or container import would hand back — the vault
        // must not silently end up holding the same key under two references.
        NewVaultEntry reimported = new(VaultEntryKind.Imported, "reimported", "testnet", "rSameAddress", "sOriginalSeed");

        VaultException error = await Assert.ThrowsAsync<VaultException>(() => vault.StoreAsync(reimported, token));

        Assert.Equal(VaultError.DuplicateAddress, error.Error);
        Assert.Single(vault.References);
    }

    [Fact]
    public async Task The_same_address_on_a_different_network_is_not_a_duplicate()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "original", "testnet", "rSameAddress", "sOriginalSeed"),
            token);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Imported, "on another network", "mainnet", "rSameAddress", "sOriginalSeed"),
            token);

        Assert.Contains(reference, vault.References);
        Assert.Equal(2, vault.References.Count);
    }
}
