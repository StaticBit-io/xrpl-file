namespace XrplFile.Keys;

/// <summary>What went wrong inside the vault, as a value a caller can switch on.</summary>
public enum VaultError
{
    /// <summary>The record could not be read as a record at all.</summary>
    Corrupt,

    /// <summary>The record's format version or KDF identifier is not one this build can open.</summary>
    Unsupported,

    /// <summary>An operation needing the data key was asked of a locked vault.</summary>
    Locked,

    /// <summary>No entry with that reference.</summary>
    NoSuchEntry,

    /// <summary>
    /// Another entry already holds this address on this network. <see cref="IKeyVault.StoreAsync"/>
    /// refuses rather than silently holding the same key twice — an import of a container or a bare
    /// seed the vault already has is a no-op the caller should be told about, not a second entry
    /// nobody asked for.
    /// </summary>
    DuplicateAddress,

    /// <summary>An entry failed its authentication tag: damaged bytes, or moved between references.</summary>
    EntryDamaged,

    /// <summary>Reading or writing the vault file failed.</summary>
    StorageFailed,

    /// <summary>
    /// A write was asked of a vault whose record failed its own integrity code when it was opened,
    /// and the damage has not been acknowledged. Every write recomputes that code, and the
    /// mismatch is the only witness there is to an entry removed from the file or to bytes edited
    /// under it — so erasing it is the user's decision, taken through
    /// <see cref="IKeyVault.AcknowledgeDamageAsync"/>. Reading a degraded vault is never refused.
    /// </summary>
    Degraded,

    /// <summary>
    /// An entry's recorded address does not match the address its own key derives to. Nothing on
    /// <see cref="IKeyVault.StoreAsync"/> checks the two against each other, so a mistyped or
    /// foreign address survives storage; this is where it is finally caught, before anything is
    /// built or signed for the wrong account.
    /// </summary>
    AddressMismatch,
}

public sealed class VaultException : Exception
{
    public VaultError Error { get; }

    public VaultException(VaultError error, string message) : base(message) => Error = error;

    public VaultException(VaultError error, string message, Exception inner)
        : base(message, inner) => Error = error;
}
