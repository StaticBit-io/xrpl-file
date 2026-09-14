namespace XrplFile.Keys;

/// <summary>
/// How an attempt to open a vault ended. The values are separated because callers must treat them
/// differently (design 9).
/// </summary>
/// <remarks>
/// Only <see cref="WrongPassword"/> may ever advance a caller's failed-attempt counter. A storage
/// outage or a damaged record counted as a wrong password lets a failing disk look like a user
/// error — and the moment a user is told they mistyped is exactly the moment that has to be true.
/// </remarks>
public enum VaultUnlockResult
{
    /// <summary>Opened, every entry verified.</summary>
    Success,

    /// <summary>The record is intact and the password does not unwrap the data key.</summary>
    WrongPassword,

    /// <summary>No record to open.</summary>
    VaultMissing,

    /// <summary>
    /// The record is damaged, or a password failed over a record whose integrity code already did
    /// not match. The two cannot be told apart, so it is never reported as "your password is not
    /// the problem".
    /// </summary>
    VaultCorrupt,

    /// <summary>
    /// The password opened the vault and something inside is damaged. The user is in;
    /// <see cref="IKeyVault.DamagedReferences"/> names the casualties.
    /// </summary>
    VaultDegraded,

    /// <summary>Format version or KDF identifier this build cannot open.</summary>
    VaultUnsupported,

    /// <summary>Storage refused a read or write. Transient; nothing can be concluded.</summary>
    VaultUnavailable,
}

/// <summary>
/// How a master-password change ended: whether it happened, and what a caller has to be told
/// about alongside it.
/// </summary>
/// <remarks>
/// <para>
/// It is a flag set rather than one value per outcome because the things worth saying are
/// independent of each other, and three of them can be true at once. Named combinations were
/// tried and do not scale: each new fact doubles the list, and the moment one combination is left
/// unnamed it becomes unsayable — which, for the damage flag below, would mean quietly losing the
/// only evidence there is.
/// </para>
/// <para>
/// <see cref="WrongPassword"/> is the empty set, and so the default, for two reasons: a value
/// nobody set must never read as success, and "nothing was written" is exactly the absence of
/// every flag here. <see cref="Changed"/> is set on every outcome where the new record reached the
/// disk, so <c>result == WrongPassword</c> and <c>!result.HasFlag(Changed)</c> are the same
/// question and either may be asked. The other three never appear without it.
/// </para>
/// <para>
/// Every other failure throws (design 6.2). A record that cannot be read as a record, a refused
/// disk and a device store that cannot answer are all conditions this type deliberately has no
/// value for. What does not throw is anything discovered about a change that is already committed:
/// a throw there would say "the password was not changed" about a change that is on disk, which
/// sends the caller back to a password the vault no longer has.
/// </para>
/// </remarks>
[Flags]
public enum VaultPasswordChange
{
    /// <summary>
    /// The old password does not unwrap the data key already on disk. Nothing was written, and
    /// this is the only outcome a caller may count against a failed-attempt limit.
    /// </summary>
    WrongPassword = 0,

    /// <summary>
    /// The password was changed and the new record is on disk. Not one entry's bytes were touched.
    /// Every other flag here qualifies this one and never appears alone.
    /// </summary>
    Changed = 1,

    /// <summary>
    /// "Remember on this device" went with the change. A fresh salt makes a fresh header, both
    /// wraps are bound to that header, and this machine could not re-seal the device wrap under it
    /// — so the wrap was dropped rather than left unopenable. The setting is off, only the user
    /// can turn it back on, and only a caller that was told can say so.
    /// </summary>
    DeviceUnlockDropped = 1 << 1,

    /// <summary>
    /// The copy the durable write kept beside the vault could not be removed — a file still open
    /// in an indexer or an antivirus is enough. The new password is what the vault opens with;
    /// what is left behind at <c>&lt;vault&gt;.bak</c> is a whole record that the old, and
    /// presumably leaked, password still opens, and which <see cref="IKeyVault.UnlockAsync"/>
    /// would serve if the vault file itself ever went missing. A caller has to say so and offer to
    /// remove it; retrying the change with the old password is the one thing it must not do,
    /// because that password is no longer the vault's.
    /// </summary>
    TheOldCopyRemains = 1 << 2,

