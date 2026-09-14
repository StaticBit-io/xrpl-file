using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// One row of the key list (design 6). <see cref="Info"/> is always populated: for an entry the
/// vault could decrypt, it is what <see cref="IKeyVault.ListAsync"/> returned; for one that
/// failed its own authentication tag, decryption never ran, so every field but
/// <see cref="VaultEntryInfo.Reference"/> is a placeholder — <see cref="IsDamaged"/> is what tells
/// a caller not to trust them.
/// </summary>
public sealed record KeyRow(VaultEntryInfo Info, bool IsDamaged, bool NeedsExporting);

/// <summary>
/// The model behind the key list (design 6, 3.1): the entries the vault can show, which of them
/// are damaged, and whether the vault currently refuses writes over damage nobody has looked at
/// yet. Every branch the key-list screen can take lives here, reachable from a test with no
/// operating system in the loop.
/// </summary>
public sealed class VaultListModel(IKeyVault vault)
{
    private bool _acknowledged;

    /// <summary>
    /// The vault's entries, in the order <see cref="IKeyVault.References"/> reports them.
    /// Meaningful only after <see cref="RefreshAsync"/> has run at least once; empty before that.
    /// </summary>
    public IReadOnlyList<KeyRow> Rows { get; private set; } = [];

    /// <summary>
    /// Whether the vault reported any damaged entry at its last unlock
    /// (<see cref="IKeyVault.DamagedReferences"/> is not empty). Unlike
    /// <see cref="WritesAreRefused"/>, this does not clear when the damage is acknowledged: the
    /// damaged entries are still unreadable, and the screen keeps naming them (design 6, "Damage")
    /// even once writing is allowed again.
    /// </summary>
    public bool IsDegraded { get; private set; }

    /// <summary>
    /// Whether a write against the vault would be refused with <see cref="VaultError.Degraded"/>
    /// right now. True from the refresh that first observes <see cref="IsDegraded"/>, and false
    /// from the moment <see cref="AcknowledgeDamageAsync"/> succeeds — set directly there rather
    /// than by asking the vault again, because the vault has no property for it and a write-only
    /// to find out would be exactly the probing this model must not do (see that method's remarks).
    /// </summary>
    public bool WritesAreRefused { get; private set; }

    /// <summary>
    /// Re-reads the vault's entries and its damage. Requires the vault to be unlocked, the same as
    /// <see cref="IKeyVault.ListAsync"/> it calls through to — this model exists only for the
    /// unlocked key-list screen (design 3.1), so a locked vault here is a caller's mistake, not a
    /// state this method smooths over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row exists for every reference <see cref="IKeyVault.References"/> carries, which is the
    /// vault's own instruction for telling a damaged entry from a deleted one apart (design: "an
    /// entry deleted from the file shows up nowhere at all — the vault cannot report a casualty it
    /// has no record of"). A reference the vault still holds but that
    /// <see cref="IKeyVault.ListAsync"/> could not decrypt is exactly
    /// <see cref="IKeyVault.DamagedReferences"/>, which is the signal this method uses to build the
    /// damaged rows — not a set difference against <see cref="IKeyVault.ListAsync"/>'s own result,
    /// which would say the same thing more expensively and less directly. A reference that is in
    /// neither list was removed from the record entirely and gets no row at all — the model cannot
    /// tell a story about a key it has no trace of.
    /// </para>
    /// <para>
    /// <see cref="WritesAreRefused"/> is recomputed here from <see cref="IsDegraded"/>, but only
    /// while damage has not already been acknowledged this session: once
    /// <see cref="AcknowledgeDamageAsync"/> has run, a later call to this method must not flip it
    /// back to <see langword="true"/> just because the same damaged entries are, correctly, still
    /// being reported — the vault's own write refusal really is lifted, and re-asserting it here
    /// would nag about a write that would in fact succeed.
    /// </para>
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<VaultEntryInfo> listed = await vault.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> damagedReferences = vault.DamagedReferences;

        Dictionary<string, VaultEntryInfo> byReference = new(listed.Count, StringComparer.Ordinal);
        foreach (VaultEntryInfo info in listed)
        {
            byReference[info.Reference] = info;
        }

        HashSet<string> damagedSet = new(damagedReferences, StringComparer.Ordinal);

        List<KeyRow> rows = new(vault.References.Count);
        foreach (string reference in vault.References)
        {
            if (byReference.TryGetValue(reference, out VaultEntryInfo? info))
            {
                bool needsExporting = info.Kind == VaultEntryKind.Generated && !info.Exported;
                rows.Add(new KeyRow(info, IsDamaged: false, NeedsExporting: needsExporting));
            }
            else if (damagedSet.Contains(reference))
            {
                rows.Add(new KeyRow(Placeholder(reference), IsDamaged: true, NeedsExporting: false));
            }

            // Neither: the reference disappeared between reading References and reading the list
            // (a concurrent write on another handle to the same vault). Nothing true can be said
            // about it, so it gets no row, the same as a reference deleted outright.
        }

        Rows = rows;
        IsDegraded = damagedReferences.Count > 0;
        WritesAreRefused = IsDegraded && !_acknowledged;
    }

    /// <summary>
    /// Tells the vault that the damage named by this refresh has been shown to a person, and
    /// releases the writes it was blocking. This is the human act design 6's "Damage" paragraph
    /// requires: it is called from a press on an explicit "I've seen it" control with the damaged
    /// rows on screen, never from a <c>catch (VaultException { Error: VaultError.Degraded })</c>
    /// around a write — a model that acknowledged automatically to retry a blocked write would
    /// destroy the one witness there is to an entry deleted from the file or the file rolled back,
    /// exactly as the bug this method's own contract was written to prevent did.
    /// </summary>
    public async Task AcknowledgeDamageAsync(CancellationToken cancellationToken)
    {
        await vault.AcknowledgeDamageAsync(cancellationToken).ConfigureAwait(false);
        _acknowledged = true;
        WritesAreRefused = false;
    }

    /// <summary>
    /// The only <see cref="VaultEntryInfo"/> that can exist for a reference
    /// <see cref="IKeyVault.ListAsync"/> refused to decrypt: its identity, and nothing else. Every
    /// other field lives inside the encrypted blob (design D7), so a failed authentication tag
    /// makes them unrecoverable, not merely unknown — <see cref="VaultEntryKind.Imported"/> is
    /// chosen over <see cref="VaultEntryKind.Generated"/> only so a damaged row can never be
    /// mistaken for a throwaway account nobody exported; <see cref="KeyRow.NeedsExporting"/> is
    /// forced to <see langword="false"/> by the caller regardless.
    /// </summary>
    private static VaultEntryInfo Placeholder(string reference) => new(
        Reference: reference,
        Kind: VaultEntryKind.Imported,
        Label: string.Empty,
        Network: string.Empty,
        Address: string.Empty,
        CreatedAt: default,
        LastKnownSequence: null,
        DeletableFromLedger: null,
        Exported: false,
        Algorithm: null);
}
