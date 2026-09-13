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

    /// <summary>Adds an entry and returns the reference the vault assigned to it.</summary>
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
}
