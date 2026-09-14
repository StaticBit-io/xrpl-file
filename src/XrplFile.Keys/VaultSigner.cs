using Xrpl.Wallet;

namespace XrplFile.Keys;

/// <summary>
/// Signs a publication's transactions from a key that never leaves the vault.
///
/// The seed is revealed, derived into a wallet, used for one signature and dropped: task 0
/// measured <see cref="XrplWallet.FromSeed"/> at 0.66–0.71 ms per call, so the roughly 21,000
/// signatures of a 20 MB publication cost about 15 seconds against a run of tens of minutes —
/// cheap enough that nothing is cached and a secret's lifetime here is one operation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Account"/> and <see cref="PublicKey"/> are given to the constructor rather than
/// derived on demand, because neither is a secret and neither may cost an unlock: an application
/// names the account a run will publish from while the vault is still shut, and an address that
/// could only be answered by opening the vault would make drawing a list of runs prompt for a
/// password.
/// </para>
/// <para>
/// A lock mid-publication therefore reaches the very next signature, and the run fails there with
/// <see cref="VaultError.Locked"/> rather than carrying on with a key the user has just put away.
/// That failure is survivable by design: part A resumes by asking the ledger what landed, so the
/// transactions already paid for are not sent a second time.
/// </para>
/// <para>
/// <see cref="VaultAutoLock"/> is optional, not required: a caller that wants no idle lock — every
/// existing test included — builds a signer with none, and nothing here asks for one. When one is
/// given, <see cref="SignAsync"/> touches it after every signature, exactly as design section 6.4
/// requires, so a publication signing continuously for tens of minutes does not lock itself out
/// mid-run while a machine left alone still shuts.
/// </para>
/// </remarks>
public sealed class VaultSigner : ITransactionSigner
{
    /// <summary>
    /// What <see cref="IKeyVault.RevealAsync"/> is told this reveal is for. It is carried for
    /// diagnostics and never written to disk, so it names the operation and nothing about the run.
    /// </summary>
    private const string Reason = "sign a transaction";

    private readonly IKeyVault _vault;
    private readonly string _reference;
    private readonly VaultAutoLock? _autoLock;

    /// <param name="vault">The vault holding the key. Its lock state decides whether a signature is possible.</param>
    /// <param name="reference">The entry to sign with. A signer never falls back on another one.</param>
    /// <param name="account">The classic address of that entry, from its <see cref="VaultEntryInfo"/>.</param>
    /// <param name="publicKey">The public key every transaction's <c>SigningPubKey</c> must carry.</param>
    /// <param name="autoLock">
    /// The idle lock to touch on every signature, or <see langword="null"/> for a signer that never
    /// touches one. Optional: a caller with no idle lock configured passes nothing, and every
    /// existing caller of this constructor keeps compiling unchanged.
    /// </param>
    public VaultSigner(IKeyVault vault, string reference, string account, string publicKey, VaultAutoLock? autoLock = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        _vault = vault;
        _reference = reference;
        _autoLock = autoLock;
        Account = account;
        PublicKey = publicKey;
    }

    /// <inheritdoc/>
    public string Account { get; }

    /// <inheritdoc/>
    public string PublicKey { get; }

    /// <summary>
    /// Builds a signer for an entry the vault already holds, by revealing it once to learn the
    /// public key — which an entry records the address of but not the key itself.
    /// </summary>
    /// <remarks>
    /// The one reveal happens here, at construction, and the wallet it derives is dropped before
    /// the signer exists. Everything afterwards follows the same rule every signature does.
    /// </remarks>
    /// <param name="autoLock">
    /// The idle lock the created signer touches on every signature, or <see langword="null"/> for
    /// none. See the type's remarks.
    /// </param>
    public static async Task<VaultSigner> CreateAsync(
        IKeyVault vault, string reference, CancellationToken cancellationToken, VaultAutoLock? autoLock = null)
    {
        ArgumentNullException.ThrowIfNull(vault);

        VaultEntry entry = await vault.RevealAsync(reference, "read a public key", cancellationToken).ConfigureAwait(false);
        XrplWallet wallet = XrplWallet.FromSeed(entry.Secret);

        // StoreAsync never checks an entry's recorded address against the key behind it, so a
        // mistyped or foreign address can sit in the vault indefinitely. Left unchecked here,
        // every chunk this signer builds would be addressed to the wrong account while signed by
        // a key that cannot authorise it — a failure that surfaces only at submission, as
        // tefBAD_AUTH_MASTER, after the account has already been funded.
        if (!string.Equals(entry.Info.Address, wallet.ClassicAddress, StringComparison.Ordinal))
        {
            throw new VaultException(
                VaultError.AddressMismatch,
                $"The entry at reference '{reference}' records an address that does not match its key.");
        }

        return new VaultSigner(vault, reference, entry.Info.Address, wallet.PublicKey, autoLock);
    }

    /// <inheritdoc/>
    public async Task<SignedTransaction> SignAsync(
        IDictionary<string, object> transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // Throws VaultException with Locked when the vault is shut and NoSuchEntry when the entry
        // is gone. Both are left to travel as they are: a caller that has to tell "unlock and try
        // again" from "this run's key no longer exists" can only do it from the typed error.
        VaultEntry entry = await _vault.RevealAsync(_reference, Reason, cancellationToken).ConfigureAwait(false);

        XrplWallet wallet = XrplWallet.FromSeed(entry.Secret);

        // A copy, because the SDK writes the signature back into the dictionary it is handed and
        // the caller's transaction is not this method's to change.
        SignatureResult signed = wallet.Sign(new Dictionary<string, object>(transaction));

        // Every signature counts as activity: design section 6.4 resets the idle window here so a
        // publication signing continuously does not lock itself out mid-run. A no-op when this
        // signer was built with no idle lock at all.
        _autoLock?.Touch();

        // Upper case, exactly as WalletSigner returns it: a hash whose case differs is a hash no
        // comparison against an account_tx answer will match.
        return new SignedTransaction(signed.TxBlob, signed.Hash.ToUpperInvariant());
    }
}
