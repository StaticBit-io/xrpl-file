using Xrpl.Wallet;
using Xunit;

namespace XrplFile.Keys.Tests;

/// <summary>
/// The signer that turns "the key is in the vault" into "the key signs without leaving it".
///
/// Two of these tests are about what the signer does <em>not</em> do: it does not open the vault
/// to answer an address, and it does not sign with anything other than the entry it was pointed
/// at. Both are the difference between a seam that means something while a vault is locked and
/// one that only looks like it does.
/// </summary>
public class VaultSignerTests : VaultTestHarness
{
    /// <summary>A vault holding one entry over a real seed, left unlocked, with the wallet behind it.</summary>
    private async Task<(KeyVault Vault, string Reference, XrplWallet Wallet)> VaultHoldingAKeyAsync(
        CancellationToken cancellationToken)
    {
        XrplWallet wallet = XrplWallet.Generate();
        KeyVault vault = NewVault();

        await vault.CreateAsync(Password("open sesame"), cancellationToken);

        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "publisher", "standalone", wallet.ClassicAddress, wallet.Seed),
            cancellationToken);

        return (vault, reference, wallet);
    }

    private static Dictionary<string, object> Transaction(XrplWallet wallet, uint sequence) => new()
    {
        ["TransactionType"] = "AccountSet",
        ["Account"] = wallet.ClassicAddress,
        ["Fee"] = "12",
        ["Sequence"] = sequence,
        ["LastLedgerSequence"] = 100u,
        ["SigningPubKey"] = wallet.PublicKey,
    };

    [Fact]
    public async Task The_account_and_the_public_key_are_answered_by_a_locked_vault()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        await vault.LockAsync();

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey);

        // Neither is a secret, and neither may cost an unlock: a caller draws the account a run
        // will publish from long before it asks for a signature, and on a vault that is shut.
        Assert.Equal(wallet.ClassicAddress, signer.Account);
        Assert.Equal(wallet.PublicKey, signer.PublicKey);
    }

    [Fact]
    public async Task A_signature_is_the_blob_the_wallet_itself_would_have_made()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey);
        SignedTransaction signed = await signer.SignAsync(Transaction(wallet, 7u), token);

        SignedTransaction direct = await new WalletSigner(wallet).SignAsync(Transaction(wallet, 7u), token);

        Assert.Equal(direct.TxBlob, signed.TxBlob);
        Assert.Equal(direct.Hash, signed.Hash);

        // Upper case, the same as WalletSigner: a hash whose case differs is a hash no comparison
        // against an account_tx answer will ever match.
        Assert.Equal(signed.Hash.ToUpperInvariant(), signed.Hash);
    }

    [Fact]
    public async Task Signing_through_a_locked_vault_is_refused_as_locked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey);
        await vault.LockAsync();

        VaultException refused = await Assert.ThrowsAsync<VaultException>(
            () => signer.SignAsync(Transaction(wallet, 7u), token));

        Assert.Equal(VaultError.Locked, refused.Error);
    }

    [Fact]
    public async Task Signing_for_an_entry_that_is_gone_is_refused_as_no_such_entry()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        // A second entry, so the vault is not merely empty. What must not happen is that a signer
        // whose own entry is gone falls back on whatever key the vault still holds: a signature
        // made by the wrong key is worse than no signature at all.
        await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "other", "standalone", "rOther", XrplWallet.Generate().Seed),
            token);

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey);
        await vault.DeleteAsync(reference, token);

        VaultException refused = await Assert.ThrowsAsync<VaultException>(
            () => signer.SignAsync(Transaction(wallet, 7u), token));

        Assert.Equal(VaultError.NoSuchEntry, refused.Error);
    }

    [Fact]
    public async Task Creating_a_signer_for_an_entry_whose_address_does_not_match_its_key_is_refused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        XrplWallet wallet = XrplWallet.Generate();
        string foreignAddress = XrplWallet.Generate().ClassicAddress;
        KeyVault vault = NewVault();

        await vault.CreateAsync(Password("open sesame"), token);

        // An entry imported with an address that is not this key's own — a typo, or a mismatched
        // pair from a migration. StoreAsync never checks the two against each other, so this must
        // be caught here, before a chunk is ever built for an account this key cannot authorise.
        string reference = await vault.StoreAsync(
            new NewVaultEntry(VaultEntryKind.Generated, "publisher", "standalone", foreignAddress, wallet.Seed),
            token);

        VaultException refused = await Assert.ThrowsAsync<VaultException>(
            () => VaultSigner.CreateAsync(vault, reference, token));

        Assert.Equal(VaultError.AddressMismatch, refused.Error);

        // No key material in the message: not the seed, not the public key.
        Assert.DoesNotContain(wallet.Seed, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(wallet.PublicKey, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_for_a_damaged_entry_is_refused_as_entry_damaged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey);

        await vault.LockAsync();
        FlipOneByteOfEntry(reference);
        await vault.UnlockAsync(Password("open sesame"), token);

        VaultException refused = await Assert.ThrowsAsync<VaultException>(
            () => signer.SignAsync(Transaction(wallet, 7u), token));

        Assert.Equal(VaultError.EntryDamaged, refused.Error);
    }

    [Fact]
    public async Task Every_signature_touches_the_idle_lock_so_a_publication_does_not_lock_itself_out()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        ManualTimeProvider time = new();
        TimeSpan idle = TimeSpan.FromMinutes(15);
        await using VaultAutoLock autoLock = new(vault, idle, time);

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey, autoLock);

        // Ten simulated minutes between each signature — inside the idle window every time — over
        // four rounds: forty minutes of continuous signing that a fifteen-minute idle timeout
        // must not interrupt, exactly the scenario design section 6.4 exists for.
        for (uint sequence = 1; sequence <= 4; sequence++)
        {
            time.Advance(TimeSpan.FromMinutes(10));
            await signer.SignAsync(Transaction(wallet, sequence), token);
        }

        Assert.True(vault.IsUnlocked);

        // The other half of the same behaviour: with no further signature to touch it, the same
        // window the run kept postponing finally elapses, and the vault the run left alone locks.
        time.Advance(idle);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task A_signer_built_with_no_idle_lock_never_touches_one()
    {
        // Optional, not required (task 10's brief): a caller with no idle lock configured, and
        // every other test in this file, builds a signer with none and must keep signing fine.
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        VaultSigner signer = new(vault, reference, wallet.ClassicAddress, wallet.PublicKey);
        await signer.SignAsync(Transaction(wallet, 1u), token);

        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Every_signature_asks_the_vault_again()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (KeyVault vault, string reference, XrplWallet wallet) = await VaultHoldingAKeyAsync(token);

        CountingVault counted = new(vault);
        VaultSigner signer = new(counted, reference, wallet.ClassicAddress, wallet.PublicKey);

        for (uint sequence = 1; sequence <= 3; sequence++)
        {
            await signer.SignAsync(Transaction(wallet, sequence), token);
        }

        // The measured decision, under test rather than only in a comment: nothing derived is
        // kept, so a lock between two signatures is felt by the second one.
        Assert.Equal(3, counted.Reveals.Count);
        Assert.All(counted.Reveals, call => Assert.Equal(reference, call.Reference));
        Assert.All(counted.Reveals, call => Assert.False(string.IsNullOrWhiteSpace(call.Reason)));
    }

    /// <summary>
    /// A vault that forwards everything and writes down what was revealed. It exists for one
    /// assertion — that a signature costs a reveal every time — which nothing observable on
    /// <see cref="KeyVault"/> itself can make.
    /// </summary>
    private sealed class CountingVault(IKeyVault inner) : IKeyVault
    {
        public List<(string Reference, string Reason)> Reveals { get; } = [];

        public bool IsUnlocked => inner.IsUnlocked;

        public IReadOnlyCollection<string> References => inner.References;

        public IReadOnlyList<string> DamagedReferences => inner.DamagedReferences;

        public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) =>
            inner.AcknowledgeDamageAsync(cancellationToken);

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => inner.ExistsAsync(cancellationToken);

        public Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken) =>
            inner.HasDeviceWrapAsync(cancellationToken);

        public Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.CreateAsync(password, cancellationToken);

        public Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken) =>
            inner.UnlockAsync(password, cancellationToken);

        public Task LockAsync() => inner.LockAsync();

        public Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken) =>
            inner.ListAsync(cancellationToken);

        public Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken) =>
            inner.StoreAsync(entry, cancellationToken);

        public Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken)
        {
            Reveals.Add((reference, reason));
            return inner.RevealAsync(reference, reason, cancellationToken);
        }

        public Task<byte[]> ExportAsync(
            string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken) =>
            inner.ExportAsync(reference, containerPassword, cancellationToken);

        public Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken) =>
            inner.UpdateAsync(reference, edit, cancellationToken);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            inner.DeleteAsync(reference, cancellationToken);

        public Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken) =>
            inner.UnlockWithDeviceAsync(cancellationToken);

        public Task<VaultPasswordChange> ChangePasswordAsync(
            ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken) =>
            inner.ChangePasswordAsync(oldPassword, newPassword, cancellationToken);

        public Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.EnableDeviceUnlockAsync(cancellationToken);

        public Task DisableDeviceUnlockAsync(CancellationToken cancellationToken) =>
            inner.DisableDeviceUnlockAsync(cancellationToken);
    }
}