    /// <summary>
    /// The record that was re-sealed did not match its own integrity code, and now it does: the
    /// change recomputed the code over the record as it stood, damage and all.
    /// </summary>
    /// <remarks>
    /// That code is the only witness there is to an entry removed from the file or to the file
    /// rolled back to an earlier version (design 11), so overwriting it is not merely losing
    /// evidence — it is the one act that destroys it. The change still goes ahead, because
    /// refusing to replace a leaked password over one damaged byte is the worse failure; this flag
    /// is what stops the evidence from disappearing silently when it does.
    /// <para>
    /// A caller must show it, and must not expect a second chance to: the record now matches the
    /// code computed over it, so the next unlock reports <see cref="VaultUnlockResult.Success"/>
    /// unless some entry's own tag also fails. This flag is the last moment at which anyone can
    /// learn that the file did not add up.
    /// </para>
    /// </remarks>
    TheRecordsIntegrityCodeDidNotMatch = 1 << 3,
}

/// <summary>
/// An encrypted store for the keys that sign publications. One password opens it; the keys inside
/// are handed back one at a time and only on request.
/// </summary>
/// <remarks>
/// Password buffers belong to the caller throughout. The vault reads them, derives from them, and
/// never stores them — and does not zero them either, because the caller usually still needs the
/// buffer for a confirmation comparison or a retry, and a callee that wipes its argument is a trap.
/// </remarks>
public interface IKeyVault
{
    /// <summary>Whether the data key is currently held in memory.</summary>
    bool IsUnlocked { get; }

    /// <summary>
    /// Every reference the record carries, damaged ones included. A caller compares a
    /// publication's key reference against this to say "this publication's key is gone" — the
    /// vault cannot report a casualty it has no record of.
    /// </summary>
    IReadOnlyCollection<string> References { get; }

    /// <summary>
    /// The references whose entries failed their authentication tag at the last unlock.
    /// Empty while the vault is locked.
    /// </summary>
    IReadOnlyList<string> DamagedReferences { get; }

    /// <summary>
    /// Whether a write against this vault would be refused right now with
    /// <see cref="VaultError.Degraded"/>. Set by the unlock that finds the record's own integrity
    /// code not matching its current contents, cleared by <see cref="AcknowledgeDamageAsync"/>, and
    /// always <see langword="false"/> while the vault is locked. A caller that wants to know whether
    /// writing would be refused reads this rather than catching <see cref="VaultError.Degraded"/>
    /// from an attempted write — the vault answers the question directly instead of a caller having
    /// to provoke it.
    /// </summary>
    bool WritesAreRefused { get; }

    /// <summary>
    /// Says that the damage reported by the unlock which opened this vault has been seen, and
    /// releases the writes that are refused until it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is needed for one kind of damage only: a record whose integrity code does not match its
    /// own contents. Every write recomputes that code, and the mismatch is the only witness there
    /// is to an entry removed from the file or to bytes edited under the code that covers them —
    /// a per-entry tag proves nothing about an entry that is no longer there. So the first write
    /// after such an unlock is refused with <see cref="VaultError.Degraded"/> rather than allowed
    /// to erase the evidence, and this is the call that says the user has been told and the vault
    /// may carry on.
    /// </para>
    /// <para>
    /// Reading needs none of this: <see cref="ListAsync"/>, <see cref="RevealAsync"/> and
    /// <see cref="ExportAsync"/> all work on a degraded vault untouched, because rescuing the keys
    /// that are still intact is exactly what <see cref="VaultUnlockResult.VaultDegraded"/> exists
    /// to allow. Nothing is written here either, so the file keeps the mismatch until a write
    /// genuinely needs to happen: a vault locked and opened again reports
    /// <see cref="VaultUnlockResult.VaultDegraded"/> once more.
    /// </para>
    /// </remarks>
    /// <exception cref="VaultException"><see cref="VaultError.Locked"/> on a locked vault.</exception>
    Task AcknowledgeDamageAsync(CancellationToken cancellationToken);

