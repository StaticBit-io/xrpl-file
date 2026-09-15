namespace XrplFile.Keys;

/// <summary>Where a key came from, which is what decides what the application may offer to do with it.</summary>
public enum VaultEntryKind
{
    /// <summary>Born in this application for one publication. Its remaining reserve can be reclaimed later.</summary>
    Generated,

    /// <summary>Typed in by the user. It is an account they had before and will have afterwards.</summary>
    Imported,
}

/// <summary>
/// What happened when a throwaway account was closed. Kept on the entry rather than anywhere else
/// because the key outlives the account: a deleted address can be funded back into existence and
/// this seed still controls it, so the entry is the only place that can say the account it names is
/// gone and where its remainder went.
/// </summary>
/// <param name="ClosedAt">When the deletion was submitted.</param>
/// <param name="Destination">Where the remainder was sent. The person named it; nothing chose it.</param>
/// <param name="ArrivedDrops">What the deletion sent: the balance less its fee.</param>
/// <param name="TxHash">The deletion's transaction hash.</param>
public sealed record AccountClosure(DateTimeOffset ClosedAt, string Destination, long ArrivedDrops, string TxHash);

/// <summary>An entry on its way in. The vault assigns the reference and the creation time.</summary>
/// <param name="Algorithm">
/// The signing algorithm the seed was derived with (<c>XrplWallet.Ed25519</c> or
/// <c>XrplWallet.Secp256k1</c>), or <see langword="null"/> to mean the SDK's own default —
/// ed25519. Trailing and optional so every existing construction site keeps compiling.
/// </param>
public sealed record NewVaultEntry(
    VaultEntryKind Kind, string Label, string Network, string Address, string Secret, string? Algorithm = null);

/// <summary>
/// Everything about an entry except its secret. This is what listing returns: <see cref="IKeyVault.ListAsync"/>
/// still has to decrypt every entry to reach its metadata (D7 puts metadata inside the encrypted
/// blob), so this guarantees something narrower than "no decryption happens" — the secret never
/// escapes into caller code, into a binding, or into a log, because this shape simply has no
/// field to carry it in.
/// </summary>
/// <param name="Algorithm">
/// The signing algorithm the entry's key was derived with, or <see langword="null"/> when none was
/// recorded — which means ed25519, because every entry ever stored without this field was derived
/// that way. There is no migration: absence keeps meaning exactly what it always meant. Trailing
/// and optional so every existing construction site keeps compiling.
/// </param>
/// <param name="Closure">
/// What happened when the account this entry names was closed, or <see langword="null"/> while it
/// is still open — which is what every entry ever stored before this field existed means, since
/// none of them could have been closed by a build that could not yet record it. Trailing and
/// optional so every existing construction site keeps compiling.
/// </param>
public sealed record VaultEntryInfo(
    string Reference,
    VaultEntryKind Kind,
    string Label,
    string Network,
    string Address,
    DateTimeOffset CreatedAt,
    uint? LastKnownSequence,
    long? DeletableFromLedger,
    bool Exported,
    string? Algorithm = null,
    AccountClosure? Closure = null);

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
    bool? Exported = null,
    AccountClosure? Closure = null);
