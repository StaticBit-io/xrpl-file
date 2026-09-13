using System.Buffers.Binary;

using Xunit;

namespace XrplFile.Keys.Tests;

public class ExportContainerTests : VaultTestHarness
{
    // Argon2id at the smallest cost the implementation accepts — see VaultTestHarness.FastParameters
    // for why a suite that derives in most of its tests cannot afford the measured real cost.
    private static readonly KdfParameters FastExportParameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);

    private static VaultEntry MakeEntry(
        string label,
        VaultEntryKind kind = VaultEntryKind.Generated,
        string network = "standalone",
        string? address = null,
        uint? lastKnownSequence = null,
        long? deletableFromLedger = null,
        bool exported = false) =>
        new(
            new VaultEntryInfo(
                Guid.NewGuid().ToString("N"),
                kind,
                label,
                network,
                address ?? "rAddressOf" + label,
                DateTimeOffset.UtcNow,
                lastKnownSequence,
                deletableFromLedger,
                exported),
            "sSecretOf" + label);

    [Fact]
    public void A_container_round_trips_every_field_of_every_entry()
    {
        VaultEntry[] entries =
        [
            MakeEntry("one", lastKnownSequence: 42, deletableFromLedger: 1_000),
            MakeEntry("two", VaultEntryKind.Imported, network: "testnet", exported: true),
        ];

        byte[] container = ExportContainer.Protect(entries, Password("export password").Span, FastExportParameters);
        IReadOnlyList<VaultEntry> opened = ExportContainer.Open(container, Password("export password").Span);

        Assert.Equal(entries, opened);
    }

    [Fact]
    public void An_empty_entry_list_round_trips_to_an_empty_list()
    {
        byte[] container = ExportContainer.Protect([], Password("export password").Span, FastExportParameters);
        IReadOnlyList<VaultEntry> opened = ExportContainer.Open(container, Password("export password").Span);

        Assert.Empty(opened);
    }

    [Fact]
    public void A_wrong_password_throws_Corrupt_and_returns_nothing()
    {
        VaultEntry[] entries = [MakeEntry("one")];
        byte[] container = ExportContainer.Protect(entries, Password("right").Span, FastExportParameters);

        VaultException error = Assert.Throws<VaultException>(
            () => ExportContainer.Open(container, Password("wrong").Span));

        // Indistinguishable from a damaged container, on purpose (D5's reasoning, applied here):
        // there is no verifier a wrong-password guess could be checked against instead.
        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public void A_container_too_short_to_hold_a_header_is_rejected_as_corrupt()
    {
        VaultException error = Assert.Throws<VaultException>(
            () => ExportContainer.Open(new byte[10], Password("right").Span));

        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public void An_altered_magic_is_rejected()
    {
        VaultEntry[] entries = [MakeEntry("one")];
        byte[] container = ExportContainer.Protect(entries, Password("right").Span, FastExportParameters);

        byte[] tampered = (byte[])container.Clone();
        tampered[0] = (byte)'Y';

        VaultException error = Assert.Throws<VaultException>(
            () => ExportContainer.Open(tampered, Password("right").Span));

        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public void An_unknown_format_version_is_rejected_as_unsupported()
    {
        VaultEntry[] entries = [MakeEntry("one")];
        byte[] container = ExportContainer.Protect(entries, Password("right").Span, FastExportParameters);

        // The version byte follows the four-byte magic — spec section 8's layout.
        byte[] tampered = (byte[])container.Clone();
        tampered[4] = 99;

        VaultException error = Assert.Throws<VaultException>(
            () => ExportContainer.Open(tampered, Password("right").Span));

        Assert.Equal(VaultError.Unsupported, error.Error);
    }

    [Fact]
    public void A_header_edited_to_lower_the_KDF_cost_fails_to_open_even_with_the_right_password()
    {
        // The tampered value (8) must still be a cost Argon2id itself accepts, or the failure would
        // come from parameter validation rather than from what this test actually pins: the header
        // binding. 16 -> 8 is a real downgrade an attacker could otherwise exploit.
        KdfParameters original = new(MemoryKib: 16, Iterations: 1, Parallelism: 1);
        VaultEntry[] entries = [MakeEntry("one")];
        byte[] container = ExportContainer.Protect(entries, Password("right").Span, original);

        // memoryKib is the four big-endian bytes right after magic, version and kdfId.
        byte[] tampered = (byte[])container.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(tampered.AsSpan(6, 4), 8);

        // The header is the associated data of the seal, so a lowered cost does not merely make
        // this open cheaply — it makes the authentication tag fail, whatever the password is.
        VaultException error = Assert.Throws<VaultException>(
            () => ExportContainer.Open(tampered, Password("right").Span));

        Assert.Equal(VaultError.Corrupt, error.Error);
    }

    [Fact]
    public void The_container_layout_matches_spec_section_8_byte_for_byte()
    {
        KdfParameters parameters = new(MemoryKib: 8, Iterations: 1, Parallelism: 1);
        VaultEntry[] entries = [MakeEntry("one")];

        byte[] container = ExportContainer.Protect(entries, Password("right").Span, parameters);

        Assert.Equal("XFK1"u8.ToArray(), container[..4]);
        Assert.Equal(1, container[4]); // version
        Assert.Equal(2, container[5]); // kdfId: Argon2id (D2)
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(container.AsSpan(6, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(container.AsSpan(10, 4)));
        Assert.Equal(1, container[14]); // parallelism

        // header(31) + nonce(12) + tag(16) is the smallest a container can ever be; the rest is
        // the deflate-compressed, encrypted entry list.
        Assert.True(container.Length > 31 + 12 + 16);
    }

    [Fact]
    public async Task Exporting_marks_only_the_entry_that_was_exported()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);
        string exportedReference = await vault.StoreAsync(Entry("exported"), token);
        string otherReference = await vault.StoreAsync(Entry("other"), token);

        VaultEntry revealed = await vault.RevealAsync(exportedReference, "export", token);
        byte[] container = ExportContainer.Protect(
            [revealed], Password("export password").Span, FastExportParameters);
        Assert.NotEmpty(container);

        // ExportContainer never touches the vault (it has no reference to one); marking the entry
        // exported is the caller's own next step, using the vault's existing edit surface.
        await vault.UpdateAsync(exportedReference, new VaultEntryEdit(Exported: true), token);

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(token);
        Assert.True(listed.Single(entry => entry.Reference == exportedReference).Exported);
        Assert.False(listed.Single(entry => entry.Reference == otherReference).Exported);
    }

    [Fact]
    public async Task Importing_a_container_creates_an_Imported_entry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("open sesame"), token);

        VaultEntry exported = MakeEntry("original", network: "testnet", address: "rImportMe");
        byte[] container = ExportContainer.Protect(
            [exported], Password("export password").Span, FastExportParameters);
        VaultEntry recovered = ExportContainer.Open(container, Password("export password").Span).Single();

        string reference = await vault.StoreAsync(
            new NewVaultEntry(
                VaultEntryKind.Imported,
                recovered.Info.Label,
                recovered.Info.Network,
                recovered.Info.Address,
                recovered.Secret),
            token);

        VaultEntry stored = await vault.RevealAsync(reference, "import", token);
        Assert.Equal(VaultEntryKind.Imported, stored.Info.Kind);
        Assert.Equal(recovered.Info.Address, stored.Info.Address);
        Assert.Equal(recovered.Secret, stored.Secret);
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