    /// <summary>Whether a vault file is present at this vault's path.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the record at this vault's path carries a device wrap — not whether "unlock with
    /// device" would actually succeed. Answered by reading and parsing the record, the same way
    /// <see cref="ExistsAsync"/> answers "is there a file here": no derivation runs, no data key is
    /// produced, and the vault's locked state never changes because of this call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately not the same question <see cref="UnlockWithDeviceAsync"/> answers. A
    /// wrap can be present while the platform secret store has lost the secret it was sealed
    /// under, or holds a secret that no longer opens it — both leave this <see langword="true"/>
    /// while a real attempt would return <see cref="VaultUnlockResult.VaultMissing"/>. That gap is
    /// the deliberate cost of asking a question with no side effect: see the type's remarks on why
    /// no combination of unlock and lock can answer it exactly without risking the vault's state.
    /// </para>
    /// <para>
    /// Returns <see langword="false"/> when there is no file, when the file does not parse, or
    /// when this build cannot open the record's format — none of those leave anything to unlock
    /// with, so none of them are a reason to offer the option.
    /// </para>
    /// </remarks>
    Task<bool> HasDeviceWrapAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a new, empty vault and leaves it unlocked. Throws
    /// <see cref="InvalidOperationException"/> rather than overwriting one that is already there.
    /// </summary>
    Task CreateAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the vault from disk. A failed attempt leaves an already-open vault open: confirming a
    /// password and opening a vault are the same call, and a mistyped confirmation must not end
    /// the session it was only meant to re-check.
    /// </summary>
    Task<VaultUnlockResult> UnlockAsync(ReadOnlyMemory<byte> password, CancellationToken cancellationToken);

    /// <summary>Forgets the data key and the cached record. Waits for any operation in flight.</summary>
    Task LockAsync();

