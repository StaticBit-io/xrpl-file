using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// What an entry's algorithm is for: telling <see cref="VaultSigner"/> which curve to derive with,
/// so a seed that was never ed25519 still comes back as the account it actually is.
/// </summary>
/// <remarks>
/// Before this field existed, <see cref="VaultSigner"/> always derived with
/// <c>XrplWallet.FromSeed(entry.Secret)</c>, which is ed25519 because that is the SDK's own
/// default. A secp256k1 key derived from the same seed is a different account with a different
/// address, so the address check added by an earlier task refused every secp256k1 entry with
/// <see cref="VaultError.AddressMismatch"/> — the vault failed safely, but it failed. It could not
/// hold a secp256k1 key at all.
/// </remarks>
public class AlgorithmTests : VaultTestHarness
{
    [Fact]
    public async Task A_secp256k1_key_can_be_stored_and_signed_with()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Secp256k1);
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(
                VaultEntryKind.Imported, "imported", "testnet", wallet.ClassicAddress, wallet.Seed, XrplWallet.Secp256k1),
            token);

        VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, token);

        Assert.Equal(wallet.ClassicAddress, signer.Account);
        Assert.Equal(wallet.PublicKey, signer.PublicKey);
    }

    [Fact]
    public async Task An_entry_with_no_algorithm_is_ed25519()
    {
        // Everything stored before this field existed was derived that way, so absent must keep
        // meaning what it meant.
        CancellationToken token = TestContext.Current.CancellationToken;

        XrplWallet wallet = XrplWallet.Generate();
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(
                VaultEntryKind.Imported, "imported", "testnet", wallet.ClassicAddress, wallet.Seed, Algorithm: null),
            token);

        VaultSigner signer = await VaultSigner.CreateAsync(vault, reference, token);

        Assert.Equal(wallet.ClassicAddress, signer.Account);
    }

    [Fact]
    public async Task An_entrys_algorithm_survives_a_reload_from_disk()
    {
        // The canonical JSON gains the field, and the reader must be able to see it again -- a
        // vault that forgot the algorithm on every restart would be back to today's defect the
        // moment the process holding it in memory exits.
        CancellationToken token = TestContext.Current.CancellationToken;

        XrplWallet wallet = XrplWallet.Generate(XrplWallet.Secp256k1);
        KeyVault vault = NewVault();
        await vault.CreateAsync(Password("p"), token);
        string reference = await vault.StoreAsync(
            new NewVaultEntry(
                VaultEntryKind.Imported, "imported", "testnet", wallet.ClassicAddress, wallet.Seed, XrplWallet.Secp256k1),
            token);
        await vault.LockAsync();

        KeyVault reopened = NewVault();
        await reopened.UnlockAsync(Password("p"), token);

        VaultSigner signer = await VaultSigner.CreateAsync(reopened, reference, token);

        Assert.Equal(wallet.ClassicAddress, signer.Account);
        Assert.Equal(wallet.PublicKey, signer.PublicKey);
    }
}
