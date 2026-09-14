using XrplFile.Keys;

namespace XrplFile.App.Core;

/// <summary>
/// One row of the key list (design 6). <see cref="Reference"/> is always the vault's reference for
/// this entry; <see cref="Info"/> is the vault's metadata for it, or <see langword="null"/> when
/// <see cref="IsDamaged"/>. There is no placeholder <see cref="VaultEntryInfo"/> for a damaged
/// entry: its label, kind, network and address all live inside the ciphertext that failed its own
/// authentication tag, so decryption never ran and nothing about them can honestly be reported.
/// Making <see cref="Info"/> nullable rather than documenting the placeholder is deliberate — a
/// caller that reaches for <c>row.Info.Kind</c> without checking <see cref="IsDamaged"/> first gets
/// a compiler error under this repository's <c>TreatWarningsAsErrors</c>, not a row that quietly
/// hands out a kind, label, network or address nobody could actually read.
/// </summary>
public sealed record KeyRow(string Reference, VaultEntryInfo? Info, bool IsDamaged, bool NeedsExporting);

/// <summary>
/// The model behind the key list (design 6, 3.1): the entries the vault can show, which of them
/// are damaged, and whether the vault currently refuses writes over damage nobody has looked at
/// yet. Every branch the key-list screen can take lives here, reachable from a test with no
/// operating system in the loop.
/// </summary>
public sealed class VaultListModel(IKeyVault vault)
{
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
    /// right now. A direct read of <see cref="IKeyVault.WritesAreRefused"/> — not a flag this model
    /// caches and updates by hand — so it is correct even for a model instance kept alive across a
    /// lock and a later unlock that comes back freshly degraded: acknowledging damage under one
    /// unlock can never be mistaken for acknowledging damage under a different one, because nothing
    /// here remembers "acknowledged" past the vault's own state doing so.
    /// </summary>
    public bool WritesAreRefused => vault.WritesAreRefused;

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
    /// <see cref="WritesAreRefused"/> is not touched here at all: it reads straight through to
    /// <see cref="IKeyVault.WritesAreRefused"/>, which the vault itself keeps current across a
    /// refresh, an acknowledgement, and a lock/unlock cycle in between. There is nothing for this
    /// method to recompute.
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
                rows.Add(new KeyRow(reference, info, IsDamaged: false, NeedsExporting: needsExporting));
            }
            else if (damagedSet.Contains(reference))
            {
                rows.Add(new KeyRow(reference, Info: null, IsDamaged: true, NeedsExporting: false));
            }

            // Neither: the reference disappeared between reading References and reading the list
            // (a concurrent write on another handle to the same vault). Nothing true can be said
            // about it, so it gets no row, the same as a reference deleted outright.
        }

        Rows = rows;
        IsDegraded = damagedReferences.Count > 0;
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
    public Task AcknowledgeDamageAsync(CancellationToken cancellationToken) =>
        vault.AcknowledgeDamageAsync(cancellationToken);
}
