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
/// How a master-password change ended. It is an enumeration rather than a <see langword="bool"/>
/// because the change has two outcomes that both mean "the password is now the new one" and that a
/// caller must show differently — and a <see langword="bool"/> leaves the second unsayable.
/// </summary>
/// <remarks>
/// <para>
/// Three values rather than two flags, so that the combination which cannot occur — the password
/// unchanged and yet device unlock dropped — cannot be spelled. <see cref="WrongPassword"/> is
/// first, and so the default, because a value nobody set must never read as success.
/// </para>
/// <para>
/// Every other failure throws (design 6.2). A damaged record, a refused disk and a device store
/// that cannot answer are all conditions this type deliberately has no value for.
/// </para>
/// </remarks>
public enum VaultPasswordChange
{
    /// <summary>
    /// The old password does not unwrap the data key already on disk. Nothing was written, and
    /// this is the only outcome a caller may count against a failed-attempt limit.
    /// </summary>
    WrongPassword,

    /// <summary>The password was changed. Not one entry's bytes were touched.</summary>
    Changed,

    /// <summary>
    /// The password was changed, and "remember on this device" went with it. A fresh salt makes a
    /// fresh header, both wraps are bound to that header, and this machine could not re-seal the
    /// device wrap under it — so the wrap was dropped rather than left unopenable, and the store
    /// was asked to forget the secret behind it. The setting is off, only the user can turn it
    /// back on, and only a caller that was told can say so.
    /// </summary>
    ChangedAndDeviceUnlockDropped,
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

    /// <summary>Whether a vault file is present at this vault's path.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

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
    /// A password is usually changed because the old one leaked, so the change leaves nothing
    /// behind that the old one opens: the copy the durable write keeps beside the vault is removed
    /// once the new record is confirmed on disk.
    /// </remarks>
    /// <returns>
    /// <see cref="VaultPasswordChange.WrongPassword"/> for exactly one condition:
    /// <paramref name="oldPassword"/> does not unwrap the data key already on disk. Every other
    /// failure throws <see cref="VaultException"/> — a damaged record reported as a wrong password
    /// is how a disk fault becomes a user's fault.
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
    Task DisableDeviceUnlockAsync(CancellationToken cancellationToken);
}