    /// <summary>Everything about every entry except its secret.</summary>
    Task<IReadOnlyList<VaultEntryInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds an entry and returns the reference the vault assigned to it. Throws
    /// <see cref="VaultException"/> with <see cref="VaultError.DuplicateAddress"/> when another
    /// entry already holds this same address on this same network — an import that hands back a
    /// key already in the vault is refused rather than filed under a second reference (design
    /// section 8).
    /// </summary>
    Task<string> StoreAsync(NewVaultEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// The only call that produces a secret. <paramref name="reason"/> is carried for diagnostics
    /// and is never written to disk.
    /// </summary>
    Task<VaultEntry> RevealAsync(string reference, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Reveals one entry, seals it into a portable <see cref="BackupContainer"/> under
    /// <paramref name="containerPassword"/>, and marks the entry <c>Exported</c> — the guaranteed
    /// path design section 8 and D10 require: per-key export is the only way back from a forgotten
    /// master password, so nothing may reveal a secret for export without also recording that it
    /// happened. <see cref="BackupContainer.Pack"/> and <see cref="BackupContainer.TryUnpack"/> remain
    /// usable on their own for a caller that already holds an entry from <see cref="RevealAsync"/>;
    /// this method exists so that the common case — export, then mark — cannot be split into two
    /// calls a caller might forget the second half of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Exported</c> is set only after the container has been sealed: if
    /// <see cref="BackupContainer.Pack"/> throws, the entry is untouched — marking an entry
    /// exported before the container that proves it exists on disk would be a lie the vault told
    /// about itself.
    /// </para>
    /// <para>
    /// The reverse failure — the container is sealed but the write that records <c>Exported</c>
    /// fails — still returns the container. The container is the operation's actual deliverable and
    /// is already correct and complete; discarding it because a one-bit metadata flag could not be
    /// persisted would make the one guaranteed recovery path unreliable exactly when storage is
    /// already misbehaving. The entry is left exactly as it was on disk (<c>Exported</c> unset), so
    /// a caller that lists entries afterwards is nagged to export again — a false negative, which is
    /// the safe direction for this flag to be wrong in.
    /// </para>
    /// </remarks>
    /// <exception cref="VaultException">
    /// <see cref="VaultError.Locked"/> on a locked vault, <see cref="VaultError.NoSuchEntry"/> for an
    /// unknown reference, <see cref="VaultError.EntryDamaged"/> for an entry that fails its own
    /// authentication tag — the same three conditions <see cref="RevealAsync"/> reports, since this
    /// method reveals the same way. Whatever <see cref="BackupContainer.Pack"/> itself throws
    /// propagates unchanged when it throws before the container exists.
    /// </exception>
    Task<byte[]> ExportAsync(
        string reference, ReadOnlyMemory<byte> containerPassword, CancellationToken cancellationToken);

    /// <summary>Changes an entry's mutable metadata. Reaches neither the secret, the kind, nor the address.</summary>
    Task UpdateAsync(string reference, VaultEntryEdit edit, CancellationToken cancellationToken);

    /// <summary>Removes an entry. Succeeds silently when there is no such entry.</summary>
    Task DeleteAsync(string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the vault using whatever secret "remember on this device" left behind, and no
    /// password. Returns <see cref="VaultUnlockResult.VaultMissing"/> when the record has no
    /// device wrap, the store holds no secret, or the wrap does not open with the secret it does
    /// hold — all three mean there is nothing left to open with, and the caller's job is to turn
    /// the setting off. Returns <see cref="VaultUnlockResult.VaultUnavailable"/> when the store
    /// itself failed; that is transient, and nothing can be concluded from it.
    /// <see cref="VaultUnlockResult.WrongPassword"/> is unreachable here: no password is involved,
    /// and a caller counting it would advance a failed-attempt counter for a path that has none.
    /// </summary>
    Task<VaultUnlockResult> UnlockWithDeviceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Changes the master password. The old password is verified against the wrap already on
    /// disk; the new one wraps the same data key under a fresh salt. Not one entry's bytes are
    /// touched — that is the entire reason the vault wraps a data key rather than encrypting
    /// entries directly under a password-derived key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A password is usually changed because the old one leaked, so the change leaves nothing
    /// behind that the old one opens: the copy the durable write keeps beside the vault is removed
    /// once the new record is confirmed on disk.
    /// </para>
    /// <para>
    /// This method reads the file itself and works on a vault that may well be locked, so it runs
    /// none of the per-entry verification an unlock runs. It does check the record's own integrity
    /// code, because resealing is what destroys a mismatch: it proceeds either way and reports
    /// <see cref="VaultPasswordChange.TheRecordsIntegrityCodeDidNotMatch"/>, which is the last
    /// moment at which that damage can be learned of at all.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see cref="VaultPasswordChange.WrongPassword"/> — the empty flag set — for exactly one
    /// condition: <paramref name="oldPassword"/> does not unwrap the data key already on disk. Any
    /// other result carries <see cref="VaultPasswordChange.Changed"/> and whichever of the three
    /// qualifying flags apply. Every other failure throws <see cref="VaultException"/> — a damaged
    /// record reported as a wrong password is how a disk fault becomes a user's fault.
    /// </returns>
    Task<VaultPasswordChange> ChangePasswordAsync(
        ReadOnlyMemory<byte> oldPassword, ReadOnlyMemory<byte> newPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Turns on "remember on this device": gets or creates this machine's secret and wraps the
    /// already-unlocked data key under it. Asks for no password, because the data key is already
    /// in memory. Returns <see langword="false"/> and writes nothing when the device secret store
    /// refuses to create a secret; a store that fails rather than refuses throws
    /// <see cref="VaultException"/> with <see cref="VaultError.StorageFailed"/>.
    /// </summary>
    Task<bool> EnableDeviceUnlockAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Turns off "remember on this device": clears the device wrap from the record, then asks the
    /// store to drop its secret. The password wrap is never touched, so the vault stays openable
    /// either way. Succeeds silently when the option was already off.
    /// </summary>
    /// <remarks>
    /// The store keeps one secret per vault and is asked about this one only (4.2), so turning the
    /// setting off here can never reach the secret of another vault on the same machine. Even that
    /// secret is dropped only once this record's own wrap has opened under it: a path outlives the
    /// file that was at it, so a vault copied over one where something else enrolled a secret
    /// clears its own field and leaves that secret exactly where it is.
    /// </remarks>
    Task DisableDeviceUnlockAsync(CancellationToken cancellationToken);
}
