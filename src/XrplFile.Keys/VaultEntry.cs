namespace XrplFile.Keys;

/// <summary>Where a key came from, which is what decides what the application may offer to do with it.</summary>
public enum VaultEntryKind
{
    /// <summary>Born in this application for one publication. Its remaining reserve can be reclaimed later.</summary>
    Generated,

    /// <summary>Typed in by the user. It is an account they had before and will have afterwards.</summary>
    Imported,
}

/// <summary>An entry on its way in. The vault assigns the reference and the creation time.</summary>
public sealed record NewVaultEntry(
    VaultEntryKind Kind, string Label, string Network, string Address, string Secret);

/// <summary>
/// Everything about an entry except its secret. This is what listing returns: <see cref="IKeyVault.ListAsync"/>
/// still has to decrypt every entry to reach its metadata (D7 puts metadata inside the encrypted
/// blob), so this guarantees something narrower than "no decryption happens" — the secret never
/// escapes into caller code, into a binding, or into a log, because this shape simply has no
/// field to carry it in.
/// </summary>
public sealed record VaultEntryInfo(
    string Reference,
    VaultEntryKind Kind,
    string Label,
    string Network,
    string Address,
    DateTimeOffset CreatedAt,
    uint? LastKnownSequence,
    long? DeletableFromLedger,
    bool Exported);

/// <summary>An entry with its secret. Only <see cref="IKeyVault.RevealAsync"/> produces one.</summary>
public sealed record VaultEntry(VaultEntryInfo Info, string Secret);

/// <summary>
/// The fields an entry can be edited to. Deliberately reaches neither the secret, the kind, nor
/// the address: those three are what the entry is, and an edit that could change them would make
/// a reference mean something else than it did when a publication recorded it.
/// </summary>
public sealed record VaultEntryEdit(
    string? Label = null,
    uint? LastKnownSequence = null,
    long? DeletableFromLedger = null,
    bool? Exported = null);
