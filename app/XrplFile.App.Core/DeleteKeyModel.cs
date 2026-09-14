using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// What is shown before a key is deleted (design 6, "Deleting a key"): the address that is about
/// to become unreachable, and whether it was ever exported. Deleting an entry never exported is
/// not removing a row — nothing else holds that key's secret, so it loses access to the account
/// for good.
/// </summary>
/// <param name="NeverExported">
/// <see langword="true"/> when the entry's <see cref="VaultEntryInfo.Exported"/> flag is
/// <see langword="false"/>: this vault is the only place the secret exists.
/// </param>
/// <param name="Address">The address about to become unreachable if the deletion goes ahead.</param>
public sealed record DeleteWarning(bool NeverExported, string Address);

/// <summary>
/// The model behind deleting a vault entry (design 6). <see cref="PrepareAsync"/> and
/// <see cref="DeleteAsync"/> are deliberately two calls, not one: <see cref="DeleteAsync"/> refuses
/// to act on a reference unless <see cref="PrepareAsync"/> has just reported the warning for that
/// same reference, so a caller cannot wire a delete button straight to
/// <see cref="IKeyVault.DeleteAsync"/> and skip past what it is about to cost.
/// </summary>
/// <param name="vault">The vault an entry is prepared for deletion in, and deleted from.</param>
public sealed class DeleteKeyModel(IKeyVault vault)
{
    private string? _prepared;

    /// <summary>
    /// Reads <paramref name="reference"/>'s metadata and reports what deleting it would cost,
    /// without deleting anything. Must be called, and must succeed, before <see cref="DeleteAsync"/>
    /// will act on this same reference — and a previous successful call for a different reference
    /// does not carry over: this clears it first.
    /// </summary>
    /// <remarks>
    /// Reads through <see cref="IKeyVault.ListAsync"/> rather than <see cref="IKeyVault.RevealAsync"/>:
    /// the warning needs only <see cref="VaultEntryInfo.Exported"/> and
    /// <see cref="VaultEntryInfo.Address"/>, both already in the vault's plain metadata, and
    /// revealing the secret to answer a question that does not need it would put a seed in memory
    /// this model has no reason to hold.
    /// </remarks>
    /// <exception cref="VaultException">
    /// <see cref="VaultError.Locked"/> when the vault is locked (from <see cref="IKeyVault.ListAsync"/>
    /// itself). <see cref="VaultError.EntryDamaged"/> when <paramref name="reference"/> names an
    /// entry <see cref="IKeyVault.DamagedReferences"/> carries: its address lives inside ciphertext
    /// that failed its own authentication tag, so nothing honest can be warned about it.
    /// <see cref="VaultError.NoSuchEntry"/> when the vault holds no such reference at all.
    /// </exception>
    public async Task<DeleteWarning> PrepareAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        _prepared = null;

        if (vault.DamagedReferences.Contains(reference, StringComparer.Ordinal))
        {
            throw new VaultException(
                VaultError.EntryDamaged,
                $"Entry '{reference}' is damaged; its address cannot be read to prepare a deletion.");
        }

        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
        VaultEntryInfo? info = listed.FirstOrDefault(
            candidate => string.Equals(candidate.Reference, reference, StringComparison.Ordinal));

        if (info is null)
        {
            throw new VaultException(VaultError.NoSuchEntry, $"No entry '{reference}' to prepare for deletion.");
        }

        _prepared = reference;
        return new DeleteWarning(NeverExported: !info.Exported, info.Address);
    }

    /// <summary>
    /// Deletes <paramref name="reference"/>. Refuses when <see cref="PrepareAsync"/> has not most
    /// recently succeeded for this same reference — see the type remarks.
    /// </summary>
    /// <remarks>
    /// A preparation is good for one attempt only: <see cref="_prepared"/> is cleared as soon as
    /// this method accepts the reference, before <see cref="IKeyVault.DeleteAsync"/> is even
    /// called, so it is cleared whether that call succeeds or throws. The warning
    /// <see cref="PrepareAsync"/> reported was shown to the person for one confirmation, not a
    /// standing authorization to delete this reference again later — a delete that fails (the vault
    /// now locked, writes refused over unacknowledged damage) must not leave that authorization
    /// sitting live for a later, unrelated press to spend without the warning having been in front
    /// of anyone at that moment. A retry requires a fresh <see cref="PrepareAsync"/>, and therefore
    /// a fresh look at the warning, every time.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="PrepareAsync"/> has not most recently succeeded for <paramref name="reference"/>.
    /// </exception>
    /// <exception cref="VaultException">
    /// Whatever <see cref="IKeyVault.DeleteAsync"/> itself throws — <see cref="VaultError.Degraded"/>
    /// when writes are refused over unacknowledged damage — propagates unchanged. The preparation is
    /// still consumed: see the remarks above.
    /// </exception>
    public async Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);

        if (!string.Equals(_prepared, reference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{reference}' was not prepared for deletion. Call PrepareAsync for it first.");
        }

        _prepared = null;
        await vault.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
    }
}
